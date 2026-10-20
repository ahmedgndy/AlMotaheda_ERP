using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database.Core;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Projects.Tests.Database.CostManagement;

/// <summary>The cost management calls, the way the frontend makes them.</summary>
internal static class CostManagementApi
{
    public static Task<HttpResponseMessage> PostSubItemAsync(this ProjectsApi api, Session session, int projectId, int parentItemId, string nameEn) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/items", session, new
        {
            nameAr = $"بند فرعي {nameEn}",
            nameEn,
            category = "Concrete",
            unit = "Ton",
            parentItemId,
        });

    public static async Task<JsonElement> CreateSubItemAsync(this ProjectsApi api, Session session, int projectId, int parentItemId, string nameEn)
    {
        var response = await api.PostSubItemAsync(session, projectId, parentItemId, nameEn);
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    public static Task<HttpResponseMessage> MoveAsync(this ProjectsApi api, Session session, int projectId, JsonElement item, int? parentItemId) =>
        api.SendAsync(HttpMethod.Put, $"/api/projects/{projectId}/items/{item.Id()}/parent", session,
            new { parentItemId, rowVersion = item.GetProperty("rowVersion").GetString() });

    public static async Task<int> CreatePriceItemAsync(this ProjectsApi api, Session session, string nameEn, string unit, decimal min, decimal max)
    {
        var response = await api.SendAsync(HttpMethod.Post, "/api/price-items", session, PriceItemBody(nameEn, unit, min, max));
        await EnsureStatus(response, HttpStatusCode.Created);
        return (await Json(response)).GetProperty("item").Id();
    }

    /// <summary>A new general range, requested by <paramref name="requester"/> and approved by <paramref name="executive"/>, from today.</summary>
    public static async Task ChangeRangeAsync(this ProjectsApi api, Session requester, Session executive, int priceItemId, decimal min, decimal max)
    {
        var requested = await api.SendAsync(HttpMethod.Post, $"/api/price-items/{priceItemId}/ranges", requester,
            new { minPrice = min, maxPrice = max, reason = "New supplier price list" });
        await EnsureStatus(requested, HttpStatusCode.Created);
        var approved = await api.SendAsync(HttpMethod.Post, $"/api/price-ranges/{(await Json(requested)).Id()}/approve", executive, new { });
        await EnsureStatus(approved, HttpStatusCode.OK);
    }

    public static async Task<JsonElement> BreakdownAsync(this ProjectsApi api, Session session, int projectId, int itemId, string query = "")
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/items/{itemId}/cost-breakdown{query}", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    public static object PriceItemBody(string nameEn, string unit, decimal min, decimal max) => new
    {
        nameAr = $"صنف {nameEn}",
        nameEn,
        classification = "CementAggregates",
        unit,
        minPrice = min,
        maxPrice = max,
        effectiveFrom = "2025-12-01",
    };
}
