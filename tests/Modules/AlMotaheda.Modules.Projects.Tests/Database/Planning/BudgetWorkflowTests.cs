using System.Net;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Core.CoreApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Planning.PlanningApi;

namespace AlMotaheda.Modules.Projects.Tests.Database.Planning;

/// <summary>Budget versions, approval and the budget against actual cost, end to end on SQL Server.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class BudgetWorkflowTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task A_budget_is_never_overwritten_a_revision_is_a_new_version_and_approving_it_supersedes_the_old_one()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("BDR-BUD-001");
        var item = await api.CreateItemAsync(cast.Manager, cast.ProjectId, "Concrete works");
        var budgets = $"/api/projects/{cast.ProjectId}/budgets";

        // The server computes a priced line: 120.5 × 1,187.50 = 143,093.75. The client's amount is ignored.
        var first = await ExpectAsync(await api.PostAsync(cast.Manager, budgets, new
        {
            title = "Tender estimate",
            lines = new object[]
            {
                new { costGroup = "Materials", projectItemId = item.Id(), description = "Ready-mix concrete", unit = "M3", quantity = 120.5m, unitPrice = 1_187.50m, amount = 1m },
                new { costGroup = "labor", description = "Concrete crew", amount = 50_000m },
            },
        }), HttpStatusCode.Created);
        Assert.Equal(1, first.GetProperty("version").GetInt32());
        Assert.Equal("Draft", first.Status());
        Assert.Equal(193_093.75m, first.Money("total"));
        var lines = first.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(143_093.75m, lines[0].Money("amount"));
        Assert.Equal("Labor", lines[1].GetProperty("costGroup").GetString());

        // One draft at a time; an engineer can't prepare a budget; a manager can't approve one.
        var second = await api.PostAsync(cast.Manager, budgets, new { title = "Another" });
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("budget_draft_exists", await CodeOf(second));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.PostAsync(cast.Engineer, budgets, new { title = "Mine" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.PostAsync(cast.Manager, $"{budgets}/{first.Id()}/approve", new { rowVersion = first.RowVersion() })).StatusCode);

        var approved = await ExpectAsync(
            await api.PostAsync(cast.Executive, $"{budgets}/{first.Id()}/approve", new { rowVersion = first.RowVersion() }), HttpStatusCode.OK);
        Assert.Equal("Approved", approved.Status());
        Assert.Equal(cast.Executive.UserId, approved.GetProperty("approver").GetProperty("userId").GetInt32());

        // An approved version never changes.
        var edit = await api.PutAsync(cast.Manager, $"{budgets}/{first.Id()}", new { title = "Changed", lines = Array.Empty<object>(), rowVersion = approved.RowVersion() });
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal("budget_not_draft", await CodeOf(edit));

        // Version 2 starts as a copy, is changed, and a stale row version is refused.
        var revision = await ExpectAsync(
            await api.PostAsync(cast.Manager, budgets, new { title = "Revision 1", copyFromBudgetId = first.Id() }), HttpStatusCode.Created);
        Assert.Equal(2, revision.GetProperty("version").GetInt32());
        Assert.Equal(193_093.75m, revision.Money("total"));

        var changed = await ExpectAsync(await api.PutAsync(cast.Manager, $"{budgets}/{revision.Id()}", new
        {
            title = "Revision 1",
            lines = new object[] { new { costGroup = "Materials", description = "All materials", amount = 200_000m } },
            rowVersion = revision.RowVersion(),
        }), HttpStatusCode.OK);
        Assert.Equal(200_000m, changed.Money("total"));
        var stale = await api.PutAsync(cast.Manager, $"{budgets}/{revision.Id()}", new { title = "Late", lines = Array.Empty<object>(), rowVersion = revision.RowVersion() });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("concurrency_conflict", await CodeOf(stale));

        await ExpectAsync(await api.PostAsync(cast.Executive, $"{budgets}/{revision.Id()}/approve", new { rowVersion = changed.RowVersion() }), HttpStatusCode.OK);

        var list = (await api.GetOkAsync(cast.Manager, budgets)).GetProperty("items").EnumerateArray().ToList();
        Assert.Equal([2, 1], list.Select(budget => budget.GetProperty("version").GetInt32()));
        Assert.Equal(["Approved", "Superseded"], list.Select(budget => budget.Status()));

        // The old version keeps its lines and its approval, so old comparisons stay true.
        var old = await api.GetOkAsync(cast.Manager, $"{budgets}/{first.Id()}");
        Assert.Equal(193_093.75m, old.Money("total"));
        Assert.Equal(2, old.GetProperty("lines").GetArrayLength());
        Assert.Equal(1, await api.QueryAsync(db => db.Budgets.CountAsync(budget => budget.ProjectId == cast.ProjectId && budget.Status == BudgetStatus.Approved)));
    }

    [DatabaseFact]
    public async Task A_budget_with_no_lines_cannot_be_approved()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("BDR-BUD-002");
        var budgets = $"/api/projects/{cast.ProjectId}/budgets";

        var empty = await ExpectAsync(await api.PostAsync(cast.Manager, budgets, new { title = "Empty" }), HttpStatusCode.Created);
        var approve = await api.PostAsync(cast.Executive, $"{budgets}/{empty.Id()}/approve", new { rowVersion = empty.RowVersion() });

        Assert.Equal(HttpStatusCode.Conflict, approve.StatusCode);
        Assert.Equal("budget_empty", await CodeOf(approve));
    }

    [DatabaseFact]
    public async Task The_overview_comparison_and_reports_keep_budget_recognized_paid_and_pending_apart()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("BDR-BUD-003");
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Suez Cement");
        var budgets = $"/api/projects/{cast.ProjectId}/budgets";

        var budget = await ExpectAsync(await api.PostAsync(cast.Manager, budgets, new
        {
            title = "Tender estimate",
            lines = new object[]
            {
                new { costGroup = "Materials", description = "Materials", amount = 100_000m },
                new { costGroup = "SiteExpenses", description = "Site running costs", amount = 10_000m },
            },
        }), HttpStatusCode.Created);
        await ExpectAsync(await api.PostAsync(cast.Executive, $"{budgets}/{budget.Id()}/approve", new { rowVersion = budget.RowVersion() }), HttpStatusCode.OK);

        // 100 bags × 150 on credit (approved, 5,000 paid), 10 bags waiting for review, and a lab test paid in cash.
        var supply = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(supplier, 100m, 150m, "Credit"));
        await api.RecordAsync(cast.Engineer, cast.ProjectId, SupplyBody(supplier, 10m, 150m, "Credit"));
        await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, ExpenseBody(2_500m, "Custody"));
        await api.PaidAsync(cast.Accountant, cast.ProjectId, supplier, 5_000m, (supply.Id(), 5_000m));

        var overview = await api.GetOkAsync(cast.Manager, $"/api/projects/{cast.ProjectId}/overview");
        Assert.Equal(budget.Id(), overview.GetProperty("budget").GetProperty("id").GetInt32());
        var totals = overview.GetProperty("totals");
        Assert.Equal(110_000m, totals.Money("budget"));
        Assert.Equal(17_500m, totals.Money("recognized"));
        Assert.Equal(7_500m, totals.Money("paid"));
        Assert.Equal(10_000m, totals.Money("outstanding"));
        Assert.Equal(1_500m, totals.Money("pendingReview"));
        Assert.Equal(92_500m, totals.Money("remainingBudget"));
        Assert.Equal(15.91m, totals.Money("percentUsed"));
        var materials = overview.GetProperty("groups").Group("Materials");
        Assert.Equal(100_000m, materials.Money("budgeted"));
        Assert.Equal(15_000m, materials.Money("recognized"));
        Assert.Equal(1_500m, materials.Money("pendingReview"));
        Assert.Equal(1, overview.GetProperty("counts").GetProperty("pendingReviewRecords").GetInt32());

        var comparison = await api.GetOkAsync(cast.Manager, $"/api/projects/{cast.ProjectId}/budget-comparison");
        var compared = comparison.GetProperty("groups").Group("Materials");
        Assert.Equal(85_000m, compared.Money("variance"));
        Assert.Equal(85.00m, compared.Money("variancePercent"));
        Assert.Equal(17_500m, comparison.GetProperty("totals").Money("recognized"));

        // The records are dated 2026-01-10: inside January, outside February.
        var january = await api.GetOkAsync(cast.Manager, $"/api/projects/{cast.ProjectId}/reports/cost-summary?from=2026-01-01&to=2026-01-31");
        Assert.Equal(17_500m, january.GetProperty("totals").Money("recognized"));
        Assert.Equal(2, january.GetProperty("totals").GetProperty("recognizedRecords").GetInt32());
        var february = await api.GetOkAsync(cast.Manager, $"/api/projects/{cast.ProjectId}/reports/cost-summary?from=2026-02-01&to=2026-02-28");
        Assert.Equal(0m, february.GetProperty("totals").Money("recognized"));

        var obligations = await api.GetOkAsync(cast.Accountant, $"/api/projects/{cast.ProjectId}/reports/obligations");
        var owed = Assert.Single(obligations.GetProperty("suppliers").EnumerateArray());
        Assert.Equal(supplier, owed.GetProperty("supplierId").GetInt32());
        Assert.Equal(10_000m, owed.Money("outstanding"));
        Assert.Equal(2_500m, obligations.GetProperty("withoutSupplier").Money("paid"));
        Assert.Equal(10_000m, obligations.Money("totalOutstanding"));

        // Money screens need projects See money.
        var refused = await api.SendAsync(HttpMethod.Get, $"/api/projects/{cast.ProjectId}/overview", cast.Engineer);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }
}
