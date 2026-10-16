using System.Net;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Tests.Database;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Workforce.Tests.Database.WorkforceApi;

namespace AlMotaheda.Modules.Workforce.Tests.Database;

/// <summary>
/// The worker screens show only the projects the caller may see, the recorder's allocation rights end at submit,
/// and only submitted sheets are pending review. On SQL Server.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ProjectScopeTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task A_manager_sees_a_workers_assignments_pay_and_payments_of_their_own_project_only()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var (otherManagerId, otherManager) = await api.CreateSignedInUserAsync(cast.Executive, "manager");
        var other = await api.CreateProjectAsync(cast.Executive, "BDR-LAB-020", otherManagerId, siteEngineers: [cast.EngineerId]);
        var worker = await api.HireAsync(cast.Executive, cast.ProjectId, Mason, "Hassan");
        await api.AssignAsync(cast.Executive, other.Id(), worker);

        // 480 on each project; one payment of 960 settles the older day (other project) and then this one.
        await api.RecordApprovedSheetAsync(cast with { Manager = otherManager }, other.Id(), api.Today().AddDays(-1), Line(worker));
        await api.RecordApprovedSheetAsync(cast, cast.ProjectId, api.Today(), Line(worker));
        await api.PaidAsync(cast.Accountant, worker, 960m);

        var detail = await Json(await api.SendAsync(HttpMethod.Get, $"/api/workers/{worker}", cast.Manager));
        Assert.Equal([cast.ProjectId], detail.GetProperty("assignments").EnumerateArray().Select(row => row.GetProperty("projectId").GetInt32()));

        var account = await api.AccountAsync(cast.Manager, worker);
        Assert.Equal(480m, account.Money("approvedPay"));
        Assert.Equal(480m, account.Money("paid"));
        Assert.Equal(0m, account.Money("outstanding"));
        Assert.Equal([cast.ProjectId], account.GetProperty("projects").EnumerateArray().Select(row => row.GetProperty("projectId").GetInt32()));

        var payments = await Json(await api.SendAsync(HttpMethod.Get, $"/api/workers/{worker}/payments", cast.Manager));
        var payment = Assert.Single(payments.GetProperty("items").EnumerateArray());
        Assert.Equal(480m, payment.Money("amount"));
        Assert.Equal([cast.ProjectId], payment.GetProperty("allocations").EnumerateArray().Select(row => row.GetProperty("projectId").GetInt32()));

        // The Executive sees every project: the company-wide account.
        var full = await api.AccountAsync(cast.Executive, worker);
        Assert.Equal(960m, full.Money("approvedPay"));
        Assert.Equal(960m, full.Money("paid"));
        Assert.Equal(2, full.GetProperty("projects").GetArrayLength());
    }

    [DatabaseFact]
    public async Task The_recorder_changes_allocations_on_a_draft_sheet_but_not_after_submitting_it()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var item = await api.CreateItemAsync(cast.Executive, cast.ProjectId, "Brickwork", "Brickwork");
        var mason = await api.HireAsync(cast.Executive, cast.ProjectId, Mason, "Saad");
        var guard = await api.HireAsync(cast.Executive, cast.ProjectId, Guard, "Fathy");

        var draft = await Json(await api.RecordSheetAsync(cast.Engineer, cast.ProjectId, api.Today(), submit: false, Line(mason)));
        var onDraft = await AllocateSheetAsync(api, cast.Engineer, cast.ProjectId, draft, draft.LinesByWorker()[mason].Id(), item);
        Assert.Equal(HttpStatusCode.OK, onDraft.StatusCode);

        var submitted = await Json(await api.RecordSheetAsync(cast.Engineer, cast.ProjectId, api.Today().AddDays(-1), submit: true, Line(guard)));
        var onSubmitted = await AllocateSheetAsync(api, cast.Engineer, cast.ProjectId, submitted, submitted.LinesByWorker()[guard].Id(), item);
        Assert.Equal(HttpStatusCode.Forbidden, onSubmitted.StatusCode);

        // A manager (workers Approve) still may.
        var byManager = await AllocateSheetAsync(api, cast.Manager, cast.ProjectId, submitted, submitted.LinesByWorker()[guard].Id(), item);
        Assert.Equal(HttpStatusCode.OK, byManager.StatusCode);
    }

    [DatabaseFact]
    public async Task A_returned_sheet_is_not_pending_review()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var worker = await api.HireAsync(cast.Executive, cast.ProjectId, Mason, "Ramy");
        var sheet = await Json(await api.RecordSheetAsync(cast.Engineer, cast.ProjectId, api.Today(), submit: true, Line(worker)));

        Assert.Equal(new ProjectCostFigures(0m, 0m, 0, 480m, 1, 0m), await LaborFiguresAsync(api, cast.ProjectId));

        await EnsureStatus(await api.ReviewAsync(cast.Manager, cast.ProjectId, sheet, "return", "Wrong shift"), HttpStatusCode.OK);

        Assert.Equal(new ProjectCostFigures(0m, 0m, 0, 0m, 0, 0m), await LaborFiguresAsync(api, cast.ProjectId));
        var summary = await api.SummaryAsync(cast.Manager, cast.ProjectId);
        Assert.Equal(0, summary.GetProperty("sheetsPending").GetInt32());
        Assert.Equal(1, summary.GetProperty("sheetsReturned").GetInt32());
        Assert.Equal(0, summary.GetProperty("pendingLines").GetInt32());
    }

    private static Task<HttpResponseMessage> AllocateSheetAsync(ProjectsApi api, Session session, int projectId, System.Text.Json.JsonElement sheet, int lineId, int itemId) =>
        api.SendAsync(HttpMethod.Put, $"/api/projects/{projectId}/labor/sheets/{sheet.Id()}/allocations", session, new
        {
            rowVersion = sheet.GetProperty("rowVersion").GetString(),
            lines = new[] { new { lineId, allocations = new[] { new { itemId, portion = 1m } } } },
        });

    private static Task<ProjectCostFigures> LaborFiguresAsync(ProjectsApi api, int projectId) =>
        api.WithServiceAsync<IEnumerable<IProjectCostSource>, ProjectCostFigures>(
            sources => sources.Single(source => source.Group == CostGroups.Labor).SummarizeAsync(projectId, null, null, CancellationToken.None));
}
