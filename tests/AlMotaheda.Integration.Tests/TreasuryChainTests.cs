using System.Globalization;
using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database.Core;
using AlMotaheda.Modules.Workforce.Tests.Database;

namespace AlMotaheda.Integration.Tests;

/// <summary>
/// The custody chain end to end on SQL Server: treasury → the Accountant's custody → the engineer's project custody
/// (Finance's registry and Projects' ledger agree) → the weekly labor settlement that recognises wages once, under
/// concurrency; a payroll batch that never distributes more than Workforce says is owed; and a supplier invoice that
/// is never paid twice by any route.
/// </summary>
public static class TreasuryChainJson
{
    public static int IdOf(this JsonElement element) => element.GetProperty("id").GetInt32();
}

[Collection(SqlServerCollection.Name)]
public sealed class TreasuryChainTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task Cash_flows_treasury_to_accountant_to_engineer_and_both_modules_show_the_same_custody()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await WorkforceApi.CastAsync(api, "INT-CUS-01");
        await OpenTreasuryAsync(api, cast.Executive, 1_000_000m);
        var accountant = await OpenAccountAsync(api, cast.Accountant, cast.Accountant.UserId);
        await FundedAsync(api, cast.Executive, cast.Accountant, accountant.IdOf(), 60_000m);

        // D: the Accountant hands 2,000 to the site engineer for the project; the engineer's project custody is opened on the way.
        var handed = await api.SendAsync(HttpMethod.Post, "/api/custody-transfers", cast.Accountant, new
        {
            amount = 2_000m,
            purpose = "CustodyAdvance",
            source = new { kind = "Custody", accountId = accountant.IdOf() },
            destination = new { kind = "Custody", accountKind = "EngineerProject", holderUserId = cast.Engineer.UserId, referenceId = cast.ProjectId },
            idempotencyKey = Guid.NewGuid().ToString(),
        });
        await AccessApi.EnsureStatus(handed, HttpStatusCode.Created);
        var transfer = await AccessApi.Json(handed);
        Assert.Equal((cast.ProjectId, "Custody"), (transfer.GetProperty("projectId").GetInt32(), transfer.GetProperty("destinationKind").GetString()));
        var engineerAccount = transfer.GetProperty("destinationAccountId").GetInt32();
        transfer = await Stepped(api, cast.Executive, transfer, "approve");
        transfer = await Stepped(api, cast.Accountant, transfer, "disburse");

        // Until the engineer confirms, the cash is in transit: in neither balance.
        Assert.Equal(0m, (await ProjectCustody(api, cast.Accountant, cast.ProjectId, cast.Engineer.UserId)).GetProperty("balance").GetDecimal());
        var pending = await Account(api, cast.Accountant, engineerAccount);
        Assert.Equal((0m, 2_000m, "Projects"), (pending.GetProperty("posted").GetDecimal(), pending.GetProperty("inTransit").GetDecimal(), pending.GetProperty("ledgerOwner").GetString()));
        transfer = await Stepped(api, cast.Engineer, transfer, "receive");

        // Projects' custody ledger and Finance's account say the same; the treasury only ever saw the first transfer.
        var projectCustody = await ProjectCustody(api, cast.Accountant, cast.ProjectId, cast.Engineer.UserId);
        Assert.Equal((2_000m, 2_000m), (projectCustody.GetProperty("received").GetDecimal(), projectCustody.GetProperty("balance").GetDecimal()));
        Assert.Equal(2_000m, (await Account(api, cast.Engineer, engineerAccount)).GetProperty("posted").GetDecimal());
        Assert.Equal(58_000m, (await Account(api, cast.Accountant, accountant.IdOf())).GetProperty("posted").GetDecimal());
        var ledger = await Get(api, cast.Accountant, "/api/ledger");
        Assert.Equal((1, -60_000m), (ledger.GetProperty("totalCount").GetInt32(), ledger.GetProperty("totals").GetProperty("net").GetDecimal()));
        Assert.Equal(940_000m, (await Treasury(api, cast.Accountant)).GetProperty("balance").GetDecimal());

        // The engineer's own statement in Projects carries the transfer as a funding.
        var statement = await Get(api, cast.Engineer, $"/api/projects/{cast.ProjectId}/custody/{cast.Engineer.UserId}");
        var line = Assert.Single(statement.GetProperty("lines").EnumerateArray());
        Assert.Equal((2_000m, "CustodyTransfer"), (line.GetProperty("amount").GetDecimal(), line.GetProperty("sourceType").GetString()));

        // E: a week of approved labor on the project is owed, not paid: the transfer paid nobody.
        var worker = await WorkforceApi.HireAsync(api, cast.Executive, cast.ProjectId, WorkforceApi.Plumber, "Saad", dailyRate: 400m);
        await WorkforceApi.RecordApprovedSheetAsync(api, cast, cast.ProjectId, WorkforceApi.Today(api).AddDays(-1), WorkforceApi.Line(worker, 1m));
        var summary = await Get(api, cast.Accountant, $"/api/projects/{cast.ProjectId}/labor/summary");
        Assert.Equal((400m, 0m), (summary.GetProperty("approved").GetDecimal(), summary.GetProperty("paid").GetDecimal()));

        // F: the engineer settles the week from their custody; the Accountant confirms: wages recognised once, custody down by 400.
        var settlement = await Send(api, cast.Engineer, HttpMethod.Post, $"/api/projects/{cast.ProjectId}/labor/settlements", SettlementBody(api, Guid.NewGuid()), HttpStatusCode.Created);
        Assert.Equal((400m, "Pending"), (settlement.GetProperty("amount").GetDecimal(), settlement.GetProperty("status").GetString()));
        Assert.Equal(0m, (await Get(api, cast.Accountant, $"/api/projects/{cast.ProjectId}/labor/summary")).GetProperty("paid").GetDecimal());

        // G: four confirmations of the same settlement at the same moment: exactly one goes through.
        var confirmations = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Decide(api, cast.Accountant, cast.ProjectId, settlement, "confirm")));
        Assert.Equal(1, confirmations.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.All(confirmations.Where(response => response.StatusCode != HttpStatusCode.OK),
            response => Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.ServiceUnavailable }));

        Assert.Equal(400m, (await Get(api, cast.Accountant, $"/api/projects/{cast.ProjectId}/labor/summary")).GetProperty("paid").GetDecimal());
        var afterWages = await ProjectCustody(api, cast.Accountant, cast.ProjectId, cast.Engineer.UserId);
        Assert.Equal((1_600m, 400m), (afterWages.GetProperty("balance").GetDecimal(), afterWages.GetProperty("spentApproved").GetDecimal()));
        Assert.Equal(1_600m, (await Account(api, cast.Accountant, engineerAccount)).GetProperty("posted").GetDecimal());
        var payroll = await Get(api, cast.Accountant, $"/api/reports/WRK-PAYROLL?from={Iso(WorkforceApi.Today(api).AddDays(-10))}&to={Iso(WorkforceApi.Today(api))}");
        Assert.Equal((400m, 400m, 0m), (Total(payroll, "pay"), Total(payroll, "paid"), Total(payroll, "outstanding")));

        // The second settlement of the same week finds nothing to pay.
        var nothingLeft = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/labor/settlements", cast.Engineer, SettlementBody(api, Guid.NewGuid()));
        Assert.NotEqual(HttpStatusCode.Created, nothingLeft.StatusCode);
        Assert.Equal(400m, (await Get(api, cast.Accountant, $"/api/projects/{cast.ProjectId}/labor/summary")).GetProperty("paid").GetDecimal());
    }

    [DatabaseFact]
    public async Task A_week_owing_more_than_the_custody_holds_is_paid_in_two_settlements_and_counted_once()
    {
        // Section 12.C: the custody policy Block refuses to pay what the engineer doesn't hold; the week is paid in two parts.
        await using var api = await ProjectsApi.StartAsync(database, new Dictionary<string, string?> { ["Projects:CustodyBalanceCheck"] = "Block" });
        var cast = await WorkforceApi.CastAsync(api, "INT-WEEK-01");
        await OpenTreasuryAsync(api, cast.Executive, 1_000_000m);
        var accountant = await OpenAccountAsync(api, cast.Accountant, cast.Accountant.UserId);
        await FundedAsync(api, cast.Executive, cast.Accountant, accountant.IdOf(), 100_000m);
        var today = WorkforceApi.Today(api);

        // Five approved days of one worker at 11,000: 55,000 owed. Recording and approving the days pays nobody.
        var worker = await WorkforceApi.HireAsync(api, cast.Executive, cast.ProjectId, WorkforceApi.Plumber, "Saad", dailyRate: 11_000m);
        foreach (var daysAgo in Enumerable.Range(1, 5))
        {
            await WorkforceApi.RecordApprovedSheetAsync(api, cast, cast.ProjectId, today.AddDays(-daysAgo), WorkforceApi.Line(worker, 1m));
        }

        var owed = await Get(api, cast.Accountant, $"/api/projects/{cast.ProjectId}/labor/summary");
        Assert.Equal((55_000m, 0m, 55_000m), (owed.GetProperty("approved").GetDecimal(), owed.GetProperty("paid").GetDecimal(), owed.GetProperty("outstanding").GetDecimal()));

        // The engineer holds 50,000 (asked, approved, handed out, received). Asking for it moved nothing until it was received.
        var engineerAccount = await HandedToEngineerAsync(api, cast, accountant.IdOf(), 50_000m);
        Assert.Equal(50_000m, (await ProjectCustody(api, cast.Accountant, cast.ProjectId, cast.Engineer.UserId)).GetProperty("balance").GetDecimal());

        // Paying the whole week (55,000) is refused: the custody holds less. Paying 50,000 is recorded and confirmed.
        var tooMuch = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/labor/settlements", cast.Engineer, SettlementBody(api, Guid.NewGuid()));
        Assert.Equal("custody_insufficient", await AccessApi.CodeOf(tooMuch));
        var first = await Send(api, cast.Engineer, HttpMethod.Post, $"/api/projects/{cast.ProjectId}/labor/settlements", SettlementBody(api, Guid.NewGuid(), [(worker, 50_000m)]), HttpStatusCode.Created);
        Assert.Equal(("Pending", 50_000m), (first.GetProperty("status").GetString(), first.GetProperty("amount").GetDecimal()));
        Assert.Equal(0m, (await Get(api, cast.Accountant, $"/api/projects/{cast.ProjectId}/labor/summary")).GetProperty("paid").GetDecimal());
        await AccessApi.EnsureStatus(await Decide(api, cast.Accountant, cast.ProjectId, first, "confirm"), HttpStatusCode.OK);

        // Posted: custody 0, labor paid 50,000, outstanding 5,000.
        var afterFirst = await Get(api, cast.Accountant, $"/api/projects/{cast.ProjectId}/labor/summary");
        Assert.Equal((50_000m, 5_000m), (afterFirst.GetProperty("paid").GetDecimal(), afterFirst.GetProperty("outstanding").GetDecimal()));
        var custodyAfterFirst = await ProjectCustody(api, cast.Accountant, cast.ProjectId, cast.Engineer.UserId);
        Assert.Equal((0m, 50_000m), (custodyAfterFirst.GetProperty("balance").GetDecimal(), custodyAfterFirst.GetProperty("spentApproved").GetDecimal()));
        Assert.Equal(0m, (await Account(api, cast.Accountant, engineerAccount)).GetProperty("posted").GetDecimal());

        // A second transfer of 5,000: requested and approved it is in transit (balance still 0); received, the balance is 5,000.
        var top = await Send(api, cast.Accountant, HttpMethod.Post, "/api/custody-transfers", new
        {
            amount = 5_000m, purpose = "CustodyAdvance", source = new { kind = "Custody", accountId = accountant.IdOf() }, destination = new { kind = "Custody", accountId = engineerAccount }, idempotencyKey = Guid.NewGuid().ToString(),
        }, HttpStatusCode.Created);
        top = await Stepped(api, cast.Executive, top, "approve");
        top = await Stepped(api, cast.Accountant, top, "disburse");
        Assert.Equal(0m, (await ProjectCustody(api, cast.Accountant, cast.ProjectId, cast.Engineer.UserId)).GetProperty("balance").GetDecimal());
        await Stepped(api, cast.Engineer, top, "receive");
        Assert.Equal(5_000m, (await ProjectCustody(api, cast.Accountant, cast.ProjectId, cast.Engineer.UserId)).GetProperty("balance").GetDecimal());

        // The rest of the week is paid; nothing is counted twice: paid 55,000 in all, custody received 55,000 and spent 55,000.
        var second = await Send(api, cast.Engineer, HttpMethod.Post, $"/api/projects/{cast.ProjectId}/labor/settlements", SettlementBody(api, Guid.NewGuid()), HttpStatusCode.Created);
        Assert.Equal(5_000m, second.GetProperty("amount").GetDecimal());
        await AccessApi.EnsureStatus(await Decide(api, cast.Accountant, cast.ProjectId, second, "confirm"), HttpStatusCode.OK);
        var final = await Get(api, cast.Accountant, $"/api/projects/{cast.ProjectId}/labor/summary");
        Assert.Equal((55_000m, 55_000m, 0m), (final.GetProperty("approved").GetDecimal(), final.GetProperty("paid").GetDecimal(), final.GetProperty("outstanding").GetDecimal()));
        var custodyFinal = await ProjectCustody(api, cast.Accountant, cast.ProjectId, cast.Engineer.UserId);
        Assert.Equal((55_000m, 55_000m, 0m), (custodyFinal.GetProperty("received").GetDecimal(), custodyFinal.GetProperty("spentApproved").GetDecimal(), custodyFinal.GetProperty("balance").GetDecimal()));
        Assert.Equal(0m, (await Account(api, cast.Accountant, engineerAccount)).GetProperty("posted").GetDecimal());
        Assert.Equal(45_000m, (await Account(api, cast.Accountant, accountant.IdOf())).GetProperty("posted").GetDecimal());
        var nothingLeft = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/labor/settlements", cast.Engineer, SettlementBody(api, Guid.NewGuid()));
        Assert.NotEqual(HttpStatusCode.Created, nothingLeft.StatusCode);
    }

    [DatabaseFact]
    public async Task The_engineer_home_page_the_approval_queue_and_the_central_custody_page_read_the_same_ledgers()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await WorkforceApi.CastAsync(api, "INT-HOME-01");
        await OpenTreasuryAsync(api, cast.Executive, 1_000_000m);
        var accountant = await OpenAccountAsync(api, cast.Accountant, cast.Accountant.UserId);
        await FundedAsync(api, cast.Executive, cast.Accountant, accountant.IdOf(), 60_000m);
        var today = WorkforceApi.Today(api);
        var period = $"from={Iso(today.AddDays(-7))}&to={Iso(today)}";

        // Section 12.A: the engineer asks Projects for a 2,000 funding; the home page shows the project, no balance yet, one pending funding. A request is not a transfer.
        var asked = await Send(api, cast.Engineer, HttpMethod.Post, $"/api/projects/{cast.ProjectId}/custody/fundings",
            new { amount = 2_000m, method = "Cash", requestedDate = Iso(today), notes = "Week's site cash", idempotencyKey = Guid.NewGuid().ToString() }, HttpStatusCode.Created);
        var home = await Get(api, cast.Engineer, "/api/me/overview");
        Assert.Equal(cast.ProjectId, Assert.Single(home.GetProperty("projects").EnumerateArray()).GetProperty("projectId").GetInt32());
        Assert.Equal(0m, home.GetProperty("custodyBalanceTotal").GetDecimal());
        var funding = Assert.Single(home.GetProperty("sections").EnumerateArray(), section => section.GetProperty("section").GetString() == "Funding");
        Assert.Equal((1m, 2_000m), (funding.GetProperty("figures").GetProperty("pendingFundingCount").GetDecimal(), funding.GetProperty("figures").GetProperty("pendingFundingAmount").GetDecimal()));
        Assert.Equal(asked.GetProperty("id").GetInt32(), Assert.Single(funding.GetProperty("items").EnumerateArray()).GetProperty("recordId").GetInt32());

        // Section 18: the request is in the Executive's queue (payments Approve), with no project needed for company-wide items; the manager (payments View) sees no payment requests to decide.
        var queue = await Get(api, cast.Executive, "/api/inbox");
        Assert.Contains(queue.GetProperty("items").EnumerateArray(), item => item.GetProperty("recordType").GetString() == "PaymentRequest" && item.GetProperty("recordId").GetInt32() == asked.GetProperty("id").GetInt32());
        Assert.DoesNotContain((await Get(api, cast.Manager, "/api/inbox")).GetProperty("items").EnumerateArray(), item => item.GetProperty("recordType").GetString() == "PaymentRequest");

        // The Accountant hands 2,000 from their custody instead; while it is only requested the engineer's queue is empty, once disbursed it says "cash to receive".
        var transfer = await Send(api, cast.Accountant, HttpMethod.Post, "/api/custody-transfers", new
        {
            amount = 2_000m, purpose = "CustodyAdvance", source = new { kind = "Custody", accountId = accountant.IdOf() },
            destination = new { kind = "Custody", accountKind = "EngineerProject", holderUserId = cast.Engineer.UserId, referenceId = cast.ProjectId }, idempotencyKey = Guid.NewGuid().ToString(),
        }, HttpStatusCode.Created);
        Assert.Contains((await Get(api, cast.Executive, "/api/inbox")).GetProperty("items").EnumerateArray(), item => item.GetProperty("recordType").GetString() == "CustodyTransfer" && item.GetProperty("reason").GetString() == "Review" && item.GetProperty("projectId").GetInt32() == cast.ProjectId);
        Assert.DoesNotContain((await Get(api, cast.Engineer, "/api/inbox")).GetProperty("items").EnumerateArray(), item => item.GetProperty("recordType").GetString() == "CustodyTransfer");
        transfer = await Stepped(api, cast.Executive, transfer, "approve");
        transfer = await Stepped(api, cast.Accountant, transfer, "disburse");
        Assert.Contains((await Get(api, cast.Engineer, "/api/inbox")).GetProperty("items").EnumerateArray(), item => item.GetProperty("recordType").GetString() == "CustodyTransfer" && item.GetProperty("reason").GetString() == "Requested");
        await Stepped(api, cast.Engineer, transfer, "receive");
        var engineerAccount = transfer.GetProperty("destinationAccountId").GetInt32();
        Assert.Equal(2_000m, (await Get(api, cast.Engineer, "/api/me/overview")).GetProperty("custodyBalanceTotal").GetDecimal());

        // The engineer buys 300 of cement in cash with no receipt filed, and the Accountant (projects Approve) approves it: spent, but unsupported until a document is linked.
        var expense = await CoreApi.RecordApprovedAsync(api, cast.Engineer, cast.Accountant, cast.ProjectId, CoreApi.ExpenseBody(300m, "Custody", changes: new() { ["expenseDate"] = Iso(today) }));
        var monitor = await Get(api, cast.Executive, $"/api/custody-monitor?{period}");
        var row = Assert.Single(monitor.GetProperty("items").EnumerateArray(), item => item.GetProperty("accountId").GetInt32() == engineerAccount);
        Assert.Equal((1_700m, 2_000m, 300m, 300m, 1, "engineer", cast.ProjectId), (row.GetProperty("posted").GetDecimal(), row.GetProperty("receivedInPeriod").GetDecimal(), row.GetProperty("spentInPeriod").GetDecimal(),
            row.GetProperty("unsupported").GetDecimal(), row.GetProperty("unsupportedCount").GetInt32(), row.GetProperty("holderRole").GetString(), row.GetProperty("projectId").GetInt32()));
        var detail = await Get(api, cast.Executive, $"/api/custody-monitor/{engineerAccount}?{period}");
        var expenseLine = Assert.Single(detail.GetProperty("statement").EnumerateArray(), line => line.GetProperty("sourceType").GetString() == "ProjectExpense");
        Assert.Equal((-300m, false), (expenseLine.GetProperty("amount").GetDecimal(), expenseLine.GetProperty("hasEvidence").GetBoolean()));
        var accountantRow = Assert.Single(monitor.GetProperty("items").EnumerateArray(), item => item.GetProperty("accountId").GetInt32() == accountant.IdOf());
        Assert.Equal((58_000m, 2_000m, 0m), (accountantRow.GetProperty("posted").GetDecimal(), accountantRow.GetProperty("transferredOutInPeriod").GetDecimal(), accountantRow.GetProperty("spentInPeriod").GetDecimal()));

        // The receipt is filed in the Archive against the expense: supported now; the page and the statement agree.
        await UploadReceiptAsync(api, cast.Engineer, cast.ProjectId, CoreApi.Id(expense));
        var supported = Assert.Single((await Get(api, cast.Executive, $"/api/custody-monitor?{period}")).GetProperty("items").EnumerateArray(), item => item.GetProperty("accountId").GetInt32() == engineerAccount);
        Assert.Equal((0m, 0), (supported.GetProperty("unsupported").GetDecimal(), supported.GetProperty("unsupportedCount").GetInt32()));
        Assert.True(Assert.Single((await Get(api, cast.Executive, $"/api/custody-monitor/{engineerAccount}?{period}")).GetProperty("statement").EnumerateArray(), line => line.GetProperty("sourceType").GetString() == "ProjectExpense").GetProperty("hasEvidence").GetBoolean());

        // The home page is the engineer's own: the manager's page shows no custody, and the engineer's labor section counts their sheets.
        Assert.Empty((await Get(api, cast.Manager, "/api/me/overview")).GetProperty("custody").EnumerateArray());
        var worker = await WorkforceApi.HireAsync(api, cast.Executive, cast.ProjectId, WorkforceApi.Plumber, "Saad", dailyRate: 400m);
        await WorkforceApi.RecordApprovedSheetAsync(api, cast, cast.ProjectId, today.AddDays(-1), WorkforceApi.Line(worker, 1m));
        var labor = Assert.Single((await Get(api, cast.Engineer, "/api/me/overview")).GetProperty("sections").EnumerateArray(), section => section.GetProperty("section").GetString() == "Labor");
        Assert.Equal(1m, labor.GetProperty("figures").GetProperty("sheetsApprovedThisWeek").GetDecimal());
        Assert.Equal(0m, (await Get(api, cast.Accountant, $"/api/projects/{cast.ProjectId}/labor/summary")).GetProperty("paid").GetDecimal());
    }

    [DatabaseFact]
    public async Task A_payroll_batch_distributes_no_more_than_its_allocation_or_than_workforce_says_is_owed()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await WorkforceApi.CastAsync(api, "INT-PB-01");
        await OpenTreasuryAsync(api, cast.Executive, 1_000_000m);
        var worker = await WorkforceApi.HireAsync(api, cast.Executive, cast.ProjectId, WorkforceApi.Plumber, "Saad", dailyRate: 400m);
        await WorkforceApi.RecordApprovedSheetAsync(api, cast, cast.ProjectId, WorkforceApi.Today(api).AddDays(-1), WorkforceApi.Line(worker, 1m));
        var from = WorkforceApi.Today(api).AddDays(-6);
        var to = WorkforceApi.Today(api);

        // An allocation above what Workforce says is owed is refused at once.
        var tooMuch = await api.SendAsync(HttpMethod.Post, "/api/payroll-batches", cast.Accountant, Batch(from, to, [(cast.ProjectId, 900m)]));
        Assert.Equal("over_distribution", await AccessApi.CodeOf(tooMuch));

        var batch = await Send(api, cast.Accountant, HttpMethod.Post, "/api/payroll-batches", Batch(from, to, [(cast.ProjectId, 400m)]), HttpStatusCode.Created);
        Assert.Equal(("DailyLabor", "Open", 400m, 400m), (batch.GetProperty("kind").GetString(), batch.GetProperty("status").GetString(),
            batch.GetProperty("entitlementApproved").GetDecimal(), batch.GetProperty("entitlementOutstanding").GetDecimal()));
        Assert.StartsWith("PB-", batch.GetProperty("number").GetString());
        var batchAccount = batch.GetProperty("accountId").GetInt32();

        // Fund the batch from the treasury, then distribute to the engineer: 500 is over the allocation, 400 fits, 1 more is over what is owed.
        await FundedAsync(api, cast.Executive, cast.Accountant, batchAccount, 400m, batch.IdOf());
        Assert.Equal("over_distribution", await AccessApi.CodeOf(await api.SendAsync(HttpMethod.Post, "/api/custody-transfers", cast.Accountant, Distribution(batchAccount, batch.IdOf(), cast, 500m))));
        var distribution = await Send(api, cast.Accountant, HttpMethod.Post, "/api/custody-transfers", Distribution(batchAccount, batch.IdOf(), cast, 400m), HttpStatusCode.Created);
        distribution = await Stepped(api, cast.Executive, distribution, "approve");
        distribution = await Stepped(api, cast.Accountant, distribution, "disburse");
        await Stepped(api, cast.Engineer, distribution, "receive");
        Assert.Equal("over_distribution", await AccessApi.CodeOf(await api.SendAsync(HttpMethod.Post, "/api/custody-transfers", cast.Accountant, Distribution(batchAccount, batch.IdOf(), cast, 1m))));

        // The reconciliation: funded 400, distributed 400, nothing left in the batch; the project's row says the same.
        var view = await Get(api, cast.Accountant, $"/api/payroll-batches/{batch.IdOf()}");
        Assert.Equal((400m, 400m, 0m, 0m), (view.GetProperty("funded").GetDecimal(), view.GetProperty("distributed").GetDecimal(), view.GetProperty("batchBalance").GetDecimal(), view.GetProperty("discrepancy").GetDecimal()));
        var project = Assert.Single(view.GetProperty("projects").EnumerateArray());
        Assert.Equal((400m, 400m, 0m, 1), (project.GetProperty("allocated").GetDecimal(), project.GetProperty("distributed").GetDecimal(), project.GetProperty("remaining").GetDecimal(), project.GetProperty("transfers").GetInt32()));
        Assert.Equal(cast.Engineer.UserId, Assert.Single(view.GetProperty("custodians").EnumerateArray()).GetProperty("holderUserId").GetInt32());

        // Only the Accountant's own settlement confirmation marks the wages paid: the batch never did.
        Assert.Equal(0m, (await Get(api, cast.Accountant, $"/api/projects/{cast.ProjectId}/labor/summary")).GetProperty("paid").GetDecimal());
        var settlement = await Send(api, cast.Engineer, HttpMethod.Post, $"/api/projects/{cast.ProjectId}/labor/settlements", SettlementBody(api, Guid.NewGuid()), HttpStatusCode.Created);
        await AccessApi.EnsureStatus(await Decide(api, cast.Accountant, cast.ProjectId, settlement, "confirm"), HttpStatusCode.OK);
        var reconciled = await Get(api, cast.Accountant, $"/api/payroll-batches/{batch.IdOf()}");
        Assert.Equal((400m, 0m), (reconciled.GetProperty("entitlementPaid").GetDecimal(), reconciled.GetProperty("entitlementOutstanding").GetDecimal()));
        var report = await Get(api, cast.Accountant, "/api/reports/FIN-PAYROLL-BATCH");
        Assert.Equal(400m, Rows(report).Single(cells => cells.GetProperty("number").GetString() == batch.GetProperty("number").GetString()).GetProperty("distributed").GetDecimal());

        // Closed: no more distributions.
        var closed = await Send(api, cast.Accountant, HttpMethod.Post, $"/api/payroll-batches/{batch.IdOf()}/close", new { note = "Week paid", rowVersion = reconciled.GetProperty("rowVersion").GetString() });
        Assert.Equal("Closed", closed.GetProperty("status").GetString());
        // Closing the batch closes its (empty) account too: whichever check answers first, nothing moves.
        Assert.Contains(await AccessApi.CodeOf(await api.SendAsync(HttpMethod.Post, "/api/custody-transfers", cast.Accountant, Distribution(batchAccount, batch.IdOf(), cast, 1m))),
            new[] { "batch_closed", "custody_account_closed" });
    }

    [DatabaseFact]
    public async Task A_supplier_invoice_is_never_paid_twice_by_a_second_request_nor_by_two_confirmations()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await CoreApi.CastAsync(api, "INT-SUP-02");
        var supplier = await CoreApi.CreateSupplierAsync(api, cast.Executive, "Cemex");
        var invoice = await CoreApi.RecordApprovedAsync(api, cast.Engineer, cast.Accountant, cast.ProjectId, CoreApi.SupplyBody(supplier, 10m, 100m, "Credit"));

        // Q: paid in full once; a second request on the same invoice has nothing left to pay.
        await CoreApi.PaidAsync(api, cast.Accountant, cast.ProjectId, supplier, 1_000m, (CoreApi.Id(invoice), 1_000m));
        Assert.Equal(1_000m, CoreApi.Money(await CoreApi.GetExpenseAsync(api, cast.Executive, cast.ProjectId, CoreApi.Id(invoice)), "paid"));
        var second = await CoreApi.PayAsync(api, cast.Accountant, cast.ProjectId, CoreApi.PaymentBody(supplier, 1_000m, [(CoreApi.Id(invoice), 1_000m)]));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        // Another invoice, approved and sent to the bank: four confirmations at once pay it once.
        var other = await CoreApi.RecordApprovedAsync(api, cast.Engineer, cast.Accountant, cast.ProjectId, CoreApi.SupplyBody(supplier, 5m, 100m, "Credit"));
        var request = await Send(api, cast.Accountant, HttpMethod.Post, $"/api/projects/{cast.ProjectId}/supplier-payments", CoreApi.PaymentBody(supplier, 500m, [(CoreApi.Id(other), 500m)]), HttpStatusCode.Created);
        var requestId = request.GetProperty("id").GetInt32();
        await RequestStep(api, cast.Executive, requestId, "approve");
        await RequestStep(api, cast.Accountant, requestId, "send-to-bank");
        var confirmations = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => RequestStep(api, cast.Accountant, requestId, "confirm-payment", new { reference = "SWIFT-9" })));
        Assert.Equal(1, confirmations.Count(response => response.StatusCode == HttpStatusCode.OK));

        Assert.Equal(500m, CoreApi.Money(await CoreApi.GetExpenseAsync(api, cast.Executive, cast.ProjectId, CoreApi.Id(other)), "paid"));
        var ledger = await Get(api, cast.Accountant, $"/api/ledger?projectId={cast.ProjectId}");
        Assert.Equal((2, -1_500m), (ledger.GetProperty("totalCount").GetInt32(), ledger.GetProperty("totals").GetProperty("net").GetDecimal()));
        var balances = await CoreApi.BalancesAsync(api, cast.Accountant, cast.ProjectId);
        Assert.Equal(0m, balances.GetProperty("items").EnumerateArray().Single(row => row.GetProperty("supplierId").GetInt32() == supplier).GetProperty("outstanding").GetDecimal());
    }

    private static object SettlementBody(ProjectsApi api, Guid key, (int WorkerId, decimal Amount)[]? lines = null) => new
    {
        periodFrom = Iso(WorkforceApi.Today(api).AddDays(-6)),
        periodTo = Iso(WorkforceApi.Today(api)),
        paidOn = Iso(WorkforceApi.Today(api)),
        reference = "Week sheet 42",
        idempotencyKey = key.ToString(),
        lines = lines?.Select(line => new { workerId = line.WorkerId, amount = line.Amount }).ToArray(),
    };

    /// <summary>The Accountant hands <paramref name="amount"/> to the engineer's project custody (opened on the way); returns the Finance account id.</summary>
    private static async Task<int> HandedToEngineerAsync(ProjectsApi api, WorkforceApi.Cast cast, int accountantAccount, decimal amount)
    {
        var transfer = await Send(api, cast.Accountant, HttpMethod.Post, "/api/custody-transfers", new
        {
            amount,
            purpose = "CustodyAdvance",
            source = new { kind = "Custody", accountId = accountantAccount },
            destination = new { kind = "Custody", accountKind = "EngineerProject", holderUserId = cast.Engineer.UserId, referenceId = cast.ProjectId },
            idempotencyKey = Guid.NewGuid().ToString(),
        }, HttpStatusCode.Created);
        transfer = await Stepped(api, cast.Executive, transfer, "approve");
        transfer = await Stepped(api, cast.Accountant, transfer, "disburse");
        transfer = await Stepped(api, cast.Engineer, transfer, "receive");
        return transfer.GetProperty("destinationAccountId").GetInt32();
    }

    private static object Batch(DateOnly from, DateOnly to, (int ProjectId, decimal Amount)[] allocations) => new
    {
        kind = "DailyLabor",
        periodFrom = Iso(from),
        periodTo = Iso(to),
        nameAr = "دفعة أجور الأسبوع",
        nameEn = "Week labor batch",
        allocations = allocations.Select(line => new { projectId = line.ProjectId, amount = line.Amount }).ToArray(),
    };

    private static object Distribution(int batchAccount, int batchId, WorkforceApi.Cast cast, decimal amount) => new
    {
        amount,
        purpose = "CustodyAdvance",
        payrollBatchId = batchId,
        source = new { kind = "Custody", accountId = batchAccount },
        destination = new { kind = "Custody", accountKind = "EngineerProject", holderUserId = cast.Engineer.UserId, referenceId = cast.ProjectId },
        idempotencyKey = Guid.NewGuid().ToString(),
    };

    private static Task<HttpResponseMessage> Decide(ProjectsApi api, Session session, int projectId, JsonElement settlement, string action) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/labor/settlements/{settlement.GetProperty("id").GetInt32()}/{action}", session,
            new { rowVersion = settlement.GetProperty("rowVersion").GetString() });

    private static async Task<HttpResponseMessage> RequestStep(ProjectsApi api, Session session, int requestId, string step, object? extra = null)
    {
        var detail = await Get(api, session, $"/api/payment-requests/{requestId}");
        var body = JsonSerializer.SerializeToElement(extra ?? new { }).EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value);
        body["rowVersion"] = detail.GetProperty("rowVersion").GetString();
        return await api.SendAsync(HttpMethod.Post, $"/api/payment-requests/{requestId}/{step}", session, body);
    }

    private static async Task OpenTreasuryAsync(ProjectsApi api, Session executive, decimal amount)
    {
        var cash = await Treasury(api, executive);
        await Send(api, executive, HttpMethod.Put, "/api/treasury/Cash/opening-balance",
            new { openingBalance = amount, openingDate = "2026-01-01", rowVersion = cash.GetProperty("rowVersion").GetString() });
    }

    private static Task<JsonElement> OpenAccountAsync(ProjectsApi api, Session reviewer, int holderUserId) =>
        Send(api, reviewer, HttpMethod.Post, "/api/custody-accounts", new { kind = "Accountant", holderUserId, nameAr = "عهدة المحاسب", nameEn = "Accountant's custody" }, HttpStatusCode.Created);

    /// <summary>Cash out of the treasury into <paramref name="accountId"/>: asked and handled by the Accountant, approved by the Executive.</summary>
    private static async Task<JsonElement> FundedAsync(ProjectsApi api, Session executive, Session accountant, int accountId, decimal amount, int? payrollBatchId = null)
    {
        var transfer = await Send(api, accountant, HttpMethod.Post, "/api/custody-transfers", new
        {
            amount,
            purpose = "CustodyAdvance",
            payrollBatchId,
            source = new { kind = "Treasury", treasuryAccount = "Cash" },
            destination = new { kind = "Custody", accountId },
            idempotencyKey = Guid.NewGuid().ToString(),
        }, HttpStatusCode.Created);
        transfer = await Stepped(api, executive, transfer, "approve");
        transfer = await Stepped(api, accountant, transfer, "disburse");
        return await Stepped(api, accountant, transfer, "receive");
    }

    private static async Task<JsonElement> Stepped(ProjectsApi api, Session session, JsonElement transfer, string step) =>
        await Send(api, session, HttpMethod.Post, $"/api/custody-transfers/{transfer.GetProperty("id").GetInt32()}/{step}", new { rowVersion = transfer.GetProperty("rowVersion").GetString() });

    private static async Task<JsonElement> Treasury(ProjectsApi api, Session session) =>
        (await Get(api, session, "/api/treasury")).GetProperty("accounts").EnumerateArray().Single(row => row.GetProperty("account").GetString() == "Cash");

    private static async Task<JsonElement> Account(ProjectsApi api, Session session, int accountId) =>
        (await Get(api, session, $"/api/custody-accounts/{accountId}")).GetProperty("account");

    private static async Task<JsonElement> ProjectCustody(ProjectsApi api, Session session, int projectId, int holderUserId) =>
        (await Get(api, session, $"/api/projects/{projectId}/custody/{holderUserId}")).GetProperty("custody");

    /// <summary>Files a receipt photo in the project archive against the expense (the Archive's upload form).</summary>
    private static async Task UploadReceiptAsync(ProjectsApi api, Session session, int projectId, int expenseId)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("Cement receipt"), "title");
        content.Add(new StringContent("Receipt"), "category");
        content.Add(new StringContent("Expense"), "entityType");
        content.Add(new StringContent(expenseId.ToString(CultureInfo.InvariantCulture)), "entityId");
        var file = new ByteArrayContent([0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, .. new byte[40], 0xFF, 0xD9]);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "file", "receipt.jpg");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{projectId}/documents") { Content = content };
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", session.AccessToken);
        var response = await api.Access.App.Client.SendAsync(request);
        await AccessApi.EnsureStatus(response, HttpStatusCode.Created);
    }

    private static List<JsonElement> Rows(JsonElement report) => report.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("cells")).ToList();

    private static decimal Total(JsonElement report, string column) => report.GetProperty("totals").GetProperty(column).GetDecimal();

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static Task<JsonElement> Get(ProjectsApi api, Session session, string path) => Send(api, session, HttpMethod.Get, path, null);

    private static async Task<JsonElement> Send(ProjectsApi api, Session session, HttpMethod method, string path, object? body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await api.SendAsync(method, path, session, body);
        await AccessApi.EnsureStatus(response, expected);
        return await AccessApi.Json(response);
    }
}
