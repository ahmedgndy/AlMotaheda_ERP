using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database.Core;
using AlMotaheda.Modules.Projects.Tests.Database.CostManagement;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Core.CoreApi;

namespace AlMotaheda.Modules.Platform.Tests.Database;

/// <summary>
/// A purchase above its reference price becomes stored notifications, through the outbox after the commit, for the
/// project's manager and the Executives only: never for the engineer who recorded it, never for someone off the project.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class CostAlertNotificationTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task A_purchase_above_its_range_is_stored_for_the_manager_and_the_executive_and_nobody_else()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var (_, outsider) = await api.CreateSignedInUserAsync(cast.Executive, "manager");
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var priceItem = await api.CreatePriceItemAsync(cast.Executive, "Cement 42.5", "Ton", 1_000m, 1_200m);

        var recorded = await api.RecordAsync(cast.Engineer, cast.ProjectId, SupplyBody(supplier, 2m, 1_400m, "Credit", new()
        {
            ["unit"] = "Ton",
            ["priceItemId"] = priceItem,
            ["priceJustification"] = "Only supplier with stock",
        }));

        var forManager = await WaitForAsync(api, cast.Manager, count: 2);
        Assert.Equal(["CostRecorded", "PriceAboveRange"], forManager.Select(item => item.GetProperty("type").GetString()).Order());
        Assert.All(forManager, item => Assert.Equal(cast.ProjectId, item.GetProperty("projectId").GetInt32()));
        // The notification points to the alert; it holds no amount and no supplier name.
        Assert.All(forManager, item => Assert.DoesNotContain("1400", item.GetRawText(), StringComparison.Ordinal));

        var forExecutive = await WaitForAsync(api, cast.Executive, count: 1);
        Assert.Equal("PriceAboveRange", Assert.Single(forExecutive).GetProperty("type").GetString());

        Assert.Equal(0, await UnreadCountAsync(api, cast.Engineer));
        Assert.Equal(0, await UnreadCountAsync(api, outsider));

        // Resubmitting the same record above its range doesn't notify the same people again.
        var returned = await Json(await api.ReviewAsync(cast.Accountant, cast.ProjectId, recorded, "return", "Attach the invoice"));
        var fixedBody = EditBody(returned, new() { ["priceItemId"] = priceItem, ["priceJustification"] = "Only supplier with stock" });
        await EnsureStatus(await api.SendAsync(HttpMethod.Put, $"/api/projects/{cast.ProjectId}/expenses/{recorded.Id()}", cast.Engineer, fixedBody), HttpStatusCode.OK);
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(2, await UnreadCountAsync(api, cast.Manager));
        Assert.Equal(1, await UnreadCountAsync(api, cast.Executive));
    }

    private static async Task<int> UnreadCountAsync(ProjectsApi api, Session session)
    {
        var response = await api.SendAsync(HttpMethod.Get, "/api/notifications/unread-count", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return (await Json(response)).GetProperty("count").GetInt32();
    }

    /// <summary>The dispatcher runs in the background after the commit: wait until <paramref name="count"/> are stored.</summary>
    private static async Task<List<JsonElement>> WaitForAsync(ProjectsApi api, Session session, int count)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var body = await Json(await api.SendAsync(HttpMethod.Get, "/api/notifications", session));
            if (body.GetProperty("totalCount").GetInt32() >= count || timer.Elapsed > TimeSpan.FromSeconds(30))
            {
                return body.GetProperty("items").EnumerateArray().ToList();
            }

            await Task.Delay(250);
        }
    }
}
