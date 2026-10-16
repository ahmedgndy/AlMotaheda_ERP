using System.Net;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Tests.Database;
using AlMotaheda.Modules.Subcontractors.Data;
using Microsoft.EntityFrameworkCore;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Subcontractors.Tests.Database;

/// <summary>
/// The subcontractor flows on SQL Server: what is cost and what is not, linking to project items, project
/// separation, payments with advances and voids, and the limits (quantity, ceiling, value).
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SubcontractFlowTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task The_contract_value_is_never_cost_and_only_approved_work_is()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var abc = await api.CreateCompanyAsync(cast.Executive, "ABC Construction");

        var contract = await api.CreateContractAsync(cast.Manager, cast.ProjectId, SubcontractsApi.ContractBody(abc, "UnitRate", 1_000_000m));
        var contractId = contract.ContractId();

        // A signed contract and nothing executed: no cost, the whole value is committed.
        var signed = await api.CostFiguresAsync(cast.ProjectId);
        Assert.Equal(0m, signed.Recognized);
        Assert.Equal(1_000_000m, signed.Committed);
        Assert.Empty(await api.ItemTotalsAsync(cast.ProjectId));

        var steel = await api.AddItemAsync(cast.Manager, cast.ProjectId, contractId, quantity: 10m, unitPrice: 40_000m, unit: "Ton");
        var approved = await api.MeasuredAsync(cast.Manager, cast.ProjectId, contractId, steel.Id(), 6m);
        var rejected = await api.MeasuredAsync(cast.Manager, cast.ProjectId, contractId, steel.Id(), 1m);
        await api.MeasuredAsync(cast.Manager, cast.ProjectId, contractId, steel.Id(), 2m);

        Assert.Equal(240_000m, approved.Money("amount"));
        Assert.Equal("Recorded", approved.GetProperty("status").GetString());

        // Recorded work is not cost yet.
        var recorded = await api.CostFiguresAsync(cast.ProjectId);
        Assert.Equal(0m, recorded.Recognized);
        Assert.Equal(360_000m, recorded.PendingReview);
        Assert.Equal(3, recorded.PendingRecords);

        // The recorder can't approve their own measurement; the Executive can.
        var own = await api.ReviewAsync(cast.Manager, cast.ProjectId, "measurements", approved, "approve");
        Assert.Equal(HttpStatusCode.Forbidden, own.StatusCode); // the Manager has no subcontractors Approve
        var byExecutive = await api.ApprovedAsync(cast.Executive, cast.ProjectId, "measurements", approved);
        Assert.Equal("Approved", byExecutive.GetProperty("status").GetString());

        var noNote = await api.ReviewAsync(cast.Executive, cast.ProjectId, "measurements", rejected, "reject");
        Assert.Equal(HttpStatusCode.BadRequest, noNote.StatusCode);
        var rejection = await api.ReviewAsync(cast.Executive, cast.ProjectId, "measurements", rejected, "reject", "Not done yet");
        await EnsureStatus(rejection, HttpStatusCode.OK);

        var again = await api.ReviewAsync(cast.Executive, cast.ProjectId, "measurements", await Json(rejection), "approve");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("invalid_status", await CodeOf(again));

        var figures = await api.CostFiguresAsync(cast.ProjectId);
        Assert.Equal(240_000m, figures.Recognized);
        Assert.Equal(1, figures.RecognizedRecords);
        Assert.Equal(80_000m, figures.PendingReview);
        Assert.Equal(1, figures.PendingRecords);
        Assert.Equal(760_000m, figures.Committed);

        var totals = await api.ItemTotalsAsync(cast.ProjectId);
        Assert.Equal([new ItemCostTotal(null, 240_000m, 0m, 1, 1)], totals);

        var detail = await api.ContractAsync(cast.Executive, cast.ProjectId, contractId);
        var account = detail.Account();
        Assert.Equal(1_000_000m, account.Money("value"));
        Assert.Equal(240_000m, account.Money("executed"));
        Assert.Equal(80_000m, account.Money("pendingReview"));
        Assert.Equal(760_000m, account.Money("remainingToExecute"));
        Assert.Equal(240_000m, account.Money("dueNow"));

        var item = detail.GetProperty("items")[0];
        Assert.Equal(6m, item.Money("approvedQuantity"));
        Assert.Equal(2m, item.Money("pendingQuantity"));
        Assert.Equal(2m, item.Money("remainingQuantity"));
        Assert.Equal(400_000m, item.Money("lineTotal"));
        Assert.Equal(240_000m, item.Money("executedAmount"));
    }

    [DatabaseFact]
    public async Task Linking_a_contract_item_moves_its_approved_work_into_the_project_item_without_a_new_row()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var abc = await api.CreateCompanyAsync(cast.Executive, "ABC Construction");
        var contractId = (await api.CreateContractAsync(cast.Manager, cast.ProjectId, SubcontractsApi.ContractBody(abc, "UnitRate", 500_000m))).ContractId();
        var slab = (await api.CreateProjectItemAsync(cast.Executive, cast.ProjectId, "Slab concrete")).Id();

        var line = await api.AddItemAsync(cast.Manager, cast.ProjectId, contractId, 100m, 1_000m);
        await api.ApprovedAsync(cast.Executive, cast.ProjectId, "measurements", await api.MeasuredAsync(cast.Manager, cast.ProjectId, contractId, line.Id(), 30m, "2026-02-01"));
        await api.ApprovedAsync(cast.Executive, cast.ProjectId, "measurements", await api.MeasuredAsync(cast.Manager, cast.ProjectId, contractId, line.Id(), 20m, "2026-02-15"));
        await api.PaidAsync(cast.Accountant, cast.ProjectId, contractId, 35_000m);

        Assert.Equal([new ItemCostTotal(null, 50_000m, 35_000m, 2, 1)], await api.ItemTotalsAsync(cast.ProjectId));
        Assert.False(await api.AskAsync<ISubcontractItemCosts, bool>(costs => costs.HasLinksAsync(cast.ProjectId, slab, CancellationToken.None)));
        var rowsBefore = await api.QuerySubAsync(db => db.ProgressMeasurements.CountAsync());

        var linked = await api.LinkItemAsync(cast.Manager, cast.ProjectId, contractId, line, slab);
        await EnsureStatus(linked, HttpStatusCode.OK);
        Assert.Equal(slab, (await Json(linked)).GetProperty("projectItemId").GetInt32());

        // The same two measurements now count on the slab item; nothing was copied.
        Assert.Equal([new ItemCostTotal(slab, 50_000m, 35_000m, 2, 1)], await api.ItemTotalsAsync(cast.ProjectId));
        Assert.Equal(rowsBefore, await api.QuerySubAsync(db => db.ProgressMeasurements.CountAsync()));
        Assert.True(await api.AskAsync<ISubcontractItemCosts, bool>(costs => costs.HasLinksAsync(cast.ProjectId, slab, CancellationToken.None)));

        var lines = await api.AskAsync<ISubcontractItemCosts, IReadOnlyList<SubcontractItemLine>>(costs => costs.LinesAsync(cast.ProjectId, slab, CancellationToken.None));
        Assert.Equal(2, lines.Count);
        Assert.Equal(new DateOnly(2026, 2, 15), lines[0].Date);
        Assert.Equal(20_000m, lines[0].Amount);
        Assert.Equal(5_000m, lines[0].Paid);
        Assert.Equal(PaymentStates.PartiallyPaid, lines[0].PaymentState);
        Assert.Equal(30_000m, lines[1].Paid);
        Assert.Empty(await api.AskAsync<ISubcontractItemCosts, IReadOnlyList<SubcontractItemLine>>(costs => costs.LinesAsync(cast.ProjectId, null, CancellationToken.None)));

        // The project's items screen shows the work on the item, and the item can no longer be deleted.
        var delete = await api.SendAsync(HttpMethod.Delete, $"/api/projects/{cast.ProjectId}/items/{slab}", cast.Executive);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);

        // The cost of the project did not change by linking.
        Assert.Equal(50_000m, (await api.CostFiguresAsync(cast.ProjectId)).Recognized);

        // Unlinking puts it back in "not linked yet".
        var unlinked = await api.LinkItemAsync(cast.Manager, cast.ProjectId, contractId, await Json(linked), null);
        await EnsureStatus(unlinked, HttpStatusCode.OK);
        Assert.Equal([new ItemCostTotal(null, 50_000m, 35_000m, 2, 1)], await api.ItemTotalsAsync(cast.ProjectId));
    }

    [DatabaseFact]
    public async Task A_link_needs_an_active_item_of_the_same_project()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("PROJ-A");
        var projectB = (await api.CreateProjectAsync(cast.Executive, "PROJ-B", cast.ManagerId)).GetProperty("id").GetInt32();
        var abc = await api.CreateCompanyAsync(cast.Executive, "ABC Construction");
        var contractId = (await api.CreateContractAsync(cast.Manager, cast.ProjectId, SubcontractsApi.ContractBody(abc, "UnitRate", 500_000m))).ContractId();
        var line = await api.AddItemAsync(cast.Manager, cast.ProjectId, contractId, 100m, 1_000m);

        var otherProjectItem = (await api.CreateProjectItemAsync(cast.Executive, projectB, "Elsewhere")).Id();
        var inactive = await api.CreateProjectItemAsync(cast.Executive, cast.ProjectId, "Old item");
        var deactivated = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/items/{inactive.Id()}/deactivate", cast.Executive,
            new { rowVersion = inactive.GetProperty("rowVersion").GetString() });
        await EnsureStatus(deactivated, HttpStatusCode.OK);

        var elsewhere = await api.LinkItemAsync(cast.Manager, cast.ProjectId, contractId, line, otherProjectItem);
        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);
        Assert.Equal(["projectItemId"], await AlMotaheda.Api.Tests.ApiCalls.ErrorFields(elsewhere));

        var off = await api.LinkItemAsync(cast.Manager, cast.ProjectId, contractId, line, inactive.Id());
        Assert.Equal(HttpStatusCode.Conflict, off.StatusCode);
        Assert.Equal("item_inactive", await CodeOf(off));
    }

    [DatabaseFact]
    public async Task One_company_on_two_projects_keeps_each_contract_and_its_records_apart()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("PROJ-A");
        var projectB = (await api.CreateProjectAsync(cast.Executive, "PROJ-B", cast.ManagerId)).GetProperty("id").GetInt32();
        var abc = await api.CreateCompanyAsync(cast.Executive, "ABC Construction");

        var onA = (await api.CreateContractAsync(cast.Manager, cast.ProjectId, SubcontractsApi.ContractBody(abc, "UnitRate", 1_000_000m, new() { ["contractNo"] = "SC/A" }))).ContractId();
        var onB = (await api.CreateContractAsync(cast.Manager, projectB, SubcontractsApi.ContractBody(abc, "UnitRate", 300_000m, new() { ["contractNo"] = "SC/B" }))).ContractId();

        // One contract per company per project, and a contract number is used once.
        var twice = await api.PostContractAsync(cast.Manager, cast.ProjectId, SubcontractsApi.ContractBody(abc, "LumpSum", 5m));
        Assert.Equal("already_contracted", await CodeOf(twice));
        var other = await api.CreateCompanyAsync(cast.Executive, "Delta MEP", "Mep");
        var sameNo = await api.PostContractAsync(cast.Manager, cast.ProjectId, SubcontractsApi.ContractBody(other, "UnitRate", 5m, new() { ["contractNo"] = "SC/B" }));
        Assert.Equal("contract_no_taken", await CodeOf(sameNo));

        var lineA = await api.AddItemAsync(cast.Manager, cast.ProjectId, onA, 10m, 1_000m);
        var measuredA = await api.ApprovedAsync(cast.Executive, cast.ProjectId, "measurements", await api.MeasuredAsync(cast.Manager, cast.ProjectId, onA, lineA.Id(), 4m));
        var paymentA = await api.PaidAsync(cast.Accountant, cast.ProjectId, onA, 1_000m);

        // Project B sees only its own contract, with nothing executed or paid.
        var listB = await api.ContractsAsync(cast.Executive, projectB);
        var contractB = Assert.Single(listB.GetProperty("items").EnumerateArray());
        Assert.Equal(onB, contractB.Id());
        Assert.Equal(300_000m, contractB.GetProperty("account").Money("value"));
        Assert.Equal(0m, contractB.GetProperty("account").Money("executed"));
        Assert.Equal(0m, contractB.GetProperty("account").Money("paidTotal"));
        Assert.Equal(0m, (await api.CostFiguresAsync(projectB)).Recognized);
        Assert.Equal(300_000m, (await api.CostFiguresAsync(projectB)).Committed);
        Assert.Empty(await api.ItemTotalsAsync(projectB));
        Assert.Equal(4_000m, (await api.CostFiguresAsync(cast.ProjectId)).Recognized);

        // Every id of project A answers 404 through project B's routes.
        var rowVersion = measuredA.GetProperty("rowVersion").GetString();
        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, $"/api/projects/{projectB}/subcontracts/{onA}", null),
                     (HttpMethod.Put, $"/api/projects/{projectB}/subcontracts/{onA}", SubcontractsApi.ContractBody(abc, "UnitRate", 2_000_000m, new() { ["rowVersion"] = rowVersion })),
                     (HttpMethod.Post, $"/api/projects/{projectB}/subcontracts/{onA}/items", new { name = "x", workType = "Concrete", unit = "M3", quantity = 1, unitPrice = 1 }),
                     (HttpMethod.Put, $"/api/projects/{projectB}/subcontracts/{onA}/items/{lineA.Id()}", new { name = "x", workType = "Concrete", unit = "M3", quantity = 10, unitPrice = 1, rowVersion }),
                     (HttpMethod.Put, $"/api/projects/{projectB}/subcontracts/{onB}/items/{lineA.Id()}/project-item", new { projectItemId = (int?)null, rowVersion }),
                     (HttpMethod.Delete, $"/api/projects/{projectB}/subcontracts/{onA}/items/{lineA.Id()}", null),
                     (HttpMethod.Post, $"/api/projects/{projectB}/subcontracts/{onA}/measurements", new { contractItemId = lineA.Id(), measuredOn = "2026-03-01", quantity = 1 }),
                     (HttpMethod.Post, $"/api/projects/{projectB}/subcontract-measurements/{measuredA.Id()}/reject", new { note = "x", rowVersion }),
                     (HttpMethod.Post, $"/api/projects/{projectB}/subcontracts/{onA}/payments", SubcontractsApi.PaymentBody(1m)),
                     (HttpMethod.Post, $"/api/projects/{projectB}/subcontract-payments/{paymentA.Id()}/void", new { reason = "x", rowVersion = paymentA.GetProperty("rowVersion").GetString() }),
                 })
        {
            var response = await api.SendAsync(method, path, cast.Executive, body);
            Assert.True(response.StatusCode == HttpStatusCode.NotFound, $"{method} {path} answered {(int)response.StatusCode}.");
        }

        // A measurement on project B can't name project A's contract item either.
        var lineB = await api.AddItemAsync(cast.Manager, projectB, onB, 10m, 1_000m);
        var crossItem = await api.MeasureAsync(cast.Manager, projectB, onB, lineA.Id(), 1m);
        Assert.Equal(HttpStatusCode.BadRequest, crossItem.StatusCode);
        Assert.NotEqual(lineA.Id(), lineB.Id());

        // Nothing moved: project A still has exactly its one approved measurement and one payment.
        Assert.Equal(1, await api.QuerySubAsync(db => db.ProgressMeasurements.CountAsync()));
        Assert.Equal(1, await api.QuerySubAsync(db => db.SubPayments.CountAsync(payment => payment.VoidedAt == null)));
    }

    [DatabaseFact]
    public async Task A_manager_sees_only_the_contracts_of_projects_they_are_on()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("PROJ-A");
        var projectB = (await api.CreateProjectAsync(cast.Executive, "PROJ-B", cast.Executive.UserId)).GetProperty("id").GetInt32();
        var abc = await api.CreateCompanyAsync(cast.Executive, "ABC Construction");
        var onB = (await api.CreateContractAsync(cast.Executive, projectB, SubcontractsApi.ContractBody(abc, "UnitRate", 300_000m))).ContractId();

        var list = await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectB}/subcontracts", cast.Manager);
        var detail = await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectB}/subcontracts/{onB}", cast.Manager);

        Assert.Equal(HttpStatusCode.NotFound, list.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
    }

    [DatabaseFact]
    public async Task Measured_quantity_never_passes_the_item_quantity_at_recording_or_at_approval()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var abc = await api.CreateCompanyAsync(cast.Executive, "ABC Construction");
        var contractId = (await api.CreateContractAsync(cast.Manager, cast.ProjectId, SubcontractsApi.ContractBody(abc, "LumpSum", 3_600_000m))).ContractId();
        var elevators = await api.AddItemAsync(cast.Manager, cast.ProjectId, contractId, 1m, 3_600_000m, unit: "Job");

        // A Job line is measured as a percentage of the line.
        var thirtyFive = await api.MeasureAsync(cast.Manager, cast.ProjectId, contractId, elevators.Id(), null, percentOfLine: 35m);
        await EnsureStatus(thirtyFive, HttpStatusCode.Created);
        var first = await Json(thirtyFive);
        Assert.Equal(0.35m, first.Money("quantity"));
        Assert.Equal(35m, first.Money("percentOfLine"));
        Assert.Equal(1_260_000m, first.Money("amount"));

        var second = await Json(await api.MeasureAsync(cast.Manager, cast.ProjectId, contractId, elevators.Id(), null, percentOfLine: 60m));

        // 35 % + 60 % recorded: only 5 % is left.
        var tooMuch = await api.MeasureAsync(cast.Manager, cast.ProjectId, contractId, elevators.Id(), null, percentOfLine: 5.1m);
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);
        Assert.Equal("exceeds_contract_quantity", await CodeOf(tooMuch));

        // A rejected measurement frees its quantity.
        await EnsureStatus(await api.ReviewAsync(cast.Executive, cast.ProjectId, "measurements", second, "reject", "Shaft 2 not finished"), HttpStatusCode.OK);
        var rest = await api.MeasureAsync(cast.Manager, cast.ProjectId, contractId, elevators.Id(), null, percentOfLine: 65m);
        await EnsureStatus(rest, HttpStatusCode.Created);

        // The quantity can't be cut below what is measured, and the unit can't change.
        var shrink = await api.SendAsync(HttpMethod.Put, $"/api/projects/{cast.ProjectId}/subcontracts/{contractId}/items/{elevators.Id()}", cast.Manager, new
        {
            name = "Elevators", workType = "Mep", unit = "Lot", quantity = 0.5m, unitPrice = 3_600_000m, rowVersion = elevators.GetProperty("rowVersion").GetString(),
        });
        Assert.Equal(HttpStatusCode.BadRequest, shrink.StatusCode);
        Assert.Equal(["quantity", "unit"], (await AlMotaheda.Api.Tests.ApiCalls.ErrorFields(shrink)).Order());

        // The item can't be deleted once measured.
        var delete = await api.SendAsync(HttpMethod.Delete, $"/api/projects/{cast.ProjectId}/subcontracts/{contractId}/items/{elevators.Id()}", cast.Manager);
        Assert.Equal("item_has_measurements", await CodeOf(delete));

        // A unit-rate line can't take a percentage, and a daily-rate contract can't take measurements.
        var plain = await api.AddItemAsync(cast.Manager, cast.ProjectId, contractId, 10m, 100m);
        var percentOnM3 = await api.MeasureAsync(cast.Manager, cast.ProjectId, contractId, plain.Id(), null, percentOfLine: 10m);
        Assert.Equal(HttpStatusCode.BadRequest, percentOnM3.StatusCode);
        var crew = (await api.CreateContractAsync(cast.Manager, cast.ProjectId, SubcontractsApi.ContractBody(await api.CreateCompanyAsync(cast.Executive, "Crew Co"), "DailyRate", 10_000m))).ContractId();
        var wrongModel = await api.MeasureAsync(cast.Manager, cast.ProjectId, crew, plain.Id(), 1m);
        Assert.Equal("wrong_contract_model", await CodeOf(wrongModel));
    }

    [DatabaseFact]
    public async Task Manpower_sheets_are_computed_by_the_server_and_stay_under_the_ceiling()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var crewCo = await api.CreateCompanyAsync(cast.Executive, "Plaster Crew", "Finishing");
        var contractId = (await api.CreateContractAsync(cast.Manager, cast.ProjectId, SubcontractsApi.ContractBody(crewCo, "DailyRate", 200_000m))).ContractId();

        // 16 workers × 3 days × 400 (the contract's rate).
        var sheet = await api.ManpowerAsync(cast.Manager, cast.ProjectId, contractId, 16, 3m);
        Assert.Equal(19_200m, sheet.Money("amount"));
        Assert.Equal(400m, sheet.Money("dailyRate"));

        var big = await api.ManpowerAsync(cast.Manager, cast.ProjectId, contractId, 40, 10m);
        Assert.Equal(160_000m, big.Money("amount"));

        // 179,200 recorded: 20,800 left under the ceiling.
        var over = await api.RecordManpowerAsync(cast.Manager, cast.ProjectId, contractId, 10, 6m);
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        Assert.Equal("exceeds_contract_ceiling", await CodeOf(over));

        // Rejecting frees the ceiling.
        await EnsureStatus(await api.ReviewAsync(cast.Executive, cast.ProjectId, "manpower", big, "reject", "Wrong crew size"), HttpStatusCode.OK);
        await api.ManpowerAsync(cast.Manager, cast.ProjectId, contractId, 10, 6m);

        // Manpower belongs to daily-rate contracts only.
        var unitRate = (await api.CreateContractAsync(cast.Manager, cast.ProjectId, SubcontractsApi.ContractBody(await api.CreateCompanyAsync(cast.Executive, "ABC"), "UnitRate", 10_000m))).ContractId();
        var wrongModel = await api.RecordManpowerAsync(cast.Manager, cast.ProjectId, unitRate, 1, 1m);
        Assert.Equal("wrong_contract_model", await CodeOf(wrongModel));

        await api.ApprovedAsync(cast.Executive, cast.ProjectId, "manpower", sheet);
        var figures = await api.CostFiguresAsync(cast.ProjectId);
        Assert.Equal(19_200m, figures.Recognized);
        Assert.Equal(24_000m, figures.PendingReview);
    }

    [DatabaseFact]
    public async Task An_advance_is_kept_unapplied_then_settles_work_approved_later_oldest_first()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var abc = await api.CreateCompanyAsync(cast.Executive, "ABC Construction");
        var contractId = (await api.CreateContractAsync(cast.Manager, cast.ProjectId, SubcontractsApi.ContractBody(abc, "UnitRate", 100_000m))).ContractId();
        var line = await api.AddItemAsync(cast.Manager, cast.ProjectId, contractId, 100m, 1_000m);

        // An advance before any work: paid, but applied to nothing and never cost.
        var advance = await api.PaidAsync(cast.Accountant, cast.ProjectId, contractId, 25_000m, "2026-01-15", "Advance");
        Assert.Equal(0m, advance.Money("applied"));
        Assert.Equal(25_000m, advance.Money("unapplied"));
        Assert.Equal(0m, (await api.CostFiguresAsync(cast.ProjectId)).Recognized);

        // Two measurements approved: the newer one first, but the advance settles the older one first.
        var march = await api.MeasuredAsync(cast.Manager, cast.ProjectId, contractId, line.Id(), 20m, "2026-03-01");
        var february = await api.MeasuredAsync(cast.Manager, cast.ProjectId, contractId, line.Id(), 10m, "2026-02-01");
        var marchApproved = await api.ApprovedAsync(cast.Executive, cast.ProjectId, "measurements", march);
        Assert.Equal(20_000m, marchApproved.Money("paid"));
        var februaryApproved = await api.ApprovedAsync(cast.Executive, cast.ProjectId, "measurements", february);
        Assert.Equal(5_000m, februaryApproved.Money("paid"));

        var account = (await api.ContractAsync(cast.Executive, cast.ProjectId, contractId)).Account();
        Assert.Equal(30_000m, account.Money("executed"));
        Assert.Equal(25_000m, account.Money("paidTotal"));
        Assert.Equal(25_000m, account.Money("appliedToWork"));
        Assert.Equal(0m, account.Money("unappliedAdvance"));
        Assert.Equal(5_000m, account.Money("outstandingWork"));
        Assert.Equal(5_000m, account.Money("dueNow"));

        // A new payment settles the oldest open work: what is left on February.
        var progress = await api.PaidAsync(cast.Accountant, cast.ProjectId, contractId, 8_000m, "2026-03-20");
        Assert.Equal(5_000m, progress.Money("applied"));
        Assert.Equal(3_000m, progress.Money("unapplied"));
        var allocation = Assert.Single(progress.GetProperty("allocations").EnumerateArray());
        Assert.Equal("Measurement", allocation.GetProperty("workType").GetString());
        Assert.Equal(february.Id(), allocation.GetProperty("workId").GetInt32());

        // Payments can never pass the contract value.
        var tooMuch = await api.PayAsync(cast.Accountant, cast.ProjectId, contractId, SubcontractsApi.PaymentBody(67_000.01m));
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);
        Assert.Equal("exceeds_contract_value", await CodeOf(tooMuch));

        // Allocations never exceed the work they settle.
        var perWork = await api.QuerySubAsync(db => db.SubPaymentAllocations
            .Where(row => row.Payment.VoidedAt == null)
            .GroupBy(row => row.MeasurementId)
            .Select(group => new { group.Key, Total = group.Sum(row => row.Amount) })
            .ToListAsync());
        Assert.Equal(10_000m, perWork.Single(row => row.Key == february.Id()).Total);
        Assert.Equal(20_000m, perWork.Single(row => row.Key == march.Id()).Total);
    }

    [DatabaseFact]
    public async Task A_voided_payment_frees_its_work_and_other_unapplied_money_moves_to_it()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var abc = await api.CreateCompanyAsync(cast.Executive, "ABC Construction");
        var contractId = (await api.CreateContractAsync(cast.Manager, cast.ProjectId, SubcontractsApi.ContractBody(abc, "UnitRate", 100_000m))).ContractId();
        var line = await api.AddItemAsync(cast.Manager, cast.ProjectId, contractId, 100m, 100m);
        var work = await api.ApprovedAsync(cast.Executive, cast.ProjectId, "measurements", await api.MeasuredAsync(cast.Manager, cast.ProjectId, contractId, line.Id(), 100m, "2026-02-01"));

        // Payment 1 (5,000) settles half; payment 2 (8,000) settles the rest and keeps 3,000 unapplied.
        var first = await api.PaidAsync(cast.Accountant, cast.ProjectId, contractId, 5_000m, "2026-02-10");
        var second = await api.PaidAsync(cast.Accountant, cast.ProjectId, contractId, 8_000m, "2026-02-20");
        Assert.Equal(5_000m, second.Money("applied"));
        Assert.Equal(3_000m, second.Money("unapplied"));

        // Only payments Approve voids (the Executive by default), with a reason.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.VoidAsync(cast.Accountant, cast.ProjectId, first)).StatusCode);
        var voided = await api.VoidAsync(cast.Executive, cast.ProjectId, first, "Cheque bounced");
        await EnsureStatus(voided, HttpStatusCode.OK);
        var voidedBody = await Json(voided);
        Assert.True(voidedBody.GetProperty("isVoided").GetBoolean());
        Assert.Equal(0m, voidedBody.Money("applied"));

        // Payment 2's 3,000 moved to the freed work; its row on that work grew instead of a second row.
        var detail = await api.ContractAsync(cast.Executive, cast.ProjectId, contractId);
        var measurement = detail.GetProperty("measurements")[0];
        Assert.Equal(8_000m, measurement.Money("paid"));
        Assert.Equal(2_000m, measurement.Money("outstanding"));
        var secondNow = detail.GetProperty("payments").EnumerateArray().Single(payment => payment.Id() == second.Id());
        Assert.Equal(8_000m, secondNow.Money("applied"));
        Assert.Equal(0m, secondNow.Money("unapplied"));
        var row = Assert.Single(secondNow.GetProperty("allocations").EnumerateArray());
        Assert.Equal(8_000m, row.Money("amount"));

        var account = detail.Account();
        Assert.Equal(8_000m, account.Money("paidTotal"));
        Assert.Equal(8_000m, account.Money("appliedToWork"));
        Assert.Equal(0m, account.Money("unappliedAdvance"));
        Assert.Equal(2_000m, account.Money("dueNow"));
        Assert.Equal(8_000m, (await api.CostFiguresAsync(cast.ProjectId)).Paid);
        Assert.Equal(work.Id(), measurement.Id());

        // A void happens once.
        var twice = await api.VoidAsync(cast.Executive, cast.ProjectId, voidedBody);
        Assert.Equal("payment_already_voided", await CodeOf(twice));
    }

    [DatabaseFact]
    public async Task The_same_idempotency_key_returns_the_first_request_and_a_different_payment_with_it_is_refused()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var abc = await api.CreateCompanyAsync(cast.Executive, "ABC Construction");
        var delta = await api.CreateCompanyAsync(cast.Executive, "Delta MEP", "Mep");
        var contractId = (await api.CreateContractAsync(cast.Manager, cast.ProjectId, SubcontractsApi.ContractBody(abc, "UnitRate", 100_000m))).ContractId();
        var otherContract = (await api.CreateContractAsync(cast.Manager, cast.ProjectId, SubcontractsApi.ContractBody(delta, "UnitRate", 100_000m))).ContractId();
        var key = Guid.NewGuid();

        var first = await api.PayAsync(cast.Accountant, cast.ProjectId, contractId, SubcontractsApi.PaymentBody(3_000m, key: key));
        await EnsureStatus(first, HttpStatusCode.Created);
        var created = await Json(first);
        Assert.Equal($"/api/payment-requests/{created.Id()}", first.Headers.Location?.OriginalString);
        Assert.Equal("Submitted", created.GetProperty("status").GetString());

        // A retry of the same payment (a lost response) gets the first request back.
        var retry = await api.PayAsync(cast.Accountant, cast.ProjectId, contractId, SubcontractsApi.PaymentBody(3_000m, key: key));
        await EnsureStatus(retry, HttpStatusCode.OK);
        Assert.Equal(created.Id(), (await Json(retry)).Id());
        Assert.Equal(3_000m, (await Json(retry)).Money("amount"));

        // A different payment sent with the same key is refused, so it can't be lost silently.
        var different = await api.PayAsync(cast.Accountant, cast.ProjectId, contractId, SubcontractsApi.PaymentBody(7_000m, key: key));
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
        Assert.Equal("idempotency_key_reused", await CodeOf(different));

        var elsewhere = await api.PayAsync(cast.Accountant, cast.ProjectId, otherContract, SubcontractsApi.PaymentBody(1m, key: key));
        Assert.Equal("idempotency_key_reused", await CodeOf(elsewhere));

        // Requested is not paid: no payment row until the request is approved and confirmed.
        Assert.Equal(0, await api.QuerySubAsync(db => db.SubPayments.CountAsync()));
    }

    [DatabaseFact]
    public async Task A_contract_value_cannot_go_below_executed_work_or_payments_and_its_model_is_fixed_once_work_exists()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var abc = await api.CreateCompanyAsync(cast.Executive, "ABC Construction");
        var detail = await api.CreateContractAsync(cast.Manager, cast.ProjectId, SubcontractsApi.ContractBody(abc, "UnitRate", 100_000m));
        var contractId = detail.ContractId();
        var line = await api.AddItemAsync(cast.Manager, cast.ProjectId, contractId, 100m, 500m);
        await api.ApprovedAsync(cast.Executive, cast.ProjectId, "measurements", await api.MeasuredAsync(cast.Manager, cast.ProjectId, contractId, line.Id(), 50m));
        await api.PaidAsync(cast.Accountant, cast.ProjectId, contractId, 30_000m);
        var rowVersion = (await api.ContractAsync(cast.Manager, cast.ProjectId, contractId)).GetProperty("contract").GetProperty("rowVersion").GetString();

        var below = await api.SendAsync(HttpMethod.Put, $"/api/projects/{cast.ProjectId}/subcontracts/{contractId}", cast.Manager,
            SubcontractsApi.ContractBody(abc, "LumpSum", 24_999.99m, new() { ["rowVersion"] = rowVersion }));
        Assert.Equal(HttpStatusCode.BadRequest, below.StatusCode);
        Assert.Equal(["model", "value"], (await AlMotaheda.Api.Tests.ApiCalls.ErrorFields(below)).Order());

        var completed = await api.SendAsync(HttpMethod.Put, $"/api/projects/{cast.ProjectId}/subcontracts/{contractId}", cast.Manager,
            SubcontractsApi.ContractBody(abc, "UnitRate", 30_000m, new() { ["rowVersion"] = rowVersion, ["status"] = "Completed", ["retentionPercent"] = 5m }));
        await EnsureStatus(completed, HttpStatusCode.OK);
        var account = (await Json(completed)).Account();
        Assert.Equal(0m, account.Money("retentionHeld"));
        Assert.Equal(5_000m, account.Money("remainingToExecute"));

        // A completed contract takes no new work and is no longer a commitment.
        var more = await api.MeasureAsync(cast.Manager, cast.ProjectId, contractId, line.Id(), 1m);
        Assert.Equal("contract_completed", await CodeOf(more));
        Assert.Equal(0m, (await api.CostFiguresAsync(cast.ProjectId)).Committed);
    }

    [DatabaseFact]
    public async Task Companies_are_unique_by_english_name_and_an_inactive_one_gets_no_new_contract()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var abc = await api.CreateCompanyAsync(cast.Executive, "ABC Construction");

        var duplicate = await api.SendAsync(HttpMethod.Post, "/api/subcontractors", cast.Executive, new { nameAr = "أخرى", nameEn = "ABC Construction", specialty = "Civil" });
        Assert.Equal("subcontractor_exists", await CodeOf(duplicate));

        var company = await Json(await api.SendAsync(HttpMethod.Get, $"/api/subcontractors/{abc}", cast.Manager));
        var deactivate = await api.SendAsync(HttpMethod.Put, $"/api/subcontractors/{abc}", cast.Executive, new
        {
            nameAr = company.GetProperty("nameAr").GetString(),
            nameEn = "ABC Construction",
            specialty = "Civil",
            status = "Inactive",
            rowVersion = company.GetProperty("rowVersion").GetString(),
        });
        await EnsureStatus(deactivate, HttpStatusCode.OK);

        var refused = await api.PostContractAsync(cast.Manager, cast.ProjectId, SubcontractsApi.ContractBody(abc, "UnitRate", 1_000m));
        Assert.Equal("subcontractor_inactive", await CodeOf(refused));

        var list = await Json(await api.SendAsync(HttpMethod.Get, "/api/subcontractors?status=inactive&search=ABC", cast.Manager));
        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());
    }

    [DatabaseFact]
    public async Task Without_see_money_the_contract_screens_return_no_amounts()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var abc = await api.CreateCompanyAsync(cast.Executive, "ABC Construction");
        var contractId = (await api.CreateContractAsync(cast.Manager, cast.ProjectId, SubcontractsApi.ContractBody(abc, "UnitRate", 100_000m))).ContractId();
        var line = await api.AddItemAsync(cast.Manager, cast.ProjectId, contractId, 100m, 500m);
        await api.MeasuredAsync(cast.Manager, cast.ProjectId, contractId, line.Id(), 10m);

        // The project's manager keeps subcontractors View but loses See money.
        var current = await Json(await api.SendAsync(HttpMethod.Get, $"/api/users/{cast.ManagerId}/permissions", cast.Executive));
        var customise = await api.SendAsync(HttpMethod.Put, $"/api/users/{cast.ManagerId}/permissions/subcontractors", cast.Executive,
            new { actions = new[] { "View", "Create" }, rowVersion = current.GetProperty("rowVersion").GetString() });
        await EnsureStatus(customise, HttpStatusCode.OK);
        var refreshed = await Session.FromAsync(await api.Access.RefreshAsync(cast.Manager.RefreshCookie));

        var detail = await api.ContractAsync(refreshed, cast.ProjectId, contractId);
        var list = await api.ContractsAsync(refreshed, cast.ProjectId);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, list.GetProperty("totals").ValueKind);

        Assert.Equal(System.Text.Json.JsonValueKind.Null, detail.GetProperty("contract").GetProperty("value").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, detail.GetProperty("contract").GetProperty("account").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, detail.GetProperty("payments").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, detail.GetProperty("items")[0].GetProperty("unitPrice").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, detail.GetProperty("measurements")[0].GetProperty("amount").ValueKind);
        Assert.Equal(10m, detail.GetProperty("measurements")[0].Money("quantity"));
    }
}
