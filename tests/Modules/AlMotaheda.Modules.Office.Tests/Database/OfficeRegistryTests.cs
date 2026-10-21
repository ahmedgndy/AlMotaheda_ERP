using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Office.Tests.Database.OfficeApi;

namespace AlMotaheda.Modules.Office.Tests.Database;

/// <summary>
/// Section 15 and 19 (Office Manager) on SQL Server, with the office custody registered in Finance's custody
/// hierarchy: EGP 5,000 received, a 1,000 internet invoice with its receipt is pending (the balance still 5,000),
/// then approved and posted (4,000); the Executive's central custody page lists the office custodian with the same
/// figures, the expense and its receipt traceable from it; cash handed through a Finance transfer lands in the
/// same office ledger.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class OfficeRegistryTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task An_office_expense_is_pending_until_approved_and_the_central_custody_page_reconciles_the_office_custodian()
    {
        await using var api = await StartAsync(database);
        var cast = await api.CastAsync();
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var period = $"from={today.AddDays(-7):yyyy-MM-dd}&to={today:yyyy-MM-dd}";

        // The Office Manager receives EGP 5,000 (asked in Office, approved by the Accountant, paid through Finance, receipt confirmed).
        await api.FundAsync(cast, 5_000m);
        Assert.Equal(5_000m, (await api.BalanceAsync(cast.Custodian, cast.CustodyId)).Money("recorded"));

        // The office is registered in Finance's custody hierarchy by its custody id; the holder is read from the Office record.
        var registered = await api.SendAsync(HttpMethod.Post, "/api/custody-accounts", cast.Executive, new { kind = "Office", referenceId = cast.CustodyId });
        await EnsureStatus(registered, HttpStatusCode.Created);
        var account = await Json(registered);
        Assert.Equal(("Office", "Office", cast.Custodian.UserId, 5_000m, 0m), (account.GetProperty("kind").GetString(), account.GetProperty("ledgerOwner").GetString(),
            account.GetProperty("holderUserId").GetInt32(), account.Money("posted"), account.Money("pending")));
        Assert.Equal("custody_account_exists", await CodeOf(await api.SendAsync(HttpMethod.Post, "/api/custody-accounts", cast.Executive, new { kind = "Office", referenceId = cast.CustodyId })));

        // The internet subscription: 1,000 with a photo of the invoice. Pending: the balance is still 5,000, the exposure 1,000.
        var expense = await api.ExpenseAsync(cast, 1m, 1_000m, vendor: "ISP", invoiceNo: "NET-2026-10");
        Assert.Equal("Submitted", expense.Status());
        var pending = await api.BalanceAsync(cast.Custodian, cast.CustodyId);
        Assert.Equal((5_000m, 1_000m), (pending.Money("recorded"), pending.Money("pendingExpenses")));
        var registeredPending = (await Json(await api.SendAsync(HttpMethod.Get, $"/api/custody-accounts/{account.Id()}", cast.Executive))).GetProperty("account");
        Assert.Equal((5_000m, 1_000m, 4_000m), (registeredPending.Money("posted"), registeredPending.Money("pending"), registeredPending.Money("available")));
        var page = await Json(await api.SendAsync(HttpMethod.Get, $"/api/custody-monitor?{period}", cast.Executive));
        var row = Assert.Single(page.GetProperty("items").EnumerateArray(), item => item.GetProperty("accountId").GetInt32() == account.Id());
        Assert.Equal((5_000m, 1_000m, 1, 5_000m, 0m, "office_admin"), (row.Money("posted"), row.Money("pending"), row.GetProperty("pendingCount").GetInt32(), row.Money("receivedInPeriod"), row.Money("spentInPeriod"), row.GetProperty("holderRole").GetString()));

        // The Office Manager can't approve their own; the Accountant approves and the expense is posted: 4,000.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.DecideExpenseAsync(cast.Custodian, expense.Id(), "approve")).StatusCode);
        var approved = await api.DecidedExpenseAsync(cast.Accountant, expense.Id(), "approve");
        Assert.Equal(("Approved", "Posted"), (approved.Status(), approved.GetProperty("postingStatus").GetString()));
        Assert.Equal(4_000m, (await api.BalanceAsync(cast.Custodian, cast.CustodyId)).Money("recorded"));

        // The central page and the registry say the same, and the expense with its receipt is traceable from the page.
        var after = Assert.Single((await Json(await api.SendAsync(HttpMethod.Get, $"/api/custody-monitor?{period}", cast.Executive))).GetProperty("items").EnumerateArray(), item => item.GetProperty("accountId").GetInt32() == account.Id());
        Assert.Equal((4_000m, 0m, 1_000m, 0m, 0), (after.Money("posted"), after.Money("pending"), after.Money("spentInPeriod"), after.Money("unsupported"), after.GetProperty("unsupportedCount").GetInt32()));
        var detail = await Json(await api.SendAsync(HttpMethod.Get, $"/api/custody-monitor/{account.Id()}?{period}", cast.Executive));
        var line = Assert.Single(detail.GetProperty("statement").EnumerateArray(), item => item.GetProperty("sourceType").GetString() == "OfficeExpense");
        Assert.Equal((-1_000m, expense.Id(), true, "NET-2026-10"), (line.Money("amount"), line.GetProperty("sourceId").GetInt32(), line.GetProperty("hasEvidence").GetBoolean(), line.GetProperty("reference").GetString()));
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, $"/api/office/expenses/{expense.Id()}", cast.Executive)).StatusCode);
        Assert.Equal(4_000m, (await Json(await api.SendAsync(HttpMethod.Get, $"/api/custody-accounts/{account.Id()}", cast.Executive))).GetProperty("account").Money("posted"));

        // Cash handed through a Finance transfer from the Accountant's own custody lands in the same office ledger, only once received.
        var cash = (await Json(await api.SendAsync(HttpMethod.Get, "/api/treasury", cast.Executive))).GetProperty("accounts").EnumerateArray().Single(entry => entry.GetProperty("account").GetString() == "Cash");
        await EnsureStatus(await api.SendAsync(HttpMethod.Put, "/api/treasury/Cash/opening-balance", cast.Executive, new { openingBalance = 100_000m, openingDate = "2026-01-01", rowVersion = cash.GetProperty("rowVersion").GetString() }), HttpStatusCode.OK);
        var accountant = await api.CreatedAsync(cast.Accountant, "/api/custody-accounts", new { kind = "Accountant", holderUserId = cast.Accountant.UserId, nameAr = "عهدة المحاسب", nameEn = "Accountant's custody" });
        var funding = await Transferred(api, cast, new { amount = 10_000m, purpose = "CustodyAdvance", source = new { kind = "Treasury", treasuryAccount = "Cash" }, destination = new { kind = "Custody", accountId = accountant.Id() }, idempotencyKey = Guid.NewGuid().ToString() }, receiver: cast.Accountant);
        Assert.Equal("Received", funding.Status());
        var handed = await Transferred(api, cast, new { amount = 2_000m, purpose = "CustodyAdvance", source = new { kind = "Custody", accountId = accountant.Id() }, destination = new { kind = "Custody", accountId = account.Id() }, idempotencyKey = Guid.NewGuid().ToString() }, receiver: cast.Custodian);
        Assert.Equal("Received", handed.Status());
        Assert.Equal(6_000m, (await api.BalanceAsync(cast.Custodian, cast.CustodyId)).Money("recorded"));
        var ledger = await api.GetAsync(cast.Accountant, $"/api/office/custodies/{cast.CustodyId}/ledger");
        Assert.Contains(ledger.GetProperty("items").EnumerateArray(), entry => entry.GetProperty("sourceType").GetString() == "CustodyTransfer" && entry.Money("amount") == 2_000m);
        Assert.Equal(8_000m, (await Json(await api.SendAsync(HttpMethod.Get, $"/api/custody-accounts/{accountant.Id()}", cast.Accountant))).GetProperty("account").Money("posted"));
    }

    /// <summary>A transfer asked by the Accountant, approved by the Executive, handed out by the Accountant and received by <paramref name="receiver"/>.</summary>
    private static async Task<JsonElement> Transferred(AccessApi api, OfficeCast cast, object body, Session receiver)
    {
        var transfer = await api.CreatedAsync(cast.Accountant, "/api/custody-transfers", body);
        foreach (var (session, step) in new[] { (cast.Executive, "approve"), (cast.Accountant, "disburse"), (receiver, "receive") })
        {
            transfer = await api.OkAsync(session, HttpMethod.Post, $"/api/custody-transfers/{transfer.Id()}/{step}", new { rowVersion = transfer.RowVersion() });
        }

        return transfer;
    }
}
