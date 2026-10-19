using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using Microsoft.EntityFrameworkCore;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Core.CoreApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Planning.PlanningApi;

namespace AlMotaheda.Modules.Projects.Tests.Database.Planning;

/// <summary>Daily site reports: submit, return, fix, review, end to end on SQL Server.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class DailyReportWorkflowTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task A_report_is_written_by_its_author_reviewed_by_the_manager_and_holds_no_money()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("BDR-DAY-001");
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Delta Steel");
        var reports = $"/api/projects/{cast.ProjectId}/daily-reports";
        var day = Date(CompanyToday.AddDays(-1));

        // A supply delivered that day is listed on the report without any price.
        await api.RecordAsync(cast.Engineer, cast.ProjectId, SupplyBody(supplier, 4m, 38_000m, "Credit", new() { ["expenseDate"] = day, ["unit"] = "Ton" }));

        var report = await ExpectAsync(await api.PostAsync(cast.Engineer, reports, new
        {
            reportDate = day,
            workersPresent = 14,
            weather = "Sunny",
            lines = new object[]
            {
                new { workType = "Concrete", description = "Poured the ground floor slab", unit = "M3", quantity = 12.5m, performedBy = "Internal", workersCount = 8 },
                new { workType = "Brickwork", description = "External walls, east side", unit = "M2", quantity = 300m, performedBy = "Subcontractor", subcontractorName = "Al-Nour Contracting" },
            },
            materials = new[] { new { materialName = "Cement", unit = "Bag", quantity = 80m } },
            equipment = new[] { new { equipmentName = "Concrete pump", hours = 6.5m } },
        }), HttpStatusCode.Created);
        Assert.Equal("Submitted", report.Status());
        Assert.Equal(2, report.GetProperty("lines").GetArrayLength());
        var received = Assert.Single(report.GetProperty("materialsReceived").EnumerateArray());
        Assert.Equal(4m, received.Money("quantity"));
        Assert.False(received.TryGetProperty("unitPrice", out _));
        Assert.False(received.TryGetProperty("amount", out _));

        var again = await api.PostAsync(cast.Engineer, reports, new { reportDate = day, issues = "Rain stopped work" });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("report_exists", await CodeOf(again));

        // The author has no site Approve; the manager returns it with a note.
        var path = $"{reports}/{report.Id()}";
        Assert.Equal(HttpStatusCode.Forbidden, (await api.PostAsync(cast.Engineer, $"{path}/review", new { rowVersion = report.RowVersion() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync(cast.Manager, $"{path}/return", new { rowVersion = report.RowVersion() })).StatusCode);
        var returned = await ExpectAsync(
            await api.PostAsync(cast.Manager, $"{path}/return", new { note = "Add the slab area", rowVersion = report.RowVersion() }), HttpStatusCode.OK);
        Assert.Equal("Returned", returned.Status());

        var notSubmitted = await api.PostAsync(cast.Manager, $"{path}/review", new { rowVersion = returned.RowVersion() });
        Assert.Equal(HttpStatusCode.Conflict, notSubmitted.StatusCode);
        Assert.Equal("invalid_status", await CodeOf(notSubmitted));

        var fixedReport = await ExpectAsync(await api.PutAsync(cast.Engineer, path, new
        {
            workersPresent = 14,
            lines = new[] { new { workType = "Concrete", description = "Poured the ground floor slab, 250 m2", unit = "M3", quantity = 12.5m, performedBy = "Internal" } },
            rowVersion = returned.RowVersion(),
        }), HttpStatusCode.OK);
        Assert.Equal("Submitted", fixedReport.Status());
        Assert.Single(fixedReport.GetProperty("lines").EnumerateArray());

        var reviewed = await ExpectAsync(await api.PostAsync(cast.Manager, $"{path}/review", new { rowVersion = fixedReport.RowVersion() }), HttpStatusCode.OK);
        Assert.Equal("Reviewed", reviewed.Status());
        Assert.Equal(cast.Manager.UserId, reviewed.GetProperty("reviewer").GetProperty("userId").GetInt32());

        // Reviewed is final.
        var late = await api.PutAsync(cast.Engineer, path, new { issues = "Late note", rowVersion = reviewed.RowVersion() });
        Assert.Equal(HttpStatusCode.Conflict, late.StatusCode);
        Assert.Equal("invalid_status", await CodeOf(late));

        var list = await api.GetOkAsync(cast.Accountant, $"{reports}?status=Reviewed");
        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());
    }

    [DatabaseFact]
    public async Task A_report_sent_again_from_the_offline_queue_is_stored_once()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("BDR-DAY-OFF");
        var reports = $"/api/projects/{cast.ProjectId}/daily-reports";
        var key = Guid.NewGuid().ToString();
        var writtenAt = DateTime.UtcNow.AddHours(-5);
        var body = new { reportDate = Date(CompanyToday), issues = "Written with no signal on site", idempotencyKey = key, writtenAt };

        var first = await ExpectAsync(await api.PostAsync(cast.Engineer, reports, body), HttpStatusCode.Created);
        Assert.Equal(writtenAt, first.GetProperty("writtenAt").GetDateTime().ToUniversalTime(), TimeSpan.FromMilliseconds(5));

        // The answer was lost; the phone sends the same report again: the first one comes back, nothing new is stored.
        var again = await ExpectAsync(await api.PostAsync(cast.Engineer, reports, body), HttpStatusCode.OK);
        Assert.Equal(first.Id(), again.Id());
        var list = await api.GetOkAsync(cast.Engineer, reports);
        Assert.Equal(1, list.GetProperty("items").GetArrayLength());

        // The same key for another day is a different report: refused, so nothing is overwritten.
        var reused = await api.PostAsync(cast.Engineer, reports, new { reportDate = Date(CompanyToday.AddDays(-1)), issues = "Other day", idempotencyKey = key });
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        Assert.Equal("idempotency_key_reused", await CodeOf(reused));

        // Without a key the old rule stays: one report per author, project and day.
        var twice = await api.PostAsync(cast.Engineer, reports, new { reportDate = Date(CompanyToday), issues = "Second try without a key" });
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal("report_exists", await CodeOf(twice));

        // Wrong values are refused with field errors.
        var bad = await api.PostAsync(cast.Engineer, reports,
            new { reportDate = Date(CompanyToday.AddDays(-2)), issues = "x", idempotencyKey = "abc", writtenAt = DateTime.UtcNow.AddDays(-40) });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var fields = (await Json(bad)).GetProperty("errors").EnumerateObject().Select(field => field.Name).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["idempotencyKey", "writtenAt"], fields);
    }

    [DatabaseFact]
    public async Task A_repeat_of_a_stored_report_comes_back_even_when_its_day_is_now_too_old()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("BDR-DAY-003");
        var reports = $"/api/projects/{cast.ProjectId}/daily-reports";
        var key = Guid.NewGuid().ToString();
        var stored = await ExpectAsync(
            await api.PostAsync(cast.Engineer, reports, new { reportDate = Date(CompanyToday.AddDays(-1)), issues = "Sent from site", idempotencyKey = key }),
            HttpStatusCode.Created);

        // The answer was lost and the phone only retries ten days later: the stored report comes back (QA review M3),
        // not "the date is too old", which would leave the phone's copy and its photos stuck.
        var tenDaysAgo = CompanyToday.AddDays(-10);
        await api.QueryAsync(db => db.DailyReports.Where(report => report.Id == stored.Id()).ExecuteUpdateAsync(set => set.SetProperty(report => report.ReportDate, tenDaysAgo)));
        var late = await ExpectAsync(
            await api.PostAsync(cast.Engineer, reports, new { reportDate = Date(tenDaysAgo), issues = "Sent from site", idempotencyKey = key }),
            HttpStatusCode.OK);
        Assert.Equal(stored.Id(), late.Id());

        // A new report for that day is still refused.
        var fresh = await api.PostAsync(cast.Engineer, reports, new { reportDate = Date(tenDaysAgo.AddDays(-1)), issues = "Too old", idempotencyKey = Guid.NewGuid().ToString() });
        Assert.Equal(HttpStatusCode.BadRequest, fresh.StatusCode);
    }

    [DatabaseFact]
    public async Task A_report_of_a_project_you_are_not_on_answers_404()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("BDR-DAY-002");
        var (_, outsider) = await api.CreateSignedInUserAsync(cast.Executive, "engineer");
        var report = await ExpectAsync(
            await api.PostAsync(cast.Engineer, $"/api/projects/{cast.ProjectId}/daily-reports", new { reportDate = Date(CompanyToday), issues = "Concrete delivery late" }),
            HttpStatusCode.Created);

        var read = await api.SendAsync(HttpMethod.Get, $"/api/projects/{cast.ProjectId}/daily-reports/{report.Id()}", outsider);
        var write = await api.PostAsync(outsider, $"/api/projects/{cast.ProjectId}/daily-reports", new { reportDate = Date(CompanyToday), issues = "Not my site" });

        Assert.Equal(HttpStatusCode.NotFound, read.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, write.StatusCode);
        Assert.Equal(JsonValueKind.Object, report.GetProperty("can").ValueKind);
    }
}
