using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database.Core;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Projects.Tests.Database.PriceControl;

/// <summary>The Price Control calls, the way the frontend makes them.</summary>
internal static class PriceControlApi
{
    public static async Task<JsonElement> ExceptionAsync(this ProjectsApi api, Session session, int exceptionId)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/price-exceptions/{exceptionId}", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    public static async Task<JsonElement> ExceptionsAsync(this ProjectsApi api, Session session, string query = "")
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/price-exceptions{query}", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    /// <summary>One of the Executive's three decisions, with the case's current row version and <paramref name="extra"/> fields.</summary>
    public static async Task<HttpResponseMessage> DecideAsync(
        this ProjectsApi api, Session session, int exceptionId, string action, string? note, Dictionary<string, object?>? extra = null)
    {
        var current = await api.ExceptionAsync(session, exceptionId);
        var body = new Dictionary<string, object?> { ["note"] = note, ["rowVersion"] = current.GetProperty("rowVersion").GetString() };
        foreach (var (key, value) in extra ?? [])
        {
            body[key] = value;
        }

        return await api.SendAsync(HttpMethod.Post, $"/api/price-exceptions/{exceptionId}/{action}", session, body);
    }

    public static async Task<JsonElement> DecidedAsync(
        this ProjectsApi api, Session session, int exceptionId, string action, string note, Dictionary<string, object?>? extra = null)
    {
        var response = await api.DecideAsync(session, exceptionId, action, note, extra);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    /// <summary>The body for <c>PUT /projects/{pid}/expenses/{id}</c> from the record as last read, with <paramref name="changes"/>.</summary>
    public static Dictionary<string, object?> EditBody(JsonElement expense, Dictionary<string, object?> changes)
    {
        var body = new Dictionary<string, object?>();
        foreach (var name in new[]
                 {
                     "kind", "expenseDate", "supplierId", "itemName", "unit", "quantity", "unitPrice", "category", "description", "channel",
                     "invoiceNo", "deliveryNoteNo", "catalogItemId", "projectItemId", "notes", "rowVersion", "priceItemId", "priceJustification",
                 })
        {
            if (expense.TryGetProperty(name, out var value))
            {
                body[name] = value.Clone();
            }
        }

        if (!body.ContainsKey("amount") && expense.GetProperty("kind").GetString() != "Supply")
        {
            body["amount"] = expense.GetProperty("amount").Clone();
        }

        // The price list item and the justification live inside the record's price check.
        if (expense.TryGetProperty("priceCheck", out var check) && check.ValueKind == JsonValueKind.Object)
        {
            body["priceItemId"] = check.GetProperty("priceItemId").GetInt32();
            if (check.TryGetProperty("justification", out var justification))
            {
                body["priceJustification"] = justification.Clone();
            }
        }

        foreach (var (key, value) in changes)
        {
            body[key] = value;
        }

        return body;
    }

    /// <summary>A purchase of 2 tons of cement checked against <paramref name="priceItemId"/>.</summary>
    public static Dictionary<string, object?> CementPurchase(int supplierId, int priceItemId, decimal unitPrice, string? justification = null, Dictionary<string, object?>? changes = null)
    {
        // Dated today, so a reference range approved "from today" applies to the next purchase.
        var body = CoreApi.SupplyBody(supplierId, 2m, unitPrice, "Credit", new()
        {
            ["expenseDate"] = DateTime.UtcNow.ToString("yyyy-MM-dd"),
            ["itemName"] = "Cement 42.5",
            ["unit"] = "Ton",
            ["priceItemId"] = priceItemId,
            ["priceJustification"] = justification,
        });
        foreach (var (key, value) in changes ?? [])
        {
            body[key] = value;
        }

        return body;
    }

    public static Task<ProjectsApi> StartGatedAsync(SqlServerFixture database) =>
        ProjectsApi.StartAsync(database, new Dictionary<string, string?> { ["Projects:PriceCheckPolicy"] = "ExecutiveApproval" });
}
