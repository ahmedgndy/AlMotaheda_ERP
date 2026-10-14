using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Projects.Tests.Database.Core;

/// <summary>
/// Calls the suppliers, items, expenses and payments endpoints the way the frontend will, on top of
/// <see cref="ProjectsApi"/>. Every helper asserts the expected status, so a test reads as the business flow.
/// </summary>
internal static class CoreApi
{
    public const string Today = "2026-01-15";

    public static async Task<int> CreateSupplierAsync(this ProjectsApi api, Session session, string nameEn, string kind = "Materials")
    {
        var response = await api.SendAsync(HttpMethod.Post, "/api/suppliers", session, new
        {
            nameAr = $"مورد {nameEn}",
            nameEn,
            kind,
            phone = "0100000000",
        });
        await EnsureStatus(response, HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetInt32();
    }

    public static async Task<JsonElement> CreateItemAsync(this ProjectsApi api, Session session, int projectId, string nameEn, string category = "Concrete")
    {
        var response = await api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/items", session, new
        {
            nameAr = $"بند {nameEn}",
            nameEn,
            category,
            unit = "M3",
        });
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    /// <summary>The body of a supply form; <paramref name="changes"/> replaces or adds fields.</summary>
    public static Dictionary<string, object?> SupplyBody(int supplierId, decimal quantity, decimal unitPrice, string channel, Dictionary<string, object?>? changes = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["kind"] = "Supply",
            ["expenseDate"] = "2026-01-10",
            ["supplierId"] = supplierId,
            ["itemName"] = "Cement 42.5",
            ["unit"] = "Bag",
            ["quantity"] = quantity,
            ["unitPrice"] = unitPrice,
            ["channel"] = channel,
            ["invoiceNo"] = channel == "Credit" ? $"INV-{Guid.NewGuid():N}"[..12] : null,
            ["deliveryNoteNo"] = "DN-1",
        };

        foreach (var (key, value) in changes ?? [])
        {
            body[key] = value;
        }

        return body;
    }

    /// <summary>The body of a site expense form.</summary>
    public static Dictionary<string, object?> ExpenseBody(decimal amount, string channel, int? supplierId = null, Dictionary<string, object?>? changes = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["kind"] = "Expense",
            ["expenseDate"] = "2026-01-10",
            ["supplierId"] = supplierId,
            ["category"] = "Lab",
            ["description"] = "Concrete cube tests",
            ["amount"] = amount,
            ["channel"] = channel,
        };

        foreach (var (key, value) in changes ?? [])
        {
            body[key] = value;
        }

