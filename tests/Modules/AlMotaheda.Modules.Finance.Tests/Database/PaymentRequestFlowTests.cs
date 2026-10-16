using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Core.CoreApi;

namespace AlMotaheda.Modules.Finance.Tests.Database;

/// <summary>
/// The payment flow on SQL Server, across modules: a supplier payment requested in Projects, decided by the
/// Executive, sent to the bank and confirmed by the Accountant in Finance, written back to Projects and posted to
/// the ledger, all in one transaction per step.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class PaymentRequestFlowTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task A_request_becomes_a_payment_only_after_the_executive_and_the_bank_and_the_ledger_follows_it()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var cemex = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var invoice = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(cemex, 10m, 100m, "Credit"));

        var created = await api.PayAsync(cast.Accountant, cast.ProjectId, PaymentBody(cemex, 1_000m, [(invoice.Id(), 1_000m)]));
        await EnsureStatus(created, HttpStatusCode.Created);
        var requestId = (await Json(created)).Id();

        // The project manager (payments View, no See money) sees it, without amounts; the engineer doesn't.
        var managerList = await Json(await api.SendAsync(HttpMethod.Get, "/api/payment-requests", cast.Manager));
        var row = Assert.Single(managerList.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("amount").ValueKind);
        Assert.Equal("Cemex", row.GetProperty("payeeNameEn").GetString());
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, "/api/payment-requests", cast.Engineer)).StatusCode);

        // The Accountant creates requests but doesn't decide them.
        Assert.Equal(HttpStatusCode.Forbidden, (await StepAsync(api, cast.Accountant, requestId, "approve")).StatusCode);

        var approved = await StepAsync(api, cast.Executive, requestId, "approve", new { note = "Checked the delivery notes" });
        Assert.Equal("Approved", await StatusOf(approved));

        // A bank transfer isn't confirmed before it goes to the bank.
        var early = await StepAsync(api, cast.Accountant, requestId, "confirm-payment");
        Assert.Equal("wrong_method", await CodeOf(early));

        var sent = await Json(await StepAsync(api, cast.Accountant, requestId, "send-to-bank"));
        Assert.Equal("SentToBank", sent.GetProperty("request").GetProperty("status").GetString());
        Assert.StartsWith("MOCK-PAY-", sent.GetProperty("bankReference").GetString());
        Assert.Equal(0m, (await api.GetExpenseAsync(cast.Executive, cast.ProjectId, invoice.Id())).Money("paid"));

        var paid = await Json(await StepAsync(api, cast.Accountant, requestId, "confirm-payment", new { reference = "SWIFT-881" }));
        Assert.Equal("Paid", paid.GetProperty("request").GetProperty("status").GetString());
        Assert.Equal(["Submit", "Approve", "SendToBank", "ConfirmPayment"], paid.GetProperty("events").EnumerateArray().Select(step => step.GetProperty("action").GetString()));
        Assert.Equal("Checked the delivery notes", paid.GetProperty("decisionNote").GetString());
        Assert.Equal(1_000m, (await api.GetExpenseAsync(cast.Executive, cast.ProjectId, invoice.Id())).Money("paid"));

        // The ledger: one entry, money out of the bank, for this supplier and project.
        var ledger = await Json(await api.SendAsync(HttpMethod.Get, $"/api/ledger?projectId={cast.ProjectId}", cast.Accountant));
        var entry = Assert.Single(ledger.GetProperty("items").EnumerateArray());
        Assert.Equal((-1_000m, "Bank", "Supplier", cemex), (entry.Money("amount"), entry.GetProperty("account").GetString(),
            entry.GetProperty("counterpartyType").GetString(), entry.GetProperty("counterpartyId").GetInt32()));
        Assert.Equal(paid.GetProperty("sourcePaymentId").GetInt32(), entry.GetProperty("sourcePaymentId").GetInt32());
        Assert.Equal(-1_000m, ledger.GetProperty("totals").Money("moneyOut"));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, "/api/ledger", cast.Manager)).StatusCode);

        // A void reverses it in the ledger, in the same transaction: the request is Reversed and the net is zero.
        var payment = (await Json(await api.SendAsync(HttpMethod.Get, $"/api/projects/{cast.ProjectId}/supplier-payments", cast.Executive)))
            .GetProperty("items")[0];
        await EnsureStatus(await api.VoidAsync(cast.Executive, cast.ProjectId, payment, "Paid from the wrong account"), HttpStatusCode.OK);

        var after = await Json(await api.SendAsync(HttpMethod.Get, $"/api/ledger?projectId={cast.ProjectId}", cast.Executive));
        Assert.Equal(2, after.GetProperty("totalCount").GetInt32());
        Assert.Equal(0m, after.GetProperty("totals").Money("net"));
        var reversed = await Json(await api.SendAsync(HttpMethod.Get, $"/api/payment-requests/{requestId}", cast.Executive));
        Assert.Equal("Reversed", reversed.GetProperty("request").GetProperty("status").GetString());
        Assert.Equal("Paid from the wrong account", reversed.GetProperty("closeReason").GetString());
    }

    [DatabaseFact]
    public async Task Rejected_and_cancelled_requests_free_their_money_and_nobody_decides_their_own()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var cemex = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var invoice = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(cemex, 10m, 100m, "Credit"));

        // The Executive may request a payment, but then can't approve it: someone else decides (separation of duties).
        var own = (await Json(await api.PayAsync(cast.Executive, cast.ProjectId, PaymentBody(cemex, 400m, [(invoice.Id(), 400m)])))).Id();
        Assert.Equal("own_record", await CodeOf(await StepAsync(api, cast.Executive, own, "approve")));
        Assert.Equal("Cancelled", await StatusOf(await StepAsync(api, cast.Executive, own, "cancel", new { reason = "Accountant will request it" })));

        // Rejection needs a note, is final, and frees the reservation.
        var first = (await Json(await api.PayAsync(cast.Accountant, cast.ProjectId, PaymentBody(cemex, 1_000m, [(invoice.Id(), 1_000m)])))).Id();
        Assert.Equal(HttpStatusCode.BadRequest, (await StepAsync(api, cast.Executive, first, "reject")).StatusCode);
        Assert.Equal("Rejected", await StatusOf(await StepAsync(api, cast.Executive, first, "reject", new { note = "Wait for the credit note" })));
        Assert.Equal("invalid_status", await CodeOf(await StepAsync(api, cast.Executive, first, "approve")));

        // Someone who neither created it nor is an Executive can't cancel it.
        var second = (await Json(await api.PayAsync(cast.Accountant, cast.ProjectId, PaymentBody(cemex, 1_000m, [(invoice.Id(), 1_000m)])))).Id();
        Assert.Equal(HttpStatusCode.Forbidden, (await StepAsync(api, cast.Manager, second, "cancel", new { reason = "No" })).StatusCode);

        // A bank failure keeps the money held; the request can be sent again and paid.
        await StepAsync(api, cast.Executive, second, "approve");
        await StepAsync(api, cast.Accountant, second, "send-to-bank");
        Assert.Equal("Failed", await StatusOf(await StepAsync(api, cast.Accountant, second, "bank-failure", new { reason = "Beneficiary account closed" })));
        Assert.Equal(HttpStatusCode.Conflict, (await api.PayAsync(cast.Accountant, cast.ProjectId, PaymentBody(cemex, 1m, [(invoice.Id(), 1m)]))).StatusCode);
        await StepAsync(api, cast.Accountant, second, "send-to-bank");
        Assert.Equal("Paid", await StatusOf(await StepAsync(api, cast.Accountant, second, "confirm-payment")));

        var queue = await Json(await api.SendAsync(HttpMethod.Get, "/api/payment-requests", cast.Executive));
        var counts = queue.GetProperty("countsByStatus");
        Assert.Equal((1, 1, 1), (counts.GetProperty("Cancelled").GetInt32(), counts.GetProperty("Rejected").GetInt32(), counts.GetProperty("Paid").GetInt32()));
    }

    [DatabaseFact]
    public async Task A_removed_permission_stops_money_steps_at_once_even_with_a_valid_token()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var cemex = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var invoice = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(cemex, 10m, 100m, "Credit"));
        var requestId = (await Json(await api.PayAsync(cast.Accountant, cast.ProjectId, PaymentBody(cemex, 1_000m, [(invoice.Id(), 1_000m)])))).Id();
        await StepAsync(api, cast.Executive, requestId, "approve");

        // The Executive takes finance Edit away from this accountant; their token still says they have it.
        var accountantId = cast.Accountant.UserId;
        var user = await Json(await api.SendAsync(HttpMethod.Get, $"/api/users/{accountantId}", cast.Executive));
        var removed = await api.SendAsync(HttpMethod.Put, $"/api/users/{accountantId}/permissions/finance", cast.Executive,
            new { actions = new[] { "View" }, rowVersion = user.GetProperty("rowVersion").GetString() });
        await EnsureStatus(removed, HttpStatusCode.OK);

        var refused = await StepAsync(api, cast.Accountant, requestId, "send-to-bank");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("permission_revoked", await CodeOf(refused));
        Assert.Equal("Approved", (await Json(await api.SendAsync(HttpMethod.Get, $"/api/payment-requests/{requestId}", cast.Executive)))
            .GetProperty("request").GetProperty("status").GetString());
    }

    [DatabaseFact]
    public async Task Payments_recorded_before_finance_are_imported_once_into_requests_and_the_ledger()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var cemex = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var invoice = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(cemex, 10m, 100m, "Credit"));

        // Two payments written the old way, straight into the Projects tables: one still paid, one voided.
        var accountantId = cast.Accountant.UserId;
        var paidId = await LegacyPaymentAsync(api, cast.ProjectId, cemex, invoice.Id(), 500m, accountantId, voided: false);
        await LegacyPaymentAsync(api, cast.ProjectId, cemex, invoice.Id(), 300m, accountantId, voided: true);

        // The import runs twice (as if the migration ran again): the second run adds nothing.
        await ImportAsync(api);
        await ImportAsync(api);

        var imported = await FinanceAsync(api, db => db.PaymentRequests.Where(request => request.Origin == RequestOrigin.Imported)
            .OrderBy(request => request.SourcePaymentId).Select(request => new { request.Status, request.Amount, LineCount = request.Lines.Count }).ToListAsync());
        Assert.Equal(
            [(PaymentRequestStatus.Paid, 500m, 1), (PaymentRequestStatus.Reversed, 300m, 1)],
            imported.Select(request => (request.Status, request.Amount, request.LineCount)));

        var entries = await FinanceAsync(api, db => db.LedgerEntries.Select(entry => entry.Amount).ToListAsync());
        Assert.Equal(3, entries.Count);
        Assert.Equal(-500m, entries.Sum());

        // The imported payment is voided the new way: Finance finds its request and reverses it.
        var payment = (await Json(await api.SendAsync(HttpMethod.Get, $"/api/projects/{cast.ProjectId}/supplier-payments", cast.Executive)))
            .GetProperty("items").EnumerateArray().Single(candidate => candidate.Id() == paidId);
        await EnsureStatus(await api.VoidAsync(cast.Executive, cast.ProjectId, payment), HttpStatusCode.OK);
        Assert.Equal(0m, await FinanceAsync(api, db => db.LedgerEntries.SumAsync(entry => entry.Amount)));
    }

    private static Task<HttpResponseMessage> StepAsync(ProjectsApi api, Session session, int requestId, string step, object? extra = null) =>
        RunStepAsync(api, session, requestId, step, extra);

    private static async Task<HttpResponseMessage> RunStepAsync(ProjectsApi api, Session session, int requestId, string step, object? extra)
    {
        var executive = await api.SignInExecutiveAsync();
        var body = JsonSerializer.SerializeToElement(extra ?? new { }).EnumerateObject()
            .ToDictionary(property => property.Name, property => (object?)property.Value);
        body["rowVersion"] = await api.RequestRowVersionAsync(executive, requestId);
        return await api.SendAsync(HttpMethod.Post, $"/api/payment-requests/{requestId}/{step}", session, body);
    }

    private static async Task<string?> StatusOf(HttpResponseMessage response)
    {
        await EnsureStatus(response, HttpStatusCode.OK);
        return (await Json(response)).GetProperty("request").GetProperty("status").GetString();
    }

    private static async Task<int> LegacyPaymentAsync(ProjectsApi api, int projectId, int supplierId, int expenseId, decimal amount, int userId, bool voided)
    {
        await using var scope = api.Access.App.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProjectsDbContext>();
        var payment = new SupplierPayment
        {
            ProjectId = projectId,
            SupplierId = supplierId,
            PaymentDate = new DateOnly(2026, 9, 1),
            Amount = amount,
            Method = PaymentMethod.Cash,
            IdempotencyKey = Guid.NewGuid(),
            CreatedAt = DateTime.UtcNow,
            CreatedBy = userId,
            VoidedAt = voided ? DateTime.UtcNow : null,
            VoidedBy = voided ? userId : null,
            VoidReason = voided ? "Entered twice" : null,
        };
        payment.Allocations.Add(new SupplierPaymentAllocation { ExpenseId = expenseId, Amount = amount });
        db.SupplierPayments.Add(payment);
        await db.SaveChangesAsync();
        return payment.Id;
    }

    private static async Task ImportAsync(ProjectsApi api)
    {
        await using var scope = api.Access.App.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<FinanceDbContext>().Database.ExecuteSqlRawAsync(LegacyPaymentImport.Sql);
    }

    private static async Task<T> FinanceAsync<T>(ProjectsApi api, Func<FinanceDbContext, Task<T>> query)
    {
        await using var scope = api.Access.App.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<FinanceDbContext>());
    }
}
