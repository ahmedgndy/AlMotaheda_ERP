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

    private static Task<HttpResponseMessage> AdjustAsync(ProjectsApi api, Session session, int projectId, int expenseId, decimal amount) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/expenses/{expenseId}/adjustments", session,
            new { amount, reason = "The supplier charged the same truck twice" });
}
