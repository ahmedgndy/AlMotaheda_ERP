using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database.Core;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Finance.Tests.Database.TreasuryCustodyTests;

namespace AlMotaheda.Modules.Finance.Tests.Database;

/// <summary>
/// Sections 13, 16 and 17 on SQL Server: the Executive's central custody page reconciles every holder from the
/// ledgers; a reviewer's adjustment is a separate, auditable, disputable transaction that never overwrites a
/// balance and never touches payroll; the audit trail answers who changed what, keeps removed permissions, and
/// refuses every update or delete at the database.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class GovernanceTests(SqlServerFixture database)
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    [DatabaseFact]
    public async Task The_central_custody_page_lists_every_holder_from_the_ledgers_and_an_adjustment_is_a_disputable_transaction()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        await OpenTreasuryAsync(api, cast.Executive, 1_000_000m);
        var account = await OpenAccountAsync(api, cast.Accountant, cast.Accountant.UserId);
        await FundedAsync(api, cast, account.Id(), 60_000m);
        var period = $"from={Iso(Today.AddDays(-7))}&to={Iso(Today)}";

        // Section 16: the page shows the Accountant's custody with what it holds and what came in this period; the manager (custody View only) has no page.
        var page = await Json(await api.SendAsync(HttpMethod.Get, $"/api/custody-monitor?{period}", cast.Executive));
        var row = Assert.Single(page.GetProperty("items").EnumerateArray(), item => item.GetProperty("accountId").GetInt32() == account.Id());
        Assert.Equal((60_000m, 60_000m, 0m, 0m, "accountant"), (row.Money("posted"), row.Money("receivedInPeriod"), row.Money("spentInPeriod"), row.Money("unsupported"), row.GetProperty("holderRole").GetString()));
        Assert.Equal(60_000m, page.GetProperty("totals").Money("posted"));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, "/api/custody-monitor", cast.Manager)).StatusCode);
        var detail = await Json(await api.SendAsync(HttpMethod.Get, $"/api/custody-monitor/{account.Id()}?{period}", cast.Accountant));
        var line = Assert.Single(detail.GetProperty("statement").EnumerateArray());
        Assert.Equal(("TransferIn", 60_000m, true), (line.GetProperty("kind").GetString(), line.Money("amount"), line.GetProperty("hasEvidence").GetBoolean()));

        // Section 16.A: the Executive charges 1,500 spent without a receipt back to the Accountant's custody. A separate transaction with a reason and evidence.
        var charged = await api.SendAsync(HttpMethod.Post, $"/api/custody-accounts/{account.Id()}/adjustments", cast.Executive,
            new { amount = -1_500m, entryDate = Iso(Today), reason = "Taxi fares claimed without receipts", evidenceReference = "CASE-2026-14", idempotencyKey = Guid.NewGuid() });
        await EnsureStatus(charged, HttpStatusCode.Created);
        var adjustment = await Json(charged);
        Assert.Equal(("Posted", 58_500m, 0m, cast.Accountant.UserId), (adjustment.GetProperty("status").GetString(), adjustment.Money("balanceAfter"), adjustment.Money("shortfall"), adjustment.GetProperty("holderUserId").GetInt32()));
        Assert.Equal(58_500m, (await Account(api, cast.Accountant, account.Id())).Money("posted"));

        // Nobody overwrote a balance: the ledger has the funding and the adjustment; the audit trail has the adjustment row with its reason.
        var statement = await Json(await api.SendAsync(HttpMethod.Get, $"/api/custody-accounts/{account.Id()}", cast.Executive));
        Assert.Equal(["TransferIn", "Adjustment"], statement.GetProperty("movements").EnumerateArray().Select(movement => movement.GetProperty("kind").GetString()));
        var audited = await Json(await api.SendAsync(HttpMethod.Get, $"/api/audit-entries?entityType=CustodyAdjustment&entityKey={adjustment.Id()}&action=Added", cast.Executive));
        var entry = Assert.Single(audited.GetProperty("items").EnumerateArray());
        Assert.Equal(cast.Executive.UserId, entry.GetProperty("userId").GetInt32());
        Assert.Equal("Taxi fares claimed without receipts", entry.GetProperty("changes").GetProperty("Reason")[1].GetString());

        // Nothing reached payroll: an adjustment is a custody matter, a deduction is HR's own flow.
        Assert.Empty((await Json(await api.SendAsync(HttpMethod.Get, $"/api/hr/payroll-inputs?month={Today:yyyy-MM}", cast.Executive))).EnumerateArray());

        // The holder disputes it with an explanation; only the holder may; the reviewer resolves by reversing it with an opposite adjustment.
        var disputeBody = new { note = "The receipts were handed to the office on the 3rd", rowVersion = adjustment.GetProperty("rowVersion").GetString() };
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/custody-accounts/{account.Id()}/adjustments/{adjustment.Id()}/dispute", cast.Executive, disputeBody)).StatusCode);
        var disputed = await api.SendAsync(HttpMethod.Post, $"/api/custody-accounts/{account.Id()}/adjustments/{adjustment.Id()}/dispute", cast.Accountant, disputeBody);
        await EnsureStatus(disputed, HttpStatusCode.OK);
        var open = await Json(disputed);
        Assert.Equal(("Disputed", "The receipts were handed to the office on the 3rd"), (open.GetProperty("status").GetString(), open.GetProperty("disputeNote").GetString()));
        Assert.Equal("invalid_status", await CodeOf(await api.SendAsync(HttpMethod.Post, $"/api/custody-accounts/{account.Id()}/adjustments/{adjustment.Id()}/dispute", cast.Accountant, new { note = "Again", rowVersion = open.GetProperty("rowVersion").GetString() })));
        Assert.Equal("own_record", await CodeOf(await api.SendAsync(HttpMethod.Post, $"/api/custody-accounts/{account.Id()}/adjustments/{adjustment.Id()}/resolve", cast.Accountant, new { note = "Fine", reverse = true, rowVersion = open.GetProperty("rowVersion").GetString() })));
        var resolved = await api.SendAsync(HttpMethod.Post, $"/api/custody-accounts/{account.Id()}/adjustments/{adjustment.Id()}/resolve", cast.Executive,
            new { note = "Receipts found in the office file", reverse = true, rowVersion = open.GetProperty("rowVersion").GetString() });
        await EnsureStatus(resolved, HttpStatusCode.OK);
        Assert.Equal(("Reversed", 60_000m), ((await Json(resolved)).GetProperty("status").GetString(), (await Json(resolved)).Money("balanceAfter")));

        // Both adjustments stay; the second points at the first; the balance is back to 60,000 and reconciles from the movements.
        var adjustments = (await Json(await api.SendAsync(HttpMethod.Get, $"/api/custody-accounts/{account.Id()}/adjustments", cast.Executive))).EnumerateArray().ToList();
        Assert.Equal(2, adjustments.Count);
        Assert.Contains(adjustments, item => item.GetProperty("reversesAdjustmentId").ValueKind == JsonValueKind.Number && item.Money("amount") == 1_500m);
        Assert.Equal(60_000m, (await Account(api, cast.Accountant, account.Id())).Money("posted"));
        var movements = (await Json(await api.SendAsync(HttpMethod.Get, $"/api/custody-accounts/{account.Id()}", cast.Executive))).GetProperty("movements").EnumerateArray().ToList();
        Assert.Equal(60_000m, movements.Sum(movement => movement.Money("amount")));

        // A debit beyond the balance under the Warn policy records the shortfall instead of hiding it.
        var beyond = await Json(await api.SendAsync(HttpMethod.Post, $"/api/custody-accounts/{account.Id()}/adjustments", cast.Executive,
            new { amount = -61_000m, entryDate = Iso(Today), reason = "Cash box found empty", evidenceReference = "COUNT-9", idempotencyKey = Guid.NewGuid() }));
        Assert.Equal((1_000m, -1_000m), (beyond.Money("shortfall"), beyond.Money("balanceAfter")));
        var attention = await Json(await api.SendAsync(HttpMethod.Get, $"/api/custody-monitor?{period}&attention=true", cast.Executive));
        Assert.Equal(1_000m, Assert.Single(attention.GetProperty("items").EnumerateArray(), item => item.GetProperty("accountId").GetInt32() == account.Id()).Money("owed"));
    }

    [DatabaseFact]
    public async Task The_audit_trail_answers_who_changed_what_keeps_removed_permissions_and_cannot_be_rewritten()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        await OpenTreasuryAsync(api, cast.Executive, 1_000_000m);
        var account = await OpenAccountAsync(api, cast.Accountant, cast.Accountant.UserId);
        var transfer = await Json(await api.SendAsync(HttpMethod.Post, "/api/custody-transfers", cast.Accountant, new
        {
            amount = 5_000m, purpose = "CustodyAdvance", source = new { kind = "Treasury", treasuryAccount = "Cash" }, destination = new { kind = "Custody", accountId = account.Id() }, idempotencyKey = Guid.NewGuid().ToString(),
        }));
        await Stepped(api, cast.Executive, transfer, "approve", new { note = "Weekly float" });

        // Section 17: who proposed, who approved, what the status was before and after, when; filtered by record, actor, action and the status set.
        var approvals = await Json(await api.SendAsync(HttpMethod.Get, $"/api/audit-entries?entityType=CustodyTransfer&entityKey={transfer.Id()}&statusAfter=Approved", cast.Executive));
        var approval = Assert.Single(approvals.GetProperty("items").EnumerateArray());
        Assert.Equal((cast.Executive.UserId, "Modified", "Requested", "Approved"), (approval.GetProperty("userId").GetInt32(), approval.GetProperty("action").GetString(),
            approval.GetProperty("changes").GetProperty("Status")[0].GetString(), approval.GetProperty("changes").GetProperty("Status")[1].GetString()));
        var byAccountant = await Json(await api.SendAsync(HttpMethod.Get, $"/api/audit-entries?entityType=CustodyTransfer&userId={cast.Accountant.UserId}&action=Added", cast.Executive));
        Assert.Contains(byAccountant.GetProperty("items").EnumerateArray(), item => item.GetProperty("entityKey").GetString() == transfer.Id().ToString());
        Assert.Empty((await Json(await api.SendAsync(HttpMethod.Get, $"/api/audit-entries?entityType=CustodyTransfer&entityKey={transfer.Id()}&statusAfter=Rejected", cast.Executive))).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, "/api/audit-entries", cast.Accountant)).StatusCode);

        // Taking a permission away is in the trail too, as the removed grant rows (no bulk delete bypasses it).
        var user = await Json(await api.SendAsync(HttpMethod.Get, $"/api/users/{cast.Accountant.UserId}", cast.Executive));
        await EnsureStatus(await api.SendAsync(HttpMethod.Put, $"/api/users/{cast.Accountant.UserId}/permissions/custody", cast.Executive,
            new { actions = new[] { "View" }, rowVersion = user.GetProperty("rowVersion").GetString() }), HttpStatusCode.OK);
        var granted = await Json(await api.SendAsync(HttpMethod.Get, $"/api/audit-entries?entityType=UserPermissionOverride&userId={cast.Executive.UserId}", cast.Executive));
        Assert.Contains(granted.GetProperty("items").EnumerateArray(), item => item.GetProperty("action").GetString() == "Added");
        var again = await Json(await api.SendAsync(HttpMethod.Get, $"/api/users/{cast.Accountant.UserId}", cast.Executive));
        await EnsureStatus(await api.SendAsync(HttpMethod.Put, $"/api/users/{cast.Accountant.UserId}/permissions/custody", cast.Executive,
            new { actions = new[] { "View", "Approve" }, rowVersion = again.GetProperty("rowVersion").GetString() }), HttpStatusCode.OK);
        var removed = await Json(await api.SendAsync(HttpMethod.Get, $"/api/audit-entries?entityType=UserPermissionOverride&action=Deleted", cast.Executive));
        Assert.NotEmpty(removed.GetProperty("items").EnumerateArray());

        // Nothing rewrites or erases the trail, not even raw SQL under the application's login: the table refuses at the database.
        var before = await api.Access.QueryAsync(db => db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [platform].[AuditEntry]").SingleAsync());
        var update = await Assert.ThrowsAsync<SqlException>(() => api.Access.QueryAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE [platform].[AuditEntry] SET [Action] = 'Modified' WHERE [Id] = (SELECT MIN([Id]) FROM [platform].[AuditEntry])")));
        Assert.Equal(50017, update.Number);
        var delete = await Assert.ThrowsAsync<SqlException>(() => api.Access.QueryAsync(db => db.Database.ExecuteSqlRawAsync("DELETE FROM [platform].[AuditEntry] WHERE [Id] = (SELECT MIN([Id]) FROM [platform].[AuditEntry])")));
        Assert.Equal(50017, delete.Number);
        Assert.Equal(before, await api.Access.QueryAsync(db => db.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS [Value] FROM [platform].[AuditEntry]").SingleAsync()));

        // Section 13.D: the Accountant initiates but never decides, by any route: the approve endpoint refuses even with custody Approve restored.
        var second = await Json(await api.SendAsync(HttpMethod.Post, "/api/custody-transfers", cast.Accountant, new
        {
            amount = 1m, purpose = "CustodyAdvance", source = new { kind = "Treasury", treasuryAccount = "Cash" }, destination = new { kind = "Custody", accountId = account.Id() }, idempotencyKey = Guid.NewGuid().ToString(),
        }));
        Assert.Equal(HttpStatusCode.Forbidden, (await Step(api, cast.Accountant, second, "approve")).StatusCode);
        Assert.Equal("Requested", (await Json(await api.SendAsync(HttpMethod.Get, $"/api/custody-transfers/{second.Id()}", cast.Accountant))).GetProperty("status").GetString());
    }
}
