using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Features.Reports;
using AlMotaheda.Modules.Projects.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database.Core;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Core.CoreApi;

namespace AlMotaheda.Modules.Platform.Tests.Database;

/// <summary>
/// The cache on SQL Server: cached reads are really served from the cache, and the next read after a change is
/// already fresh (the change removes what depends on it).
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class CacheTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task The_project_overview_is_cached_and_fresh_right_after_a_change()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("CACHE-001");
        var cemex = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var overview = $"/api/projects/{cast.ProjectId}/overview";

        Assert.Equal(0m, (await Get(api, cast.Executive, overview)).GetProperty("totals").Money("recognized"));

        // Served from the cache: asking the cache with a factory that fails returns the stored figures.
        await using (var scope = api.Access.App.Services.CreateAsyncScope())
        {
            var cache = scope.ServiceProvider.GetRequiredService<HybridCache>();
            var cached = await cache.GetOrCreateAsync<ProjectCostData>(
                $"projects:costs:{cast.ProjectId}:-:-:v1",
                _ => throw new InvalidOperationException("Not cached: the factory ran."));
            Assert.Equal(6, cached.Groups.Count);
        }

        // An approved expense removes the cached figures: the very next read shows it.
        await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(cemex, 10m, 100m, "Credit"));
        Assert.Equal(1_000m, (await Get(api, cast.Executive, overview)).GetProperty("totals").Money("recognized"));

        // And so does a payment (through Finance): the paid figure is fresh too.
        var expenses = await api.ListExpensesAsync(cast.Executive, cast.ProjectId);
        var expenseId = expenses.GetProperty("items")[0].Id();
        await api.PaidAsync(cast.Accountant, cast.ProjectId, cemex, 400m, (expenseId, 400m));
        Assert.Equal(400m, (await Get(api, cast.Executive, overview)).GetProperty("totals").Money("paid"));
    }

    [DatabaseFact]
    public async Task Reference_lists_are_fresh_right_after_a_change()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();

        Assert.DoesNotContain("tst", (await Get(api, executive, "/api/trades")).GetProperty("items").EnumerateArray().Select(trade => trade.GetProperty("code").GetString()));
        var created = await api.SendAsync(HttpMethod.Post, "/api/trades", executive,
            new { code = "TST", // stored as "tst"
            nameAr = "تجربة", nameEn = "Test trade", defaultDailyRate = 300m, defaultWorkType = "General" });
        await EnsureStatus(created, HttpStatusCode.Created);
        Assert.Contains("tst", (await Get(api, executive, "/api/trades")).GetProperty("items").EnumerateArray().Select(trade => trade.GetProperty("code").GetString()));

        var roles = await Get(api, executive, "/api/roles");
        var manager = roles.GetProperty("roles").EnumerateArray().Single(role => role.GetProperty("code").GetString() == "manager");
        var permissions = manager.GetProperty("permissions").Deserialize<Dictionary<string, string[]>>()!;
        permissions["accounts"] = ["View"];
        await EnsureStatus(await api.SendAsync(HttpMethod.Put, "/api/roles/manager/permissions", executive,
            new { permissions, rowVersion = manager.GetProperty("rowVersion").GetString() }), HttpStatusCode.OK);

        var after = (await Get(api, executive, "/api/roles")).GetProperty("roles").EnumerateArray().Single(role => role.GetProperty("code").GetString() == "manager");
        Assert.Equal(["View"], after.GetProperty("permissions").GetProperty("accounts").EnumerateArray().Select(action => action.GetString()));
    }

    private static async Task<JsonElement> Get(ProjectsApi api, Session session, string path) => await Get(api.Access, session, path);

    private static async Task<JsonElement> Get(AccessApi api, Session session, string path)
    {
        var response = await api.SendAsync(HttpMethod.Get, path, session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }
}
