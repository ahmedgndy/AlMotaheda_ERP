using System.Net;
using AlMotaheda.Modules.Access.Tests.Database;
using Microsoft.EntityFrameworkCore;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Core.CoreApi;

namespace AlMotaheda.Modules.Projects.Tests.Database.Core;

/// <summary>
/// Supplier payments on SQL Server: allocation limits, scope, idempotency, open requests holding money, and void.
/// A payment is requested here and becomes a payment only once Finance has it approved and confirmed.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SupplierPaymentTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task A_payment_cannot_over_allocate_touch_another_project_or_supplier_or_pay_an_unapproved_record()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("PROJ-A");
        var projectB = (await api.CreateProjectAsync(cast.Executive, "PROJ-B", cast.Executive.UserId)).Id();
        var cemex = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var steel = await api.CreateSupplierAsync(cast.Executive, "Steel House");

        var invoice = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(cemex, 10m, 100m, "Credit"));
        var custody = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(cemex, 1m, 50m, "Custody"));
        var pending = await api.RecordAsync(cast.Engineer, cast.ProjectId, SupplyBody(cemex, 1m, 50m, "Credit"));
        var elsewhere = await api.RecordApprovedAsync(cast.Executive, cast.Accountant, projectB, SupplyBody(cemex, 1m, 500m, "Credit"));

        // More than the invoice.
        var tooMuch = await api.PayAsync(cast.Accountant, cast.ProjectId, PaymentBody(cemex, 1_000.01m, [(invoice.Id(), 1_000.01m)]));
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);
        Assert.Equal("over_allocation", await CodeOf(tooMuch));

        // 600 now, then 400.01: the second payment goes over what is left.
        await api.PaidAsync(cast.Accountant, cast.ProjectId, cemex, 600m, (invoice.Id(), 600m));
        var over = await api.PayAsync(cast.Accountant, cast.ProjectId, PaymentBody(cemex, 400.01m, [(invoice.Id(), 400.01m)]));
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        Assert.Equal("over_allocation", await CodeOf(over));

        // Another project's record: not found here. Another supplier's record: a form error. Custody or pending: wrong status.
        var otherProject = await api.PayAsync(cast.Accountant, cast.ProjectId, PaymentBody(cemex, 100m, [(elsewhere.Id(), 100m)]));
        Assert.Equal(HttpStatusCode.NotFound, otherProject.StatusCode);
        var otherSupplier = await api.PayAsync(cast.Accountant, cast.ProjectId, PaymentBody(steel, 100m, [(invoice.Id(), 100m)]));
        Assert.Equal(HttpStatusCode.BadRequest, otherSupplier.StatusCode);
        var paysCustody = await api.PayAsync(cast.Accountant, cast.ProjectId, PaymentBody(cemex, 50m, [(custody.Id(), 50m)]));
        Assert.Equal(HttpStatusCode.Conflict, paysCustody.StatusCode);
        Assert.Equal("invalid_status", await CodeOf(paysCustody));
        var paysPending = await api.PayAsync(cast.Accountant, cast.ProjectId, PaymentBody(cemex, 50m, [(pending.Id(), 50m)]));
        Assert.Equal(HttpStatusCode.Conflict, paysPending.StatusCode);

        // Nothing but the one good payment was written, and the invoice shows exactly it.
        Assert.Equal(1, await api.QueryAsync(db => db.SupplierPayments.CountAsync()));
        var shown = await api.GetExpenseAsync(cast.Executive, cast.ProjectId, invoice.Id());
        Assert.Equal(600m, shown.Money("paid"));
        Assert.Equal(400m, shown.Money("outstanding"));

        // The rest fits exactly.
        var rest = await api.PaidAsync(cast.Accountant, cast.ProjectId, cemex, 400m, (invoice.Id(), 400m));
        Assert.Equal(400m, rest.Money("amount"));
        Assert.Equal("Paid", (await api.GetExpenseAsync(cast.Executive, cast.ProjectId, invoice.Id())).GetProperty("paymentState").GetString());
    }

    [DatabaseFact]
    public async Task The_same_idempotency_key_returns_the_first_request_and_a_different_payment_with_it_is_refused()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("PROJ-A");
        var projectB = (await api.CreateProjectAsync(cast.Executive, "PROJ-B", cast.Executive.UserId)).Id();
        var cemex = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var invoice = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(cemex, 10m, 100m, "Credit"));
        var key = Guid.NewGuid();

        var first = await api.PayAsync(cast.Accountant, cast.ProjectId, PaymentBody(cemex, 300m, [(invoice.Id(), 300m)], key));
        await EnsureStatus(first, HttpStatusCode.Created);
        var created = await Json(first);
        Assert.Equal($"/api/payment-requests/{created.Id()}", first.Headers.Location?.OriginalString);
        Assert.Equal("Submitted", created.GetProperty("status").GetString());
        Assert.StartsWith("PAY-", created.GetProperty("number").GetString());

        // The same form sent again is answered with the same request.
        var retry = await api.PayAsync(cast.Accountant, cast.ProjectId, PaymentBody(cemex, 300m, [(invoice.Id(), 300m)], key));
        await EnsureStatus(retry, HttpStatusCode.OK);
        Assert.Equal(created.Id(), (await Json(retry)).Id());
        Assert.Equal(300m, (await Json(retry)).Money("amount"));

        // A different payment sent with the same key is a client bug: refused, never answered with the old payment.
        var different = await api.PayAsync(cast.Accountant, cast.ProjectId, PaymentBody(cemex, 700m, [(invoice.Id(), 700m)], key));
        Assert.Equal(HttpStatusCode.Conflict, different.StatusCode);
        Assert.Equal("idempotency_key_reused", await CodeOf(different));

        // The same key on another project is a client bug, not a retry.
        var otherProject = await api.PayAsync(cast.Accountant, projectB, PaymentBody(cemex, 1m, [(invoice.Id(), 1m)], key));
        Assert.Equal(HttpStatusCode.Conflict, otherProject.StatusCode);
        Assert.Equal("idempotency_key_reused", await CodeOf(otherProject));

        // Requested is not paid: no payment row yet, and the invoice is still unpaid.
        Assert.Equal(0, await api.QueryAsync(db => db.SupplierPayments.CountAsync()));
        Assert.Equal(0m, (await api.GetExpenseAsync(cast.Executive, cast.ProjectId, invoice.Id())).Money("paid"));
    }

    [DatabaseFact]
    public async Task An_open_request_holds_its_money_until_it_is_paid_or_cancelled()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var cemex = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var invoice = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(cemex, 10m, 100m, "Credit"));

        // 700 requested and waiting for the Executive: only 300 is left to request.
        var waiting = await api.PayAsync(cast.Accountant, cast.ProjectId, PaymentBody(cemex, 700m, [(invoice.Id(), 700m)]));
        await EnsureStatus(waiting, HttpStatusCode.Created);
        var request = await Json(waiting);
        var tooMuch = await api.PayAsync(cast.Accountant, cast.ProjectId, PaymentBody(cemex, 300.01m, [(invoice.Id(), 300.01m)]));
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);
        Assert.Equal("over_allocation", await CodeOf(tooMuch));

        // Cancelled by its creator, with a reason: the 700 is free again, and nothing was ever paid.
        var cancelled = await api.SendAsync(HttpMethod.Post, $"/api/payment-requests/{request.Id()}/cancel", cast.Accountant,
            new { reason = "Wrong amount", rowVersion = await api.RequestRowVersionAsync(cast.Accountant, request.Id()) });
        await EnsureStatus(cancelled, HttpStatusCode.OK);
        Assert.Equal("Cancelled", (await Json(cancelled)).GetProperty("request").GetProperty("status").GetString());

        var paid = await api.PaidAsync(cast.Accountant, cast.ProjectId, cemex, 1_000m, (invoice.Id(), 1_000m));
        Assert.Equal(1_000m, paid.Money("amount"));
        Assert.Equal("Paid", (await api.GetExpenseAsync(cast.Executive, cast.ProjectId, invoice.Id())).GetProperty("paymentState").GetString());
        Assert.Equal(1, await api.QueryAsync(db => db.SupplierPayments.CountAsync()));
    }

    [DatabaseFact]
    public async Task Voiding_needs_payments_approve_frees_the_records_and_happens_once()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var cemex = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var invoice = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(cemex, 10m, 100m, "Credit"));
        var payment = await api.PaidAsync(cast.Accountant, cast.ProjectId, cemex, 1_000m, (invoice.Id(), 1_000m));

        // The invoice is fully paid, so a new payment can't touch it.
        var full = await api.PayAsync(cast.Accountant, cast.ProjectId, PaymentBody(cemex, 1m, [(invoice.Id(), 1m)]));
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);

        // The accountant records payments (payments Create) but doesn't void them (payments Approve, Executive by default).
        var byAccountant = await api.VoidAsync(cast.Accountant, cast.ProjectId, payment);
        Assert.Equal(HttpStatusCode.Forbidden, byAccountant.StatusCode);

        var noReason = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/supplier-payments/{payment.Id()}/void", cast.Executive,
            new { rowVersion = payment.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);

        var voided = await api.VoidAsync(cast.Executive, cast.ProjectId, payment, "Paid from the wrong account");
        await EnsureStatus(voided, HttpStatusCode.OK);
        var voidedBody = await Json(voided);
        Assert.True(voidedBody.GetProperty("isVoided").GetBoolean());
        Assert.Equal(cast.Executive.UserId, voidedBody.GetProperty("voidedBy").GetInt32());
        Assert.Equal("Paid from the wrong account", voidedBody.GetProperty("voidReason").GetString());

        var twice = await api.VoidAsync(cast.Executive, cast.ProjectId, voidedBody);
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal("payment_already_voided", await CodeOf(twice));

        // Free again: the full amount can be paid once more, and the voided row stays in the table.
        Assert.Equal(1_000m, (await api.GetExpenseAsync(cast.Executive, cast.ProjectId, invoice.Id())).Money("outstanding"));
        await api.PaidAsync(cast.Accountant, cast.ProjectId, cemex, 1_000m, (invoice.Id(), 1_000m));

        var live = await Json(await api.SendAsync(HttpMethod.Get, $"/api/projects/{cast.ProjectId}/supplier-payments", cast.Executive));
        var all = await Json(await api.SendAsync(HttpMethod.Get, $"/api/projects/{cast.ProjectId}/supplier-payments?includeVoided=true", cast.Executive));
        Assert.Equal(1, live.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, all.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, await api.QueryAsync(db => db.SupplierPayments.CountAsync()));

        var balance = Assert.Single((await api.BalancesAsync(cast.Executive, cast.ProjectId)).GetProperty("items").EnumerateArray());
        Assert.Equal(1_000m, balance.Money("paid"));
        Assert.Equal(0m, balance.Money("outstanding"));
        Assert.Equal(0, balance.GetProperty("openInvoices").GetInt32());
    }

    [DatabaseFact]
    public async Task One_payment_settles_several_invoices_and_each_shows_its_share()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var cemex = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var first = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(cemex, 10m, 100m, "Credit"));
        var second = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(cemex, 5m, 100m, "Credit"));

        var payment = await api.PaidAsync(cast.Accountant, cast.ProjectId, cemex, 1_250m, (first.Id(), 1_000m), (second.Id(), 250m));

        Assert.Equal(2, payment.GetProperty("allocations").GetArrayLength());
        Assert.Equal("Paid", (await api.GetExpenseAsync(cast.Executive, cast.ProjectId, first.Id())).GetProperty("paymentState").GetString());
        var partly = await api.GetExpenseAsync(cast.Executive, cast.ProjectId, second.Id());
        Assert.Equal(250m, partly.Money("paid"));
        Assert.Equal("PartiallyPaid", partly.GetProperty("paymentState").GetString());

        var list = await api.ListExpensesAsync(cast.Executive, cast.ProjectId, "&paymentState=PartiallyPaid");
        Assert.Equal([second.Id()], list.GetProperty("items").EnumerateArray().Select(item => item.Id()));
        Assert.Equal(1_250m, list.GetProperty("summary").Money("paid"));
        Assert.Equal(250m, list.GetProperty("summary").Money("outstanding"));
    }
}
