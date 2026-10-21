using System.Net;
using AlMotaheda.Modules.Access.Tests.Database;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Core.CoreApi;

namespace AlMotaheda.Modules.Projects.Tests.Database.Core;

/// <summary>
/// Correcting an approved record (refactor plan M6): the record stays frozen, an adjustment is asked for and decided
/// by an Executive, and every figure then uses the corrected amount, never below what is paid.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ExpenseAdjustmentTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task An_approved_record_is_corrected_by_an_executive_and_never_below_what_is_paid()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var cemex = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var invoice = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(cemex, 10m, 100m, "Credit"));
        await api.PaidAsync(cast.Accountant, cast.ProjectId, cemex, 600m, (invoice.Id(), 600m));

        // A record still waiting for review is edited, not adjusted.
        var pending = await api.RecordAsync(cast.Engineer, cast.ProjectId, SupplyBody(cemex, 1m, 50m, "Credit"));
        Assert.Equal("invalid_status", await CodeOf(await AdjustAsync(api, cast.Engineer, cast.ProjectId, pending.Id(), -10m)));

        // 600 is paid: the corrected amount can't go below it.
        var tooLow = await AdjustAsync(api, cast.Engineer, cast.ProjectId, invoice.Id(), -401m);
        Assert.Equal(HttpStatusCode.Conflict, tooLow.StatusCode);
        Assert.Equal("adjustment_below_paid", await CodeOf(tooLow));

        // The supplier charged 400 too much: asked for, one at a time.
        var asked = await AdjustAsync(api, cast.Engineer, cast.ProjectId, invoice.Id(), -400m);
        await EnsureStatus(asked, HttpStatusCode.Created);
        var adjustment = Assert.Single((await Json(asked)).GetProperty("adjustments").EnumerateArray());
        Assert.Equal("Requested", adjustment.GetProperty("status").GetString());
        Assert.Equal("adjustment_open", await CodeOf(await AdjustAsync(api, cast.Engineer, cast.ProjectId, invoice.Id(), -1m)));

        // Still 1,000 until decided.
        Assert.Equal(1_000m, (await api.GetExpenseAsync(cast.Executive, cast.ProjectId, invoice.Id())).Money("adjustedAmount"));

        // Only an Executive decides; the accountant (projects Approve) can't.
        var path = $"/api/projects/{cast.ProjectId}/expense-adjustments/{adjustment.Id()}/approve";
        var body = new { rowVersion = adjustment.GetProperty("rowVersion").GetString() };
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, path, cast.Accountant, body)).StatusCode);
        var approved = await api.SendAsync(HttpMethod.Post, path, cast.Executive, body);
        await EnsureStatus(approved, HttpStatusCode.OK);

        // Every figure follows: the record is now 600, fully paid; the original amount is kept.
        var corrected = await Json(approved);
        Assert.Equal(1_000m, corrected.Money("amount"));
        Assert.Equal(600m, corrected.Money("adjustedAmount"));
        Assert.Equal(600m, corrected.Money("recognized"));
        Assert.Equal(0m, corrected.Money("outstanding"));
        Assert.Equal("Paid", corrected.GetProperty("paymentState").GetString());
        var balance = Assert.Single((await api.BalancesAsync(cast.Executive, cast.ProjectId)).GetProperty("items").EnumerateArray());
        Assert.Equal(0m, balance.Money("outstanding"));
    }

    [DatabaseFact]
    public async Task The_payment_state_filter_follows_the_corrected_amount()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var cemex = await api.CreateSupplierAsync(cast.Executive, "Cemex");

        // Over-charged: 1,000 recorded, 600 paid, then corrected down by 400. Fully paid once corrected.
        var overcharged = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(cemex, 10m, 100m, "Credit"));
        await api.PaidAsync(cast.Accountant, cast.ProjectId, cemex, 600m, (overcharged.Id(), 600m));
        await ApprovedAdjustmentAsync(api, cast.Engineer, cast.Executive, cast.ProjectId, overcharged.Id(), -400m);

        // Under-charged: 1,000 recorded and paid in full, then corrected up by 200. Partly paid once corrected.
        var undercharged = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(cemex, 10m, 100m, "Credit"));
        await api.PaidAsync(cast.Accountant, cast.ProjectId, cemex, 1_000m, (undercharged.Id(), 1_000m));
        await ApprovedAdjustmentAsync(api, cast.Engineer, cast.Executive, cast.ProjectId, undercharged.Id(), 200m);

        // The list filter and the state each row shows agree, both on the corrected amount.
        var paid = await api.ListExpensesAsync(cast.Executive, cast.ProjectId, "&paymentState=Paid");
        var paidRow = Assert.Single(paid.GetProperty("items").EnumerateArray());
        Assert.Equal(overcharged.Id(), paidRow.Id());
        Assert.Equal("Paid", paidRow.GetProperty("paymentState").GetString());

        var partlyPaid = await api.ListExpensesAsync(cast.Executive, cast.ProjectId, "&paymentState=PartiallyPaid");
        var partlyPaidRow = Assert.Single(partlyPaid.GetProperty("items").EnumerateArray());
        Assert.Equal(undercharged.Id(), partlyPaidRow.Id());
        Assert.Equal("PartiallyPaid", partlyPaidRow.GetProperty("paymentState").GetString());
        Assert.Equal(200m, partlyPaidRow.Money("outstanding"));

        Assert.Empty((await api.ListExpensesAsync(cast.Executive, cast.ProjectId, "&paymentState=Unpaid")).GetProperty("items").EnumerateArray());
    }

    private static async Task ApprovedAdjustmentAsync(ProjectsApi api, Session requester, Session executive, int projectId, int expenseId, decimal amount)
    {
        var asked = await AdjustAsync(api, requester, projectId, expenseId, amount);
        await EnsureStatus(asked, HttpStatusCode.Created);
        var adjustment = (await Json(asked)).GetProperty("adjustments").EnumerateArray()
            .Single(candidate => candidate.GetProperty("status").GetString() == "Requested");
        var approved = await api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/expense-adjustments/{adjustment.Id()}/approve", executive,
            new { rowVersion = adjustment.GetProperty("rowVersion").GetString() });
        await EnsureStatus(approved, HttpStatusCode.OK);
    }

    private static Task<HttpResponseMessage> AdjustAsync(ProjectsApi api, Session session, int projectId, int expenseId, decimal amount) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/expenses/{expenseId}/adjustments", session,
            new { amount, reason = "The supplier charged the same truck twice" });
}
