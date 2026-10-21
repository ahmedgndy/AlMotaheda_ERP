using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database.Core;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Finance.Tests.Database;

/// <summary>
/// The company treasury and the custody hierarchy on SQL Server: the Executive sets the opening balance once; the
/// Accountant asks for cash, the Executive approves, the cash leaves the treasury and lands in the Accountant's
/// custody; the ledger and the reports show it as an internal transfer, never a payment; cash counts never move a
/// balance, adjustments do, period closes keep every entry and refuse anything dated inside them.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class TreasuryCustodyTests(SqlServerFixture database)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    [DatabaseFact]
    public async Task The_opening_balance_is_set_once_and_a_funding_moves_cash_from_the_treasury_to_the_accountants_custody()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();

        // A: nothing is set yet; the Executive sets the Cash opening balance; nobody sets it twice, nobody but an Executive.
        var before = await Treasury(api, cast.Accountant, "Cash");
        Assert.Equal((false, 0m, 0m), (before.GetProperty("openingSet").GetBoolean(), before.Money("openingBalance"), before.Money("balance")));
        var refused = await api.SendAsync(HttpMethod.Put, "/api/treasury/Cash/opening-balance", cast.Accountant, Opening(1_000_000m, before));
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        var opened = await api.SendAsync(HttpMethod.Put, "/api/treasury/Cash/opening-balance", cast.Executive, Opening(1_000_000m, before));
        await EnsureStatus(opened, HttpStatusCode.OK);
        var cash = await Json(opened);
        Assert.Equal((true, 1_000_000m, 1_000_000m, 1_000_000m), (cash.GetProperty("openingSet").GetBoolean(), cash.Money("openingBalance"), cash.Money("balance"), cash.Money("available")));
        Assert.Equal("opening_balance_set", await CodeOf(await api.SendAsync(HttpMethod.Put, "/api/treasury/Cash/opening-balance", cast.Executive, Opening(5m, cash))));

        // B: the Accountant's custody account, opened once.
        var account = await OpenAccountAsync(api, cast.Accountant, cast.Accountant.UserId);
        Assert.Equal(("Accountant", "Active", "Finance", 0m), (account.GetProperty("kind").GetString(), account.GetProperty("status").GetString(), account.GetProperty("ledgerOwner").GetString(), account.Money("posted")));
        Assert.StartsWith("CA-", account.GetProperty("number").GetString());
        Assert.Equal("custody_account_exists", await CodeOf(await api.SendAsync(HttpMethod.Post, "/api/custody-accounts", cast.Accountant, AccountBody(cast.Accountant.UserId))));

        // The Accountant asks for 60,000 out of the treasury; the same form twice is one request.
        var key = Guid.NewGuid();
        var requested = await api.SendAsync(HttpMethod.Post, "/api/custody-transfers", cast.Accountant, TreasuryFunding(account.Id(), 60_000m, key));
        await EnsureStatus(requested, HttpStatusCode.Created);
        var transfer = await Json(requested);
        Assert.Equal(("Requested", "CustodyAdvance", "Treasury", "Cash", "Custody"), (transfer.GetProperty("status").GetString(), transfer.GetProperty("purpose").GetString(),
            transfer.GetProperty("sourceKind").GetString(), transfer.GetProperty("sourceTreasuryAccount").GetString(), transfer.GetProperty("destinationKind").GetString()));
        var again = await api.SendAsync(HttpMethod.Post, "/api/custody-transfers", cast.Accountant, TreasuryFunding(account.Id(), 60_000m, key));
        Assert.Equal((HttpStatusCode.OK, transfer.Id()), (again.StatusCode, (await Json(again)).Id()));

        // Requested: the treasury shows it reserved; the balance is untouched.
        var reserved = await Treasury(api, cast.Accountant, "Cash");
        Assert.Equal((1_000_000m, 60_000m, 940_000m), (reserved.Money("balance"), reserved.Money("reserved"), reserved.Money("available")));

        // The Accountant can't approve (no payments Approve); the Executive does.
        Assert.Equal(HttpStatusCode.Forbidden, (await Step(api, cast.Accountant, transfer, "approve", new { note = "Please" })).StatusCode);
        transfer = await Stepped(api, cast.Executive, transfer, "approve", new { note = "Weekly cash" });
        Assert.Equal("Approved", transfer.GetProperty("status").GetString());
        Assert.Equal(0m, (await Account(api, cast.Accountant, account.Id())).Money("posted"));

        // Disbursed: the cash left the treasury (ledger), and is in transit to the account.
        transfer = await Stepped(api, cast.Accountant, transfer, "disburse", new { reference = "CASH-0042" });
        Assert.Equal(("Disbursed", "CASH-0042"), (transfer.GetProperty("status").GetString(), transfer.GetProperty("disbursementReference").GetString()));
        var disbursed = await Treasury(api, cast.Accountant, "Cash");
        Assert.Equal((940_000m, 0m, 940_000m, 60_000m), (disbursed.Money("balance"), disbursed.Money("reserved"), disbursed.Money("available"), disbursed.Money("internalTransfersOut")));
        var inTransit = await Account(api, cast.Accountant, account.Id());
        Assert.Equal((0m, 60_000m), (inTransit.Money("posted"), inTransit.Money("inTransit")));

        // Received: the account holds it; the balances only moved on disbursement and receipt.
        transfer = await Stepped(api, cast.Accountant, transfer, "receive", new { note = "Counted" });
        Assert.Equal("Received", transfer.GetProperty("status").GetString());
        Assert.Equal(["Request", "Approve", "Disburse", "Receive"], transfer.GetProperty("events").EnumerateArray().Select(step => step.GetProperty("action").GetString()));
        var funded = await Account(api, cast.Accountant, account.Id());
        Assert.Equal((60_000m, 0m, 0m, 60_000m), (funded.Money("posted"), funded.Money("pending"), funded.Money("inTransit"), funded.Money("available")));
        var statement = await Json(await api.SendAsync(HttpMethod.Get, $"/api/custody-accounts/{account.Id()}", cast.Accountant));
        var movement = Assert.Single(statement.GetProperty("movements").EnumerateArray());
        Assert.Equal(("TransferIn", "Posted", 60_000m, transfer.Id()), (movement.GetProperty("kind").GetString(), movement.GetProperty("status").GetString(), movement.Money("amount"), movement.GetProperty("transferId").GetInt32()));

        // C: the ledger has one entry, an internal transfer with no payment request behind it...
        var ledger = await Json(await api.SendAsync(HttpMethod.Get, "/api/ledger", cast.Accountant));
        var entry = Assert.Single(ledger.GetProperty("items").EnumerateArray());
        Assert.Equal((-60_000m, "Cash", "CustodyAdvance", "CustodyAccount", account.Id(), true), (entry.Money("amount"), entry.GetProperty("account").GetString(),
            entry.GetProperty("purpose").GetString(), entry.GetProperty("counterpartyType").GetString(), entry.GetProperty("counterpartyId").GetInt32(), entry.GetProperty("isInternalTransfer").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("paymentRequestId").ValueKind);
        Assert.Equal(transfer.Id(), entry.GetProperty("custodyTransferId").GetInt32());

        // ...and the reports keep it apart from payments.
        var cashReport = await Report(api, cast.Accountant, $"FIN-CASH?from={Iso(Today.AddDays(-7))}&to={Iso(Today.AddDays(1))}");
        Assert.Equal((0m, 60_000m, 0m, -60_000m), (Total(cashReport, "moneyOut"), Total(cashReport, "internalOut"), Total(cashReport, "paidFromCustody"), Total(cashReport, "net")));
        var treasuryReport = await Report(api, cast.Accountant, "FIN-TREASURY");
        var row = Rows(treasuryReport).Single(cells => cells.GetProperty("account").GetString() == "Cash");
        Assert.Equal((1_000_000m, 60_000m, 940_000m), (row.Money("openingBalance"), row.Money("internalOut"), row.Money("balance")));
        var custodians = await Report(api, cast.Accountant, "FIN-CUSTODY");
        Assert.Equal(60_000m, Rows(custodians).Single(cells => cells.GetProperty("number").GetString() == account.GetProperty("number").GetString()).Money("posted"));
        var ledgerReport = await Report(api, cast.Accountant, $"FIN-LEDGER?from={Iso(Today.AddDays(-7))}&to={Iso(Today.AddDays(1))}");
        Assert.Equal("Internal transfer", Assert.Single(Rows(ledgerReport)).GetProperty("kind").GetString());

        // The Executive's supervision view: who initiated the transfer, who decided it, the evidence, without asking anyone.
        var activity = await Report(api, cast.Executive, $"FIN-ACTIVITY?from={Iso(Today.AddDays(-7))}&to={Iso(Today)}");
        var step = Assert.Single(Rows(activity));
        Assert.Equal(("CustodyTransfer", transfer.GetProperty("number").GetString(), "Received", 60_000m, "Treasury (Cash)", account.GetProperty("number").GetString(), "CASH-0042"),
            (step.GetProperty("kind").GetString(), step.GetProperty("number").GetString(), step.GetProperty("status").GetString(), step.Money("amount"),
             step.GetProperty("source").GetString(), step.GetProperty("destination").GetString(), step.GetProperty("evidence").GetString()));
        Assert.Equal((JsonValueKind.String, JsonValueKind.String), (step.GetProperty("initiatedBy").ValueKind, step.GetProperty("decidedBy").ValueKind));
        Assert.Empty(Rows(await Report(api, cast.Executive, $"FIN-ACTIVITY?from={Iso(Today.AddDays(-7))}&to={Iso(Today)}&actorUserId={cast.Engineer.UserId}")));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, $"/api/reports/FIN-ACTIVITY?from={Iso(Today.AddDays(-7))}&to={Iso(Today)}", cast.Manager)).StatusCode);
    }

    [DatabaseFact]
    public async Task Nobody_reads_funds_approves_adjusts_or_reverses_without_the_right_and_nobody_decides_their_own()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        await OpenTreasuryAsync(api, cast.Executive, 1_000_000m);
        var account = await OpenAccountAsync(api, cast.Accountant, cast.Accountant.UserId);

        // R: the engineer (custody View, Create) opens no accounts and doesn't see the Accountant's.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, "/api/custody-accounts", cast.Engineer, AccountBody(cast.Engineer.UserId))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, $"/api/custody-accounts/{account.Id()}", cast.Engineer)).StatusCode);
        Assert.Empty((await Json(await api.SendAsync(HttpMethod.Get, "/api/custody-accounts", cast.Engineer))).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, "/api/treasury", cast.Engineer)).StatusCode);

        // Cash out of the treasury needs finance Create: the engineer can't ask for it, even into their own project custody.
        var engineerAsks = await api.SendAsync(HttpMethod.Post, "/api/custody-transfers", cast.Engineer, new
        {
            amount = 100m,
            source = new { kind = "Treasury", treasuryAccount = "Cash" },
            destination = new { kind = "Custody", accountKind = "EngineerProject", holderUserId = cast.Engineer.UserId, referenceId = cast.ProjectId },
            idempotencyKey = Guid.NewGuid().ToString(),
        });
        Assert.Equal(HttpStatusCode.Forbidden, engineerAsks.StatusCode);

        // The Executive may ask, but then can't approve their own request.
        var own = await Json(await api.SendAsync(HttpMethod.Post, "/api/custody-transfers", cast.Executive, TreasuryFunding(account.Id(), 100m, Guid.NewGuid())));
        Assert.Equal("own_record", await CodeOf(await Step(api, cast.Executive, own, "approve")));
        Assert.Equal(HttpStatusCode.Forbidden, (await Step(api, cast.Engineer, own, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Step(api, cast.Manager, own, "approve")).StatusCode);

        // A funding the Accountant asked for: someone who is neither a party nor a reviewer doesn't even see it (404), so can't
        // hand out or receive its cash; the Executive (a reviewer, finance Edit) could, the Accountant does.
        var transfer = await Json(await api.SendAsync(HttpMethod.Post, "/api/custody-transfers", cast.Accountant, TreasuryFunding(account.Id(), 60_000m, Guid.NewGuid())));
        transfer = await Stepped(api, cast.Executive, transfer, "approve");
        Assert.Equal(HttpStatusCode.NotFound, (await Step(api, cast.Manager, transfer, "disburse")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, $"/api/custody-transfers/{transfer.Id()}", cast.Engineer)).StatusCode);
        transfer = await Stepped(api, cast.Accountant, transfer, "disburse");
        Assert.Equal(HttpStatusCode.NotFound, (await Step(api, cast.Engineer, transfer, "receive")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Step(api, cast.Manager, transfer, "receive")).StatusCode);
        transfer = await Stepped(api, cast.Accountant, transfer, "receive");

        // Adjustments and reversals are a reviewer's; the holder never adjusts their own account.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/custody-accounts/{account.Id()}/adjustments", cast.Engineer, Adjustment(-10m, Today))).StatusCode);
        Assert.Equal("own_record", await CodeOf(await api.SendAsync(HttpMethod.Post, $"/api/custody-accounts/{account.Id()}/adjustments", cast.Accountant, Adjustment(-10m, Today))));
        Assert.Equal(HttpStatusCode.Forbidden, (await Step(api, cast.Engineer, transfer, "reverse", new { reason = "No" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Step(api, cast.Manager, transfer, "reverse", new { reason = "No" })).StatusCode);

        // A reversal by a reviewer puts the cash back: opposite movements, the ledger nets to zero.
        var reversed = await Stepped(api, cast.Executive, transfer, "reverse", new { reason = "Counted twice" });
        Assert.Equal(("Reversed", "Counted twice"), (reversed.GetProperty("status").GetString(), reversed.GetProperty("closeReason").GetString()));
        Assert.Equal(0m, (await Account(api, cast.Accountant, account.Id())).Money("posted"));
        var ledger = await Json(await api.SendAsync(HttpMethod.Get, "/api/ledger", cast.Accountant));
        Assert.Equal((2, 0m), (ledger.GetProperty("totalCount").GetInt32(), ledger.GetProperty("totals").Money("net")));
        Assert.Equal(1_000_000m, (await Treasury(api, cast.Accountant, "Cash")).Money("balance"));
    }

    [DatabaseFact]
    public async Task A_cash_count_never_moves_the_balance_an_adjustment_does_and_a_closed_period_keeps_every_entry_and_takes_no_more()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        await OpenTreasuryAsync(api, cast.Executive, 1_000_000m);
        var account = await OpenAccountAsync(api, cast.Accountant, cast.Accountant.UserId);
        await FundedAsync(api, cast, account.Id(), 60_000m, on: Today.AddDays(-3));

        // S: the Accountant counts 59,900: the difference is recorded; the balance stays at 60,000.
        var countKey = Guid.NewGuid();
        var counted = await api.SendAsync(HttpMethod.Post, $"/api/custody-accounts/{account.Id()}/cash-counts", cast.Accountant,
            new { countDate = Iso(Today.AddDays(-2)), countedAmount = 59_900m, notes = "Safe counted with the Executive", idempotencyKey = countKey });
        await EnsureStatus(counted, HttpStatusCode.Created);
        var count = await Json(counted);
        Assert.Equal((60_000m, 59_900m, -100m), (count.Money("bookBalance"), count.Money("countedAmount"), count.Money("difference")));
        Assert.Equal(60_000m, (await Account(api, cast.Accountant, account.Id())).Money("posted"));
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Post, $"/api/custody-accounts/{account.Id()}/cash-counts", cast.Accountant,
            new { countedAmount = 59_900m, idempotencyKey = countKey })).StatusCode);
        // Someone who can't see the account can't count it either (404, nothing leaks); the manager, who may count, isn't its holder (403).
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Post, $"/api/custody-accounts/{account.Id()}/cash-counts", cast.Engineer,
            new { countedAmount = 1m, idempotencyKey = Guid.NewGuid() })).StatusCode);

        // The one way the difference reaches the balance: a reviewer's adjustment with a reason and evidence, naming the count.
        var noEvidence = await api.SendAsync(HttpMethod.Post, $"/api/custody-accounts/{account.Id()}/adjustments", cast.Executive,
            new { amount = -100m, entryDate = Iso(Today.AddDays(-2)), reason = "Shortage at count", cashCountId = count.Id(), idempotencyKey = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.BadRequest, noEvidence.StatusCode);
        Assert.Contains("evidenceReference", (await Json(noEvidence)).GetProperty("errors").EnumerateObject().Select(field => field.Name));
        var adjustKey = Guid.NewGuid();
        var adjusted = await api.SendAsync(HttpMethod.Post, $"/api/custody-accounts/{account.Id()}/adjustments", cast.Executive,
            Adjustment(-100m, Today.AddDays(-2), cashCountId: count.Id(), key: adjustKey));
        await EnsureStatus(adjusted, HttpStatusCode.Created);
        Assert.Equal(59_900m, (await Json(adjusted)).Money("balanceAfter"));
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Post, $"/api/custody-accounts/{account.Id()}/adjustments", cast.Executive,
            Adjustment(-100m, Today.AddDays(-2), cashCountId: count.Id(), key: adjustKey))).StatusCode);
        Assert.Equal(59_900m, (await Account(api, cast.Accountant, account.Id())).Money("posted"));

        // T: the period up to yesterday is closed: a snapshot, every movement still there.
        var closed = await api.SendAsync(HttpMethod.Post, $"/api/custody-accounts/{account.Id()}/period-closes", cast.Executive, new { periodEnd = Iso(Today.AddDays(-1)), notes = "Week closed" });
        await EnsureStatus(closed, HttpStatusCode.Created);
        var close = await Json(closed);
        Assert.Equal((59_900m, 0m), (close.Money("closingBalance"), close.Money("pendingAtClose")));
        Assert.Equal("period_closed", await CodeOf(await api.SendAsync(HttpMethod.Post, $"/api/custody-accounts/{account.Id()}/period-closes", cast.Executive, new { periodEnd = Iso(Today.AddDays(-2)) })));
        var after = await Json(await api.SendAsync(HttpMethod.Get, $"/api/custody-accounts/{account.Id()}", cast.Accountant));
        Assert.Equal(2, after.GetProperty("movements").GetArrayLength());
        Assert.Equal(Iso(Today.AddDays(-1)), after.GetProperty("account").GetProperty("lastPeriodEnd").GetString());

        // Nothing is dated inside the closed period any more; today still is open.
        Assert.Equal("period_closed", await CodeOf(await api.SendAsync(HttpMethod.Post, $"/api/custody-accounts/{account.Id()}/adjustments", cast.Executive, Adjustment(5m, Today.AddDays(-1)))));
        var late = await Json(await api.SendAsync(HttpMethod.Post, "/api/custody-transfers", cast.Accountant, TreasuryFunding(account.Id(), 1_000m, Guid.NewGuid())));
        late = await Stepped(api, cast.Executive, late, "approve");
        late = await Stepped(api, cast.Accountant, late, "disburse");
        Assert.Equal("period_closed", await CodeOf(await Step(api, cast.Accountant, late, "receive", new { on = Iso(Today.AddDays(-1)) })));
        await Stepped(api, cast.Accountant, late, "receive", new { on = Iso(Today) });
        await EnsureStatus(await api.SendAsync(HttpMethod.Post, $"/api/custody-accounts/{account.Id()}/adjustments", cast.Executive, Adjustment(5m, Today)), HttpStatusCode.Created);
        var final = await Json(await api.SendAsync(HttpMethod.Get, $"/api/custody-accounts/{account.Id()}", cast.Accountant));
        Assert.Equal((4, 60_905m), (final.GetProperty("movements").GetArrayLength(), final.GetProperty("account").Money("posted")));

        var recon = await Report(api, cast.Accountant, $"FIN-CUSTODY-RECON?from={Iso(Today.AddDays(-7))}&to={Iso(Today)}");
        Assert.Equal(-100m, Rows(recon).Single(cells => cells.GetProperty("detail").GetString() == "Cash count" && cells.GetProperty("number").GetString() == account.GetProperty("number").GetString()).Money("difference"));
    }

    private static object Opening(decimal amount, JsonElement treasury) =>
        new { openingBalance = amount, openingDate = "2026-01-01", notes = "Opening per the auditors", rowVersion = treasury.GetProperty("rowVersion").GetString() };

    private static object AccountBody(int holderUserId) => new { kind = "Accountant", holderUserId, nameAr = "عهدة المحاسب", nameEn = "Accountant's custody" };

    private static object TreasuryFunding(int accountId, decimal amount, Guid key) => new
    {
        amount,
        purpose = "CustodyAdvance",
        source = new { kind = "Treasury", treasuryAccount = "Cash" },
        destination = new { kind = "Custody", accountId },
        notes = "Weekly cash",
        idempotencyKey = key.ToString(),
    };

    private static object Adjustment(decimal amount, DateOnly entryDate, int? cashCountId = null, Guid? key = null) => new
    {
        amount, entryDate = Iso(entryDate), reason = "Shortage at count", evidenceReference = "COUNT-SHEET-7", cashCountId, idempotencyKey = (key ?? Guid.NewGuid()).ToString(),
    };

    internal static async Task OpenTreasuryAsync(ProjectsApi api, Session executive, decimal amount)
    {
        var cash = await Treasury(api, executive, "Cash");
        await EnsureStatus(await api.SendAsync(HttpMethod.Put, "/api/treasury/Cash/opening-balance", executive, Opening(amount, cash)), HttpStatusCode.OK);
    }

    internal static async Task<JsonElement> OpenAccountAsync(ProjectsApi api, Session reviewer, int holderUserId)
    {
        var response = await api.SendAsync(HttpMethod.Post, "/api/custody-accounts", reviewer, AccountBody(holderUserId));
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    /// <summary>A funding out of the treasury into <paramref name="accountId"/>, received by the Accountant on <paramref name="on"/>.</summary>
    internal static async Task<JsonElement> FundedAsync(ProjectsApi api, CoreApi.Cast cast, int accountId, decimal amount, DateOnly? on = null)
    {
        var transfer = await Json(await api.SendAsync(HttpMethod.Post, "/api/custody-transfers", cast.Accountant, TreasuryFunding(accountId, amount, Guid.NewGuid())));
        transfer = await Stepped(api, cast.Executive, transfer, "approve");
        transfer = await Stepped(api, cast.Accountant, transfer, "disburse", new { on = on is { } day ? Iso(day) : null });
        return await Stepped(api, cast.Accountant, transfer, "receive", new { on = on is { } received ? Iso(received) : null });
    }

    internal static async Task<JsonElement> Treasury(ProjectsApi api, Session session, string account)
    {
        var response = await api.SendAsync(HttpMethod.Get, "/api/treasury", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return (await Json(response)).GetProperty("accounts").EnumerateArray().Single(row => row.GetProperty("account").GetString() == account);
    }

    internal static async Task<JsonElement> Account(ProjectsApi api, Session session, int accountId)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/custody-accounts/{accountId}", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return (await Json(response)).GetProperty("account");
    }

    internal static Task<HttpResponseMessage> Step(ProjectsApi api, Session session, JsonElement transfer, string step, object? extra = null)
    {
        var body = JsonSerializer.SerializeToElement(extra ?? new { }).EnumerateObject().ToDictionary(property => property.Name, property => (object?)property.Value);
        body["rowVersion"] = transfer.GetProperty("rowVersion").GetString();
        return api.SendAsync(HttpMethod.Post, $"/api/custody-transfers/{transfer.Id()}/{step}", session, body);
    }

    internal static async Task<JsonElement> Stepped(ProjectsApi api, Session session, JsonElement transfer, string step, object? extra = null)
    {
        var response = await Step(api, session, transfer, step, extra);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    internal static async Task<JsonElement> Report(ProjectsApi api, Session session, string codeAndQuery)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/reports/{codeAndQuery}", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    internal static List<JsonElement> Rows(JsonElement report) => report.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("cells")).ToList();

    internal static decimal Total(JsonElement report, string column) => report.GetProperty("totals").GetProperty(column).GetDecimal();

    internal static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
