using System.Net;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Tests.Database;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Workforce.Tests.Database.WorkforceApi;

namespace AlMotaheda.Modules.Workforce.Tests.Database;

/// <summary>
/// The labor flow on SQL Server: an engineer records a day, a manager approves it, the labor lands on the project
/// items through <see cref="ILaborItemCosts"/>, and nobody's day is counted twice.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class LaborFlowTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task Fourteen_workers_on_one_day_seven_on_plumbing_the_items_and_the_project_total_reconcile()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var plumbing = await api.CreateItemAsync(cast.Executive, cast.ProjectId, "Plumbing", "Mep");
        var brickwork = await api.CreateItemAsync(cast.Executive, cast.ProjectId, "Brickwork", "Brickwork");
        var plastering = await api.CreateItemAsync(cast.Executive, cast.ProjectId, "Plastering", "Plastering");

        var plumbers = new List<int>();
        for (var index = 1; index <= 7; index++)
        {
            plumbers.Add(await api.HireAsync(cast.Executive, cast.ProjectId, Plumber, $"Plumber {index}"));
        }

        var masons = new List<int>();
        for (var index = 1; index <= 7; index++)
        {
            masons.Add(await api.HireAsync(cast.Executive, cast.ProjectId, Mason, $"Mason {index}"));
        }

        // 7 plumbers on plumbing; masons 1–4 on brickwork, 5–6 on plastering, and mason 7 half and half with an odd net.
        var lines = plumbers.Select(worker => Line(worker, allocations: [(plumbing, 1m)]))
            .Concat(masons.Take(4).Select(worker => Line(worker, allocations: [(brickwork, 1m)])))
            .Concat(masons.Skip(4).Take(2).Select(worker => Line(worker, allocations: [(plastering, 1m)])))
            .Append(Line(masons[6], allocations: [(brickwork, 0.5m), (plastering, 0.5m)],
                adjustments: [("Allowance", "Transport", 15.01m, null)]))
            .ToArray();

        var sheet = await api.RecordApprovedSheetAsync(cast, cast.ProjectId, api.Today(), lines);

        // The sheet: 7 × 550 + 7 × 480 + 15.01 allowance = 7,225.01, frozen at approval.
        Assert.Equal("Approved", sheet.GetProperty("status").GetString());
        Assert.False(sheet.GetProperty("payIsPreview").GetBoolean());
        Assert.Equal(7_225.01m, sheet.GetProperty("totals").Money("netAmount"));
        var split = sheet.LinesByWorker()[masons[6]];
        Assert.Equal(495.01m, split.Money("netAmount"));
        Assert.Equal(
            [247.51m, 247.50m],
            split.GetProperty("allocations").EnumerateArray().OrderBy(allocation => allocation.GetProperty("itemId").GetInt32())
                .Select(allocation => allocation.Money("amount")));

        // What the Projects items screen gets: each item its own workers, the items add up to the project's labor.
        var totals = await api.WithServiceAsync<ILaborItemCosts, IReadOnlyList<ItemCostTotal>>(
            labor => labor.TotalsByItemAsync(cast.ProjectId, CancellationToken.None));
        var byItem = totals.ToDictionary(total => total.ItemId ?? 0);
        Assert.Equal(3_850m, byItem[plumbing].Cost);
        Assert.Equal(7, byItem[plumbing].RecordCount);
        Assert.Equal(7, byItem[plumbing].PartyCount);
        Assert.Equal(1_920m + 247.51m, byItem[brickwork].Cost);
        Assert.Equal(5, byItem[brickwork].RecordCount);
        Assert.Equal(960m + 247.50m, byItem[plastering].Cost);
        Assert.Equal(3, byItem[plastering].RecordCount);
        Assert.False(byItem.ContainsKey(0)); // nothing left "not linked"
        Assert.Equal(7_225.01m, totals.Sum(total => total.Cost));

        var plumbingLines = await api.WithServiceAsync<ILaborItemCosts, IReadOnlyList<LaborItemLine>>(
            labor => labor.LinesAsync(cast.ProjectId, plumbing, CancellationToken.None));
        Assert.Equal(plumbers.Order(), plumbingLines.Select(line => line.WorkerId).Order());
        Assert.All(plumbingLines, line =>
        {
            Assert.Equal(Plumber, line.TradeId);
            Assert.Equal(api.Today(), line.WorkDate);
            Assert.Equal(1m, line.Days);
            Assert.Equal(550m, line.DailyRate);
            Assert.Equal(550m, line.Amount);
            Assert.Equal(0m, line.Paid);
            Assert.Equal(550m, line.Outstanding);
            Assert.Equal(PaymentStates.Unpaid, line.PaymentState);
        });

        var figures = await api.WithServiceAsync<IEnumerable<IProjectCostSource>, ProjectCostFigures>(
            sources => sources.Single(source => source.Group == CostGroups.Labor).SummarizeAsync(cast.ProjectId, null, null, CancellationToken.None));
        Assert.Equal(new ProjectCostFigures(7_225.01m, 0m, 14, 0m, 0, 0m), figures);

        var summary = await api.SummaryAsync(cast.Executive, cast.ProjectId);
        Assert.Equal(7_225.01m, summary.Money("approved"));
        Assert.Equal(0m, summary.Money("notLinkedCost"));
        Assert.Equal(14, summary.GetProperty("workersAssigned").GetInt32());

        // A day can't be counted in full against two items.
        var plumberLine = sheet.LinesByWorker()[plumbers[0]].Id();
        var tooMuch = await api.AllocateLineAsync(cast.Manager, cast.ProjectId, plumberLine, (plumbing, 0.6m), (brickwork, 0.5m));
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);
        Assert.Equal("allocation_exceeds_day", await CodeOf(tooMuch));

        // Half of one plumber's day moved off plumbing: it shows "not linked", and the project total doesn't move.
        var half = await api.AllocateLineAsync(cast.Manager, cast.ProjectId, plumberLine, (plumbing, 0.5m));
        await EnsureStatus(half, HttpStatusCode.OK);

        var after = await api.WithServiceAsync<ILaborItemCosts, IReadOnlyList<ItemCostTotal>>(
            labor => labor.TotalsByItemAsync(cast.ProjectId, CancellationToken.None));
        var afterByItem = after.ToDictionary(total => total.ItemId ?? 0);
        Assert.Equal(3_575m, afterByItem[plumbing].Cost);
        Assert.Equal(275m, afterByItem[0].Cost);
        Assert.Equal(1, afterByItem[0].RecordCount);
        Assert.Equal(7_225.01m, after.Sum(total => total.Cost));

        var notLinked = await api.WithServiceAsync<ILaborItemCosts, IReadOnlyList<LaborItemLine>>(
            labor => labor.LinesAsync(cast.ProjectId, null, CancellationToken.None));
        var rest = Assert.Single(notLinked);
        Assert.Equal(plumberLine, rest.LineId);
        Assert.Equal(0.5m, rest.Portion);
        Assert.Equal(550m, rest.LineNet);
        Assert.True(await api.WithServiceAsync<ILaborItemCosts, bool>(labor => labor.HasLinksAsync(cast.ProjectId, plumbing, CancellationToken.None)));
    }

    [DatabaseFact]
    public async Task Payments_settle_the_oldest_days_first_across_projects_and_follow_the_split_onto_the_items()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var second = await api.CreateProjectAsync(cast, "BDR-LAB-002");
        var plumbing = await api.CreateItemAsync(cast.Executive, cast.ProjectId, "Plumbing");
        var drainage = await api.CreateItemAsync(cast.Executive, cast.ProjectId, "Drainage");
        var worker = await api.HireAsync(cast.Executive, cast.ProjectId, Plumber, "Hassan", dailyRate: 501m);
        await api.AssignAsync(cast.Executive, second, worker);

        // Day 1 on project 1 split half and half (501 → 250.50 + 250.50); day 2 on project 2.
        await api.RecordApprovedSheetAsync(cast, cast.ProjectId, api.Today().AddDays(-2), Line(worker, allocations: [(plumbing, 0.5m), (drainage, 0.5m)]));
        await api.RecordApprovedSheetAsync(cast, second, api.Today().AddDays(-1), Line(worker));

        var tooMuch = await api.PayAsync(cast.Accountant, worker, 1_002.01m);
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);
        Assert.Equal("over_payment", await CodeOf(tooMuch));

        // The request plans the oldest days first: all of day 1 (project 1), then 99 of day 2 (project 2).
        var key = Guid.NewGuid();
        var requested = await api.PayAsync(cast.Accountant, worker, 600m, key);
        await EnsureStatus(requested, HttpStatusCode.Created);
        var request = await Json(requested);
        Assert.Equal(
            [(cast.ProjectId, 501m), (second, 99m)],
            request.GetProperty("lines").EnumerateArray()
                .Select(line => (line.GetProperty("projectId").GetInt32(), line.Money("amount"))));

        // The same key again returns the first request and creates nothing.
        var again = await api.PayAsync(cast.Accountant, worker, 600m, key);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(request.Id(), (await Json(again)).Id());

        // A different amount with the same key is refused instead of being answered with the first request.
        var different = await api.PayAsync(cast.Accountant, worker, 300m, key);
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
        Assert.Equal("idempotency_key_reused", await CodeOf(different));

        // Requested is not paid, but the 600 is held: only 402 can still be requested.
        Assert.Equal(0m, (await api.AccountAsync(cast.Accountant, worker)).Money("paid"));
        var heldTooMuch = await api.PayAsync(cast.Accountant, worker, 402.01m);
        Assert.Equal(HttpStatusCode.Conflict, heldTooMuch.StatusCode);
        Assert.Equal("over_payment", await CodeOf(heldTooMuch));

        // Approved and paid (cash): the payment is written exactly as planned.
        var payment = await api.PaymentOfAsync(cast.Accountant, worker, await api.SettleAsync(cast.Accountant, request));
        Assert.Equal(
            [(cast.ProjectId, 501m), (second, 99m)],
            payment.GetProperty("allocations").EnumerateArray()
                .Select(allocation => (allocation.GetProperty("projectId").GetInt32(), allocation.Money("amount"))));

        var account = await api.AccountAsync(cast.Accountant, worker);
        Assert.Equal(1_002m, account.Money("approvedPay"));
        Assert.Equal(600m, account.Money("paid"));
        Assert.Equal(402m, account.Money("outstanding"));

        // The paid day is split over the two items like its cost.
        var totals = await api.WithServiceAsync<ILaborItemCosts, IReadOnlyList<ItemCostTotal>>(
            labor => labor.TotalsByItemAsync(cast.ProjectId, CancellationToken.None));
        Assert.Equal([250.50m, 250.50m], totals.OrderBy(total => total.ItemId).Select(total => total.Paid));
        Assert.Equal(501m, totals.Sum(total => total.Paid));

        // Voided, the payment stops counting everywhere.
        var voided = await api.SendAsync(HttpMethod.Post, $"/api/worker-payments/{payment.Id()}/void", cast.Executive,
            new { reason = "Entered twice", rowVersion = payment.GetProperty("rowVersion").GetString() });
        await EnsureStatus(voided, HttpStatusCode.OK);
        Assert.Equal(0m, (await api.AccountAsync(cast.Accountant, worker)).Money("paid"));
        var figures = await api.WithServiceAsync<IEnumerable<IProjectCostSource>, ProjectCostFigures>(
            sources => sources.Single(source => source.Group == CostGroups.Labor).SummarizeAsync(cast.ProjectId, null, null, CancellationToken.None));
        Assert.Equal(501m, figures.Recognized);
        Assert.Equal(0m, figures.Paid);
    }

    [DatabaseFact]
    public async Task Only_approved_days_are_cost_and_a_rejected_sheet_frees_the_workers_day()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var (otherEngineerId, otherEngineer) = await api.CreateSignedInUserAsync(cast.Executive, "engineer");
        var project = await api.CreateProjectAsync(cast.Executive, "BDR-LAB-003", cast.ManagerId, siteEngineers: [cast.EngineerId, otherEngineerId]);
        var worker = await api.HireAsync(cast.Executive, project.Id(), Mason, "Mahmoud");
        var today = api.Today();

        var recorded = await api.RecordSheetAsync(cast.Engineer, project.Id(), today, submit: true, Line(worker));
        await EnsureStatus(recorded, HttpStatusCode.Created);
        var sheet = await Json(recorded);

        // A worker is recorded once per day and shift, whoever records.
        var twice = await api.RecordSheetAsync(otherEngineer, project.Id(), today, submit: true, Line(worker));
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal("worker_already_recorded", await CodeOf(twice));

        // Pending is shown apart and is never cost; the recorder can't review their own sheet.
        var pending = await api.WithServiceAsync<IEnumerable<IProjectCostSource>, ProjectCostFigures>(
            sources => sources.Single(source => source.Group == CostGroups.Labor).SummarizeAsync(project.Id(), null, null, CancellationToken.None));
        Assert.Equal(new ProjectCostFigures(0m, 0m, 0, 480m, 1, 0m), pending);
        var own = await api.ReviewAsync(cast.Engineer, project.Id(), sheet, "reject", "Wrong day");
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode); // engineers have no workers Approve

        var rejected = await api.ReviewAsync(cast.Manager, project.Id(), sheet, "reject", "Wrong day");
        await EnsureStatus(rejected, HttpStatusCode.OK);
        Assert.Empty(await api.WithServiceAsync<ILaborItemCosts, IReadOnlyList<ItemCostTotal>>(
            labor => labor.TotalsByItemAsync(project.Id(), CancellationToken.None)));

        // The day is free again: the other engineer records it and it is approved.
        var approved = await api.RecordApprovedSheetAsync(cast with { Engineer = otherEngineer }, project.Id(), today, Line(worker));
        Assert.Equal(480m, approved.GetProperty("totals").Money("netAmount"));
        var notLinked = await api.WithServiceAsync<ILaborItemCosts, IReadOnlyList<ItemCostTotal>>(
            labor => labor.TotalsByItemAsync(project.Id(), CancellationToken.None));
        var total = Assert.Single(notLinked);
        Assert.Null(total.ItemId);
        Assert.Equal(480m, total.Cost);
    }

    [DatabaseFact]
    public async Task A_manager_cannot_approve_a_sheet_they_recorded()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var worker = await api.HireAsync(cast.Executive, cast.ProjectId, Guard, "Saber");

        // The Executive records and then tries to approve the same sheet.
        var recorded = await api.RecordSheetAsync(cast.Executive, cast.ProjectId, api.Today(), submit: true, Line(worker));
        await EnsureStatus(recorded, HttpStatusCode.Created);

        var own = await api.ReviewAsync(cast.Executive, cast.ProjectId, await Json(recorded), "approve");

        Assert.Equal(HttpStatusCode.Conflict, own.StatusCode);
        Assert.Equal("own_record", await CodeOf(own));
    }

    [DatabaseFact]
    public async Task Workers_must_be_assigned_to_the_project_on_the_work_date()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var other = await api.CreateProjectAsync(cast, "BDR-LAB-004");
        var worker = await api.HireAsync(cast.Executive, other, Mason, "Ali");

        var response = await api.RecordSheetAsync(cast.Engineer, cast.ProjectId, api.Today(), submit: false, Line(worker));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("not_assigned", await CodeOf(response));
    }
}
