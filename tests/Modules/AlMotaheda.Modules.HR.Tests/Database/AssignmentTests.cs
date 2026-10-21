using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.HR.Tests.Database;

/// <summary>
/// Salaried employees on projects over time, on SQL Server: one open stay per project, no stay starting before the
/// last one ended, ended rows kept, and "who was on the project that day" for investigations.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class AssignmentTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task An_employee_is_on_a_project_once_at_a_time_and_the_history_says_who_was_there_on_a_day()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (_, hr) = await api.CreateSignedInUserAsync(executive, "hr");
        var (_, engineer) = await api.CreateSignedInUserAsync(executive, "engineer");
        var project = (await Created(api, executive, "/api/projects", Project("HR-ASG-01", executive.UserId))).Id();
        var other = (await Created(api, executive, "/api/projects", Project("HR-ASG-02", executive.UserId))).Id();
        var department = (await Created(api, hr, "/api/hr/departments", new { nameAr = "الحركة", nameEn = "Transport" })).Id();
        var hany = (await Created(api, hr, "/api/hr/employees", Employee("Hany", "29005051234567", department))).Id();

        // Assigned as the project's driver from the first of last month; the same stay can't be opened twice.
        var start = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1);
        var stay = await Created(api, hr, $"/api/hr/employees/{hany}/assignments", new { projectId = project, role = "Driver", startDate = Iso(start), notes = "Pickup 201" });
        Assert.Equal(("Driver", "HR-ASG-01", Iso(start)), (stay.GetProperty("role").GetString(), stay.GetProperty("projectCode").GetString(), stay.GetProperty("startDate").GetString()));
        Assert.Equal(JsonValueKind.Null, stay.GetProperty("endDate").ValueKind);
        Assert.Equal("already_assigned", await CodeOf(await api.SendAsync(HttpMethod.Post, $"/api/hr/employees/{hany}/assignments", hr, new { projectId = project, role = "Guard", startDate = Iso(start.AddDays(3)) })));
        var unknownProject = await api.SendAsync(HttpMethod.Post, $"/api/hr/employees/{hany}/assignments", hr, new { projectId = 999_999, role = "Driver" });
        Assert.Equal(HttpStatusCode.BadRequest, unknownProject.StatusCode);
        Assert.Equal(["projectId"], await Fields(unknownProject));

        // The engineer has no HR access; the HR user can't end a stay with an end before its start.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, $"/api/hr/employees/{hany}/assignments", engineer)).StatusCode);
        var tooEarly = await api.SendAsync(HttpMethod.Post, $"/api/hr/employees/{hany}/assignments/{stay.Id()}/end", hr, new { endDate = Iso(start.AddDays(-1)), reason = "Typo", rowVersion = stay.RowVersion() });
        Assert.Equal(HttpStatusCode.BadRequest, tooEarly.StatusCode);

        // Ended on the 10th; a new stay on the same project can't start before that, and starts fine after it.
        var end = start.AddDays(9);
        var ended = await Json(await api.SendAsync(HttpMethod.Post, $"/api/hr/employees/{hany}/assignments/{stay.Id()}/end", hr, new { endDate = Iso(end), reason = "Moved to the other site", rowVersion = stay.RowVersion() }));
        Assert.Equal((Iso(end), "Moved to the other site"), (ended.GetProperty("endDate").GetString(), ended.GetProperty("endReason").GetString()));
        Assert.Equal("assignment_already_ended", await CodeOf(await api.SendAsync(HttpMethod.Post, $"/api/hr/employees/{hany}/assignments/{stay.Id()}/end", hr, new { endDate = Iso(end), reason = "Again", rowVersion = ended.RowVersion() })));
        var overlapping = await api.SendAsync(HttpMethod.Post, $"/api/hr/employees/{hany}/assignments", hr, new { projectId = project, role = "Driver", startDate = Iso(end.AddDays(-2)) });
        Assert.Equal(HttpStatusCode.BadRequest, overlapping.StatusCode);
        Assert.Equal(["startDate"], await Fields(overlapping));
        var onOther = await Created(api, hr, $"/api/hr/employees/{hany}/assignments", new { projectId = other, role = "Driver", startDate = Iso(end.AddDays(1)) });
        var back = await Created(api, hr, $"/api/hr/employees/{hany}/assignments", new { projectId = project, role = "Supervisor", startDate = Iso(end.AddDays(5)) });

        // The employee's list: open stays only by default, the whole history on request.
        var open = (await Json(await api.SendAsync(HttpMethod.Get, $"/api/hr/employees/{hany}/assignments", hr))).GetProperty("items");
        Assert.Equal([back.Id(), onOther.Id()], open.EnumerateArray().Select(row => row.Id()));
        var all = (await Json(await api.SendAsync(HttpMethod.Get, $"/api/hr/employees/{hany}/assignments?includeEnded=true", hr))).GetProperty("items");
        Assert.Equal(3, all.GetArrayLength());

        // Who was on the project on a day: during the first stay, after it ended, and today.
        Assert.Equal([stay.Id()], (await OnDate(api, hr, project, start.AddDays(5))).Select(row => row.Id()));
        Assert.Empty(await OnDate(api, hr, project, end.AddDays(2)));
        Assert.Equal([back.Id()], (await OnDate(api, hr, project, DateOnly.FromDateTime(DateTime.UtcNow))).Select(row => row.Id()));
        Assert.Equal([onOther.Id()], (await OnDate(api, hr, other, end.AddDays(3))).Select(row => row.Id()));
        Assert.Equal(HttpStatusCode.BadRequest, (await api.SendAsync(HttpMethod.Get, "/api/hr/assignments?projectId=0", hr)).StatusCode);
        Assert.Equal(2, (await Json(await api.SendAsync(HttpMethod.Get, "/api/hr/assignments", hr))).GetProperty("items").GetArrayLength());
    }

    private static object Project(string code, int managerUserId) => new
    {
        code,
        nameEn = $"Project {code}",
        nameAr = $"مشروع {code}",
        clientNameEn = "Badr Urban Development Co.",
        locationEn = "Badr City, Cairo",
        siteLatitude = 30.1395m,
        siteLongitude = 31.7085m,
        siteRadiusMeters = 500,
        startDate = "2025-03-01",
        endDate = "2026-12-31",
        status = "Active",
        managerUserId,
    };

    private static object Employee(string name, string nationalId, int departmentId) => new
    {
        nameAr = $"{name} بالعربية", nameEn = name, nationalId, jobTitle = "Driver", departmentId, kind = "Staff", hireDate = "2024-06-01",
    };

    private static async Task<List<JsonElement>> OnDate(AccessApi api, Session session, int projectId, DateOnly on)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/hr/assignments?projectId={projectId}&on={Iso(on)}", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return (await Json(response)).GetProperty("items").EnumerateArray().ToList();
    }

    private static async Task<JsonElement> Created(AccessApi api, Session session, string path, object body)
    {
        var response = await api.SendAsync(HttpMethod.Post, path, session, body);
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    private static async Task<List<string>> Fields(HttpResponseMessage response) =>
        (await Json(response)).GetProperty("errors").EnumerateObject().Select(field => field.Name).ToList();

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
