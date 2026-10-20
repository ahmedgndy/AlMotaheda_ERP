using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database.Workspace;
using AlMotaheda.Modules.Workforce.Data;
using Microsoft.EntityFrameworkCore;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Workforce.Tests.Database.WorkforceApi;

namespace AlMotaheda.Modules.Workforce.Tests.Database;

/// <summary>
/// The daily team and the weekly cash settlement on SQL Server: two carpenters at 400 a day cost 800 on Concrete
/// works; members of one team on different work items; the engineer pays the week in cash from custody; the
/// accountant confirms; the labor is paid once, never a second cost, and every screen reads the same figures.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SettlementTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task Two_carpenters_at_400_cost_800_on_concrete_and_a_team_can_split_across_work_items()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("BDR-TEAM-001");
        var concrete = await api.CreateItemAsync(cast.Manager, cast.ProjectId, "Concrete works", "Concrete");
        var masonry = await api.CreateItemAsync(cast.Manager, cast.ProjectId, "Masonry works", "Brickwork");
        var first = await api.HireAsync(cast.Executive, cast.ProjectId, Mason, "Formwork carpenter 1", dailyRate: 400m);
        var second = await api.HireAsync(cast.Executive, cast.ProjectId, Mason, "Formwork carpenter 2", dailyRate: 400m);

        // Day 1: both on Concrete works: 2 × 400 = 800.
        var dayOne = await api.RecordApprovedSheetAsync(cast, cast.ProjectId, api.Today().AddDays(-2),
            Line(first, allocations: [(concrete, 1m)]), Line(second, allocations: [(concrete, 1m)]));
        Assert.Equal(800m, dayOne.GetProperty("totals").Money("netAmount"));
        var concreteRow = await api.ItemRowAsync(cast.Executive, cast.ProjectId, concrete);
        Assert.Equal(800m, concreteRow.Dec("laborCost"));
        Assert.Equal(800m, concreteRow.Dec("laborOutstanding"));

        // Day 2: one on Concrete works, the other on Masonry works: each item gets its own worker's day.
        await api.RecordApprovedSheetAsync(cast, cast.ProjectId, api.Today().AddDays(-1),
            Line(first, allocations: [(concrete, 1m)]), Line(second, allocations: [(masonry, 1m)]));
        Assert.Equal(1_200m, (await api.ItemRowAsync(cast.Executive, cast.ProjectId, concrete)).Dec("laborCost"));
        Assert.Equal(400m, (await api.ItemRowAsync(cast.Executive, cast.ProjectId, masonry)).Dec("laborCost"));

        var summary = await api.SummaryAsync(cast.Accountant, cast.ProjectId);
        Assert.Equal(1_600m, summary.Money("approved"));
        Assert.Equal(1_600m, summary.Money("outstanding"));
    }

    [DatabaseFact]
    public async Task A_weekly_cash_settlement_pays_the_labor_once_and_reconciles_the_items_the_workers_and_the_custody()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("BDR-PAY-001");
        var concrete = await api.CreateItemAsync(cast.Manager, cast.ProjectId, "Concrete works", "Concrete");
        var masonry = await api.CreateItemAsync(cast.Manager, cast.ProjectId, "Masonry works", "Brickwork");
        var first = await api.HireAsync(cast.Executive, cast.ProjectId, Mason, "Carpenter 1", dailyRate: 400m);
        var second = await api.HireAsync(cast.Executive, cast.ProjectId, Mason, "Carpenter 2", dailyRate: 400m);
        await api.RecordApprovedSheetAsync(cast, cast.ProjectId, api.Today().AddDays(-2),
            Line(first, allocations: [(concrete, 1m)]), Line(second, allocations: [(concrete, 1m)]));
        await api.RecordApprovedSheetAsync(cast, cast.ProjectId, api.Today().AddDays(-1),
            Line(first, allocations: [(concrete, 1m)]), Line(second, allocations: [(masonry, 1m)]));

        // The engineer's custody holds 2,000 (asked by the engineer, paid through Finance).
        await api.FundAsync(cast.Engineer, cast.Accountant, cast.ProjectId, 2_000m);
        var from = api.Today().AddDays(-6).Iso();
        var to = api.Today().Iso();

        // The engineer (a custody holder) sees what the week owes.
        var payable = await Json(await api.SendAsync(HttpMethod.Get, $"/api/projects/{cast.ProjectId}/labor/payable?from={from}&to={to}", cast.Engineer));
        Assert.Equal(1_600m, payable.Money("outstanding"));
        Assert.Equal(2_000m, payable.Money("custodyBalance"));

        // The engineer pays everything in cash; the form is sent twice.
        var key = Guid.NewGuid();
        var body = SettlementBody(api, key);
        var recorded = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/labor/settlements", cast.Engineer, body);
        await EnsureStatus(recorded, HttpStatusCode.Created);
        var repeated = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/labor/settlements", cast.Engineer, body);
        await EnsureStatus(repeated, HttpStatusCode.OK);
        var settlement = await Json(recorded);
        Assert.Equal(settlement.Id(), (await Json(repeated)).Id());
        Assert.Equal(("Pending", 1_600m, 2), (settlement.GetProperty("status").GetString(), settlement.Money("amount"), settlement.GetProperty("workerCount").GetInt32()));
        Assert.Equal(cast.EngineerId, settlement.GetProperty("paidByUserId").GetInt32());

        // The cash is out of the custody at once; the days are held, not paid yet.
        Assert.Equal(400m, await api.BalanceAsync(cast.Engineer, cast.ProjectId, cast.EngineerId));
        var held = await Json(await api.SendAsync(HttpMethod.Get, $"/api/projects/{cast.ProjectId}/labor/payable?from={from}&to={to}", cast.Accountant));
        Assert.Equal((0m, 1_600m, 0m), (held.Money("paid"), held.Money("held"), held.Money("outstanding")));
        Assert.Equal(1_600m, (await api.SummaryAsync(cast.Accountant, cast.ProjectId)).Money("outstanding"));

        // Nobody can pay the same days again: not a second settlement, not a Finance payment request.
        var again = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/labor/settlements", cast.Engineer, SettlementBody(api, Guid.NewGuid()));
        Assert.Equal("nothing_to_settle", await CodeOf(again));
        var request = await api.PayAsync(cast.Accountant, first, 400m);
        Assert.Equal("over_payment", await CodeOf(request));

        // The managers, Executives and accountants were told, with the settlement; never the engineer.
        var told = await api.WithServiceAsync<WorkforceDbContext, List<int>>(db =>
            db.SettlementAlerts.Where(alert => alert.SettlementId == settlement.Id()).Select(alert => alert.RecipientUserId).ToListAsync());
        Assert.Contains(cast.ManagerId, told);
        Assert.Contains(cast.Executive.UserId, told);
        Assert.Contains(cast.Accountant.UserId, told);
        Assert.DoesNotContain(cast.EngineerId, told);

        // It waits in the accountant's work inbox, not in the engineer's (who recorded and paid it).
        Assert.Contains(await InboxTypesAsync(api, cast.Accountant), entry => entry == ("PayrollSettlement", settlement.Id()));
        Assert.DoesNotContain(await InboxTypesAsync(api, cast.Engineer), entry => entry.Type == "PayrollSettlement");

        // Only a custody reviewer confirms, never the engineer who paid, never the manager (no custody Approve).
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(api, cast.Engineer, cast.ProjectId, settlement, "confirm")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(api, cast.Manager, cast.ProjectId, settlement, "confirm")).StatusCode);
        var confirmed = await DecideAsync(api, cast.Accountant, cast.ProjectId, settlement, "confirm");
        await EnsureStatus(confirmed, HttpStatusCode.OK);
        var paid = await Json(confirmed);
        Assert.Equal("Confirmed", paid.GetProperty("status").GetString());
        Assert.All(paid.GetProperty("lines").EnumerateArray(), line => Assert.NotEqual(JsonValueKind.Null, line.GetProperty("paymentId").ValueKind));

        // Paid once: the labor cost stays 1,600, now paid; each work item gets its own days' share.
        var summary = await api.SummaryAsync(cast.Accountant, cast.ProjectId);
        Assert.Equal((1_600m, 1_600m, 0m), (summary.Money("approved"), summary.Money("paid"), summary.Money("outstanding")));
        var concreteRow = await api.ItemRowAsync(cast.Executive, cast.ProjectId, concrete);
        Assert.Equal((1_200m, 1_200m), (concreteRow.Dec("laborCost"), concreteRow.Dec("laborPaid")));
        var masonryRow = await api.ItemRowAsync(cast.Executive, cast.ProjectId, masonry);
        Assert.Equal((400m, 400m), (masonryRow.Dec("laborCost"), masonryRow.Dec("laborPaid")));
        Assert.Equal(800m, (await api.AccountAsync(cast.Accountant, first)).Money("paid"));
        var custody = (await api.CustodyAsync(cast.Accountant, cast.ProjectId, cast.EngineerId)).GetProperty("custody");
        Assert.Equal((400m, 1_600m, 0m), (custody.Dec("balance"), custody.Dec("spentApproved"), custody.Dec("spentPending")));

        // A payment of the settlement is undone only by reversing the settlement.
        var paymentId = paid.GetProperty("lines").EnumerateArray().Single(line => line.GetProperty("workerId").GetInt32() == first).GetProperty("paymentId").GetInt32();
        var payments = await Json(await api.SendAsync(HttpMethod.Get, $"/api/workers/{first}/payments?pageSize=100", cast.Accountant));
        var payment = payments.GetProperty("items").EnumerateArray().Single(row => row.Id() == paymentId);
        var voided = await api.SendAsync(HttpMethod.Post, $"/api/worker-payments/{payment.Id()}/void", cast.Executive,
            new { reason = "Wrong", rowVersion = payment.GetProperty("rowVersion").GetString() });
        Assert.Equal("settlement_payment", await CodeOf(voided));

        var reversed = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/labor/settlements/{settlement.Id()}/reverse", cast.Accountant,
            new { note = "Paid to the wrong crew", rowVersion = paid.GetProperty("rowVersion").GetString() });
        await EnsureStatus(reversed, HttpStatusCode.OK);
        Assert.Equal("Reversed", (await Json(reversed)).GetProperty("status").GetString());
        Assert.Equal(1_600m, (await api.SummaryAsync(cast.Accountant, cast.ProjectId)).Money("outstanding"));
        Assert.Equal(2_000m, await api.BalanceAsync(cast.Engineer, cast.ProjectId, cast.EngineerId));

        // Nothing was deleted: the settlement, its payments (voided) and the custody line (released) stay for the audit.
        var kept = await api.WithServiceAsync<WorkforceDbContext, (int Payments, int Voided)>(async db => (
            await db.WorkerPayments.CountAsync(row => row.SettlementId == settlement.Id()),
            await db.WorkerPayments.CountAsync(row => row.SettlementId == settlement.Id() && row.VoidedAt != null)));
        Assert.Equal((2, 2), kept);
        var lines = (await api.CustodyAsync(cast.Accountant, cast.ProjectId, cast.EngineerId)).GetProperty("lines").EnumerateArray();
        Assert.DoesNotContain(lines, line => line.GetProperty("type").GetString() == "Payroll");
    }

    [DatabaseFact]
    public async Task A_rejected_settlement_frees_the_days_and_gives_the_cash_back_and_an_engineer_pays_only_from_their_own_custody()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("BDR-PAY-002");
        var concrete = await api.CreateItemAsync(cast.Manager, cast.ProjectId, "Concrete works", "Concrete");
        var worker = await api.HireAsync(cast.Executive, cast.ProjectId, Mason, "Carpenter", dailyRate: 400m);
        await api.RecordApprovedSheetAsync(cast, cast.ProjectId, api.Today().AddDays(-1), Line(worker, allocations: [(concrete, 1m)]));
        await api.FundAsync(cast.Engineer, cast.Accountant, cast.ProjectId, 1_000m);

        // An engineer can't say the cash came from the manager's custody.
        var forManager = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/labor/settlements", cast.Engineer,
            SettlementBody(api, Guid.NewGuid(), paidByUserId: cast.ManagerId));
        Assert.Equal(HttpStatusCode.Forbidden, forManager.StatusCode);

        // More than the worker is owed is refused.
        var tooMuch = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/labor/settlements", cast.Engineer,
            SettlementBody(api, Guid.NewGuid(), lines: [(worker, 500m)]));
        Assert.Equal("over_payment", await CodeOf(tooMuch));

        var recorded = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/labor/settlements", cast.Engineer,
            SettlementBody(api, Guid.NewGuid(), lines: [(worker, 400m)]));
        await EnsureStatus(recorded, HttpStatusCode.Created);
        var settlement = await Json(recorded);
        Assert.Equal(600m, await api.BalanceAsync(cast.Engineer, cast.ProjectId, cast.EngineerId));

        var noReason = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/labor/settlements/{settlement.Id()}/reject", cast.Accountant,
            new { rowVersion = settlement.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        var rejected = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/labor/settlements/{settlement.Id()}/reject", cast.Accountant,
            new { note = "No signatures", rowVersion = settlement.GetProperty("rowVersion").GetString() });
        await EnsureStatus(rejected, HttpStatusCode.OK);
        Assert.Equal(1_000m, await api.BalanceAsync(cast.Engineer, cast.ProjectId, cast.EngineerId));

        // A rejected settlement can't be confirmed later, and the day can be paid again.
        Assert.Equal("invalid_status", await CodeOf(await DecideAsync(api, cast.Accountant, cast.ProjectId, await Json(rejected), "confirm")));
        await EnsureStatus(await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/labor/settlements", cast.Engineer,
            SettlementBody(api, Guid.NewGuid())), HttpStatusCode.Created);

        // The engineer sees their own settlements only; someone off the project gets 404.
        var mine = await Json(await api.SendAsync(HttpMethod.Get, $"/api/projects/{cast.ProjectId}/labor/settlements", cast.Engineer));
        Assert.Equal(2, mine.GetProperty("items").GetArrayLength());
        var (_, stranger) = await api.CreateSignedInUserAsync(cast.Executive, "engineer");
        Assert.Equal(HttpStatusCode.NotFound,
            (await api.SendAsync(HttpMethod.Get, $"/api/projects/{cast.ProjectId}/labor/settlements/{settlement.Id()}", stranger)).StatusCode);
    }

    [DatabaseFact]
    public async Task Two_settlements_of_the_same_week_sent_at_the_same_moment_pay_the_days_once()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("BDR-PAY-004");
        var concrete = await api.CreateItemAsync(cast.Manager, cast.ProjectId, "Concrete works", "Concrete");
        var worker = await api.HireAsync(cast.Executive, cast.ProjectId, Mason, "Carpenter", dailyRate: 400m);
        await api.RecordApprovedSheetAsync(cast, cast.ProjectId, api.Today().AddDays(-1), Line(worker, allocations: [(concrete, 1m)]));
        await api.FundAsync(cast.Engineer, cast.Accountant, cast.ProjectId, 1_000m);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/labor/settlements", cast.Engineer, SettlementBody(api, Guid.NewGuid()))));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
        Assert.All(responses.Where(response => response.StatusCode != HttpStatusCode.Created),
            response => Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.ServiceUnavailable }));
        var held = await api.WithServiceAsync<WorkforceDbContext, decimal>(db =>
            db.PayrollSettlementAllocations.Where(allocation => allocation.SettlementLine.Settlement.ProjectId == cast.ProjectId).SumAsync(allocation => allocation.Amount));
        Assert.Equal(400m, held);
        Assert.Equal(600m, await api.BalanceAsync(cast.Engineer, cast.ProjectId, cast.EngineerId));
    }

    private static async Task<List<(string Type, int Id)>> InboxTypesAsync(ProjectsApi api, Session session)
    {
        var response = await api.SendAsync(HttpMethod.Get, "/api/inbox", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return (await Json(response)).GetProperty("items").EnumerateArray()
            .Select(item => (item.GetProperty("recordType").GetString()!, item.GetProperty("recordId").GetInt32())).ToList();
    }

    private static object SettlementBody(ProjectsApi api, Guid key, (int WorkerId, decimal Amount)[]? lines = null, int? paidByUserId = null) => new
    {
        periodFrom = api.Today().AddDays(-6).Iso(),
        periodTo = api.Today().Iso(),
        paidOn = api.Today().Iso(),
        paidByUserId,
        reference = "Week sheet 42",
        idempotencyKey = key.ToString(),
        lines = lines?.Select(line => new { workerId = line.WorkerId, amount = line.Amount }).ToArray(),
    };

    private static Task<HttpResponseMessage> DecideAsync(ProjectsApi api, Session session, int projectId, JsonElement settlement, string action) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/labor/settlements/{settlement.Id()}/{action}", session,
            new { note = action == "confirm" ? null : "Checked", rowVersion = settlement.GetProperty("rowVersion").GetString() });
}
