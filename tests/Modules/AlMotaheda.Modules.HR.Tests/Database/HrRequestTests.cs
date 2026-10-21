using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.HR.Tests.Database;

/// <summary>
/// Section 12.D on SQL Server: an employee writes to HR from their own account with evidence; HR asks for more,
/// the employee answers, HR decides; nobody decides their own request; nobody reads another employee's; an
/// approved request changes no payroll by itself.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class HrRequestTests(SqlServerFixture database)
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, .. new byte[40], 0xFF, 0xD9];

    [DatabaseFact]
    public async Task An_employee_asks_hr_with_evidence_hr_asks_back_and_decides_and_nobody_sees_or_decides_for_another()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (hrUser, hr) = await api.CreateSignedInUserAsync(executive, "hr");
        var (engineerUser, engineer) = await api.CreateSignedInUserAsync(executive, "engineer");
        var (otherUser, other) = await api.CreateSignedInUserAsync(executive, "engineer");
        var (_, stranger) = await api.CreateSignedInUserAsync(executive, "staff");
        var department = (await Created(api, hr, "/api/hr/departments", new { nameAr = "الموقع", nameEn = "Site" })).Id();
        var mahmoud = (await Created(api, hr, "/api/hr/employees", Employee("Mahmoud", "29005051234567", department, engineerUser.Id()))).Id();
        await Created(api, hr, "/api/hr/employees", Employee("Samir", "29006061234567", department, otherUser.Id()));
        var hrEmployee = (await Created(api, hr, "/api/hr/employees", Employee("Hoda", "29007071234567", department, hrUser.Id()))).Id();
        var month = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM");

        // A login not linked to an employee has no HR self-service.
        Assert.Equal("not_an_employee", await CodeOf(await api.SendAsync(HttpMethod.Get, "/api/hr/my/requests", stranger)));

        // The engineer uploads a photo of the medical note and asks for two days' leave; the same form twice is one request.
        var evidence = await UploadAsync(api, engineer);
        var key = Guid.NewGuid();
        var body = new { type = "Leave", subject = "Sick leave", details = "Two days, see the medical note", fromDate = "2026-10-12", toDate = "2026-10-13", attachmentIds = new[] { evidence }, idempotencyKey = key };
        var submitted = await api.SendAsync(HttpMethod.Post, "/api/hr/my/requests", engineer, body);
        await EnsureStatus(submitted, HttpStatusCode.Created);
        var request = await Json(submitted);
        Assert.Equal(("Submitted", "Leave", mahmoud, 1), (request.GetProperty("status").GetString(), request.GetProperty("type").GetString(), request.GetProperty("employeeId").GetInt32(), request.GetProperty("attachments").GetArrayLength()));
        Assert.StartsWith("HRQ-", request.GetProperty("number").GetString());
        Assert.Equal((HttpStatusCode.OK, request.Id()), ((await api.SendAsync(HttpMethod.Post, "/api/hr/my/requests", engineer, body)).StatusCode, (await Json(await api.SendAsync(HttpMethod.Post, "/api/hr/my/requests", engineer, body))).Id()));
        var missing = await api.SendAsync(HttpMethod.Post, "/api/hr/my/requests", engineer, new { type = "Leave", subject = "x", details = "y", idempotencyKey = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("fromDate", await Fields(missing));

        // Confidentiality: the other employee sees nothing of it; the evidence streams to its uploader and to HR only.
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, $"/api/hr/my/requests/{request.Id()}", other)).StatusCode);
        Assert.Equal(0, (await Json(await api.SendAsync(HttpMethod.Get, "/api/hr/my/requests", other))).GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, $"/api/hr/attachments/{evidence}/content", other)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, $"/api/hr/attachments/{evidence}/content", hr)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, $"/api/hr/attachments/{evidence}/content", engineer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, "/api/hr/requests", engineer)).StatusCode);

        // The employee can't decide their own request: no hr right at all, and even an HR user never decides their own.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/hr/requests/{request.Id()}/approve", engineer, new { note = "Yes", rowVersion = request.RowVersion() })).StatusCode);
        var own = await Created(api, hr, "/api/hr/my/requests", new { type = "PayrollQuestion", subject = "Allowance", details = "Why was the allowance 900?", idempotencyKey = Guid.NewGuid() });
        Assert.Equal("own_record", await CodeOf(await api.SendAsync(HttpMethod.Post, $"/api/hr/requests/{own.Id()}/start-review", hr, new { rowVersion = own.RowVersion() })));
        Assert.Equal(hrEmployee, own.GetProperty("employeeId").GetInt32());

        // HR takes it, asks for the note's date; the employee answers with another photo; it is back in HR's queue.
        var queue = await Json(await api.SendAsync(HttpMethod.Get, "/api/hr/requests?status=Submitted", hr));
        Assert.Equal(2, queue.GetProperty("countsByStatus").GetProperty("Submitted").GetInt32());
        var reviewing = await Step(api, hr, request, "start-review");
        Assert.Equal("UnderReview", reviewing.GetProperty("status").GetString());
        var asked = await Step(api, hr, reviewing, "request-info", new { text = "Which date is on the medical note?" });
        Assert.Equal(("NeedsInfo", 1), (asked.GetProperty("status").GetString(), asked.GetProperty("messages").GetArrayLength()));
        Assert.True(asked.GetProperty("messages")[0].GetProperty("fromHr").GetBoolean());
        var inbox = await Json(await api.SendAsync(HttpMethod.Get, "/api/inbox", engineer));
        Assert.Contains(inbox.GetProperty("items").EnumerateArray(), item => item.GetProperty("recordType").GetString() == "HrRequest" && item.GetProperty("reason").GetString() == "Returned");

        var mine = await Json(await api.SendAsync(HttpMethod.Get, $"/api/hr/my/requests/{request.Id()}", engineer));
        Assert.Equal("NeedsInfo", mine.GetProperty("status").GetString());
        var more = await UploadAsync(api, engineer);
        var answered = await Json(await api.SendAsync(HttpMethod.Post, $"/api/hr/my/requests/{request.Id()}/messages", engineer, new { text = "The 12th, photo attached", attachmentIds = new[] { more }, rowVersion = mine.RowVersion() }));
        Assert.Equal(("Submitted", 2, 2), (answered.GetProperty("status").GetString(), answered.GetProperty("messages").GetArrayLength(), answered.GetProperty("attachments").GetArrayLength()));
        Assert.Equal(more, answered.GetProperty("messages")[1].GetProperty("attachments")[0].Id());
        Assert.Contains((await Json(await api.SendAsync(HttpMethod.Get, "/api/inbox", hr))).GetProperty("items").EnumerateArray(), item => item.GetProperty("recordType").GetString() == "HrRequest" && item.GetProperty("recordId").GetInt32() == request.Id());

        // The HR role approves nothing (hr VCEF); the Executive approves with a note. Final; no payroll input appeared.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/hr/requests/{request.Id()}/approve", hr, new { note = "OK", rowVersion = answered.RowVersion() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.SendAsync(HttpMethod.Post, $"/api/hr/requests/{request.Id()}/approve", executive, new { rowVersion = answered.RowVersion() })).StatusCode);
        var approved = await Step(api, executive, answered, "approve", new { note = "Two days of sick leave recorded" });
        Assert.Equal(("Approved", false, "Two days of sick leave recorded"), (approved.GetProperty("status").GetString(), approved.GetProperty("isOpen").GetBoolean(), approved.GetProperty("decisionNote").GetString()));
        Assert.Equal("invalid_status", await CodeOf(await api.SendAsync(HttpMethod.Post, $"/api/hr/requests/{request.Id()}/reject", executive, new { note = "No", rowVersion = approved.RowVersion() })));
        Assert.Equal("invalid_status", await CodeOf(await api.SendAsync(HttpMethod.Post, $"/api/hr/my/requests/{request.Id()}/messages", engineer, new { text = "Thanks", rowVersion = approved.RowVersion() })));
        Assert.Empty((await Json(await api.SendAsync(HttpMethod.Get, $"/api/hr/payroll-inputs?month={month}&employeeId={mahmoud}", executive))).EnumerateArray());

        // The employee's own list and the HR counts agree; the employee's home page section says one approved.
        var list = await Json(await api.SendAsync(HttpMethod.Get, "/api/hr/my/requests", engineer));
        Assert.Equal((1, 1), (list.GetProperty("totalCount").GetInt32(), list.GetProperty("countsByStatus").GetProperty("Approved").GetInt32()));
        var overview = await Json(await api.SendAsync(HttpMethod.Get, "/api/me/overview", engineer));
        var hrSection = Assert.Single(overview.GetProperty("sections").EnumerateArray(), section => section.GetProperty("section").GetString() == "Hr");
        Assert.Equal(1m, hrSection.GetProperty("figures").GetProperty("requestsApproved").GetDecimal());
        Assert.Empty((await Json(await api.SendAsync(HttpMethod.Get, "/api/hr/my/referrals", engineer))).GetProperty("items").EnumerateArray());
    }

    private static object Employee(string name, string nationalId, int departmentId, int userId) => new
    {
        nameAr = $"{name} بالعربية", nameEn = name, nationalId, jobTitle = "Engineer", departmentId, kind = "Engineer", hireDate = "2024-06-01", userId,
    };

    private static async Task<int> UploadAsync(AccessApi api, Session session)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(Guid.NewGuid().ToString()), "idempotencyKey");
        var file = new ByteArrayContent(Jpeg);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "file", "note.jpg");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/hr/my/attachments") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        var response = await api.App.Client.SendAsync(request);
        await EnsureStatus(response, HttpStatusCode.Created);
        var attachment = await Json(response);
        Assert.Equal(("image/jpeg", (long)Jpeg.Length), (attachment.GetProperty("contentType").GetString(), attachment.GetProperty("sizeBytes").GetInt64()));
        return attachment.Id();
    }

    private static async Task<JsonElement> Step(AccessApi api, Session session, JsonElement request, string step, object? extra = null)
    {
        var body = JsonSerializer.SerializeToElement(extra ?? new { }).EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value);
        body["rowVersion"] = request.RowVersion();
        var response = await api.SendAsync(HttpMethod.Post, $"/api/hr/requests/{request.Id()}/{step}", session, body);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    private static async Task<JsonElement> Created(AccessApi api, Session session, string path, object body)
    {
        var response = await api.SendAsync(HttpMethod.Post, path, session, body);
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    private static async Task<List<string>> Fields(HttpResponseMessage response) =>
        (await Json(response)).GetProperty("errors").EnumerateObject().Select(field => field.Name).ToList();
}