        return body;
    }

    public static async Task<JsonElement> RecordAsync(this ProjectsApi api, Session session, int projectId, Dictionary<string, object?> body)
    {
        var response = await api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/expenses", session, body);
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    /// <summary>The body for <c>PUT …/expenses/{id}</c> from the record as last read, with <paramref name="changes"/>.</summary>
    public static Dictionary<string, object?> EditBody(JsonElement expense, Dictionary<string, object?>? changes = null)
    {
        var body = new Dictionary<string, object?>();
        foreach (var name in new[]
                 {
                     "kind", "expenseDate", "supplierId", "itemName", "unit", "quantity", "unitPrice", "category", "description",
                     "amount", "channel", "invoiceNo", "deliveryNoteNo", "catalogItemId", "projectItemId", "notes", "rowVersion",
                 })
        {
            body[name] = expense.GetProperty(name).Clone();
        }

        foreach (var (key, value) in changes ?? [])
        {
            body[key] = value;
        }

        return body;
    }

    public static Task<HttpResponseMessage> ReviewAsync(this ProjectsApi api, Session session, int projectId, JsonElement expense, string action, string? note = null) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/expenses/{expense.GetProperty("id").GetInt32()}/{action}", session,
            new { note, rowVersion = expense.GetProperty("rowVersion").GetString() });

    public static async Task<JsonElement> ApproveAsync(this ProjectsApi api, Session session, int projectId, JsonElement expense)
    {
        var response = await api.ReviewAsync(session, projectId, expense, "approve");
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    /// <summary>Records a record and has <paramref name="reviewer"/> approve it; returns the approved record.</summary>
    public static async Task<JsonElement> RecordApprovedAsync(this ProjectsApi api, Session recorder, Session reviewer, int projectId, Dictionary<string, object?> body)
    {
        var recorded = await api.RecordAsync(recorder, projectId, body);
        return await api.ApproveAsync(reviewer, projectId, recorded);
    }

    public static async Task<JsonElement> GetExpenseAsync(this ProjectsApi api, Session session, int projectId, int expenseId)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/expenses/{expenseId}", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    public static async Task<JsonElement> ListExpensesAsync(this ProjectsApi api, Session session, int projectId, string query = "")
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/expenses?pageSize=100{query}", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    public static Task<HttpResponseMessage> LinkAsync(this ProjectsApi api, Session session, int projectId, JsonElement expense, int? itemId) =>
        api.SendAsync(HttpMethod.Put, $"/api/projects/{projectId}/expenses/{expense.GetProperty("id").GetInt32()}/item", session,
            new { itemId, rowVersion = expense.GetProperty("rowVersion").GetString() });

    public static async Task<JsonElement> ItemsAsync(this ProjectsApi api, Session session, int projectId, string query = "")
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/items{query}", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    public static async Task<JsonElement> ItemDetailAsync(this ProjectsApi api, Session session, int projectId, int? itemId)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/items/{(itemId is { } id ? id.ToString() : "unallocated")}", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    public static object PaymentBody(int supplierId, decimal amount, (int ExpenseId, decimal Amount)[] allocations, Guid? key = null) => new
    {
        supplierId,
        paymentDate = "2026-01-12",
        amount,
        method = "BankTransfer",
        reference = "TRF-100",
        idempotencyKey = (key ?? Guid.NewGuid()).ToString(),
        allocations = allocations.Select(line => new { expenseId = line.ExpenseId, amount = line.Amount }).ToArray(),
    };

    public static Task<HttpResponseMessage> PayAsync(this ProjectsApi api, Session session, int projectId, object body) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/supplier-payments", session, body);

    public static async Task<JsonElement> PaidAsync(this ProjectsApi api, Session session, int projectId, int supplierId, decimal amount, params (int ExpenseId, decimal Amount)[] allocations)
    {
        var response = await api.PayAsync(session, projectId, PaymentBody(supplierId, amount, allocations));
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    public static Task<HttpResponseMessage> VoidAsync(this ProjectsApi api, Session session, int projectId, JsonElement payment, string reason = "Entered twice") =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/supplier-payments/{payment.GetProperty("id").GetInt32()}/void", session,
            new { reason, rowVersion = payment.GetProperty("rowVersion").GetString() });

    public static async Task<JsonElement> BalancesAsync(this ProjectsApi api, Session session, int projectId)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/supplier-balances", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    public static int Id(this JsonElement element) => element.GetProperty("id").GetInt32();

    public static decimal Money(this JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    /// <summary>The usual cast: an Executive, a project with a manager and a site engineer, both signed in.</summary>
    public static async Task<Cast> CastAsync(this ProjectsApi api, string code = "BDR-MALL-011")
    {
        var executive = await api.SignInExecutiveAsync();
        var (managerId, manager) = await api.CreateSignedInUserAsync(executive, "manager");
        var (engineerId, engineer) = await api.CreateSignedInUserAsync(executive, "engineer");
        var (_, accountant) = await api.CreateSignedInUserAsync(executive, "accountant");
        var project = await api.CreateProjectAsync(executive, code, managerId, siteEngineers: [engineerId]);
        return new Cast(executive, manager, engineer, accountant, project.Id());
    }

    internal sealed record Cast(Session Executive, Session Manager, Session Engineer, Session Accountant, int ProjectId);
}
