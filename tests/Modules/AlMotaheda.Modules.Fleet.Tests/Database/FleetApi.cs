using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Projects.Tests.Database;
using Microsoft.Extensions.DependencyInjection;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Fleet.Tests.Database;

/// <summary>
/// Calls the equipment endpoints the way the frontend will, on top of <see cref="ProjectsApi"/>. Every helper
/// asserts the expected status, so a test reads as the business flow.
/// </summary>
internal static class FleetApi
{
    /// <summary>
    /// The usual cast with default role permissions: an Executive (everything), two managers with one project each
    /// (equipment View, Edit, Approve), an accountant (equipment View, Approve, See money; payments Create) and a
    /// site engineer on project A (equipment View only).
    /// </summary>
    public static async Task<Cast> CastAsync(this ProjectsApi api)
    {
        var executive = await api.SignInExecutiveAsync();
        var (managerId, manager) = await api.CreateSignedInUserAsync(executive, "manager");
        var (otherManagerId, otherManager) = await api.CreateSignedInUserAsync(executive, "manager");
        var (engineerId, engineer) = await api.CreateSignedInUserAsync(executive, "engineer");
        var (_, accountant) = await api.CreateSignedInUserAsync(executive, "accountant");
        var projectA = await api.CreateProjectAsync(executive, "BDR-MALL-011", managerId, siteEngineers: [engineerId]);
        var projectB = await api.CreateProjectAsync(executive, "NAC-TWR-004", otherManagerId);
        return new Cast(executive, manager, otherManager, engineer, accountant, projectA.Id(), projectB.Id());
    }

    public static async Task<int> CreateSupplierAsync(this ProjectsApi api, Session session, string nameEn)
    {
        var response = await api.SendAsync(HttpMethod.Post, "/api/suppliers", session, new
        {
            nameAr = $"مورد {nameEn}",
            nameEn,
            kind = "Equipment",
            phone = "0100000000",
        });
        await EnsureStatus(response, HttpStatusCode.Created);
        return (await Json(response)).Id();
    }

    public static object EquipmentBody(string assetNo, string ownership = "Owned", object? rental = null) => new
    {
        assetNo,
        nameAr = $"حفار {assetNo}",
        nameEn = $"Excavator {assetNo}",
        category = "Heavy",
        typeName = "Excavator",
        serialNo = $"SN-{assetNo}",
        ownership,
        condition = "Good",
        rental,
    };

    public static async Task<JsonElement> RegisterAsync(this ProjectsApi api, Session session, object body)
    {
        var response = await api.SendAsync(HttpMethod.Post, "/api/equipment", session, body);
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    public static async Task<JsonElement> GetEquipmentAsync(this ProjectsApi api, Session session, int equipmentId)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/equipment/{equipmentId}", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    public static Task<HttpResponseMessage> AssignAsync(this ProjectsApi api, Session session, int projectId, int equipmentId, string startDate) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/equipment", session, new { equipmentId, operatorName = "Hany Wagdy", startDate });

    public static async Task<JsonElement> AssignedAsync(this ProjectsApi api, Session session, int projectId, int equipmentId, string startDate)
    {
        var response = await api.AssignAsync(session, projectId, equipmentId, startDate);
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    public static async Task<JsonElement> EndAsync(this ProjectsApi api, Session session, int projectId, JsonElement assignment, string endDate)
    {
        var response = await api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/equipment/{assignment.Id()}/end", session,
            new { endDate, rowVersion = assignment.GetProperty("rowVersion").GetString() });
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    public static async Task<List<JsonElement>> ProjectEquipmentAsync(this ProjectsApi api, Session session, int projectId, bool includeEnded = false)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/equipment?includeEnded={includeEnded}&pageSize=100", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return (await Json(response)).GetProperty("items").EnumerateArray().ToList();
    }

    public static object FuelBody(int equipmentId, decimal liters, decimal pricePerLiter, string costDate = "2026-01-15") => new
    {
        equipmentId, costDate, type = "Fuel", liters, pricePerLiter, channel = "Custody",
    };

    public static object RentalCostBody(int equipmentId, int supplierId, decimal days, decimal rate) => new
    {
        equipmentId, costDate = "2026-01-15", type = "Rental", basis = "Day", quantity = days, rate,
        description = "Excavator rental 1-15 Jan", supplierId, channel = "Credit", invoiceNo = "INV-7781",
    };

    public static object LumpBody(int equipmentId, string type, decimal amount) => new
    {
        equipmentId, costDate = "2026-01-20", type, basis = "Lump", amount, description = "Hydraulic hose replaced", channel = "Custody",
    };

    public static Task<HttpResponseMessage> RecordCostAsync(this ProjectsApi api, Session session, int projectId, object body) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/equipment-costs", session, body);

    public static async Task<JsonElement> RecordedCostAsync(this ProjectsApi api, Session session, int projectId, object body)
    {
        var response = await api.RecordCostAsync(session, projectId, body);
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    public static Task<HttpResponseMessage> ReviewAsync(this ProjectsApi api, Session session, int projectId, JsonElement cost, string action, string? note = null) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/equipment-costs/{cost.Id()}/{action}", session,
            new { note, rowVersion = cost.GetProperty("rowVersion").GetString() });

    public static async Task<JsonElement> ReviewedAsync(this ProjectsApi api, Session session, int projectId, JsonElement cost, string action, string? note = null)
    {
        var response = await api.ReviewAsync(session, projectId, cost, action, note);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    public static Task<HttpResponseMessage> PayAsync(this ProjectsApi api, Session session, int projectId, JsonElement cost) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/equipment-costs/{cost.Id()}/payment", session,
            new { paidOn = "2026-02-05", method = "BankTransfer", reference = "TRF-2201", rowVersion = cost.GetProperty("rowVersion").GetString() });

    /// <summary>
    /// An equipment cost paid all the way: requested by <paramref name="session"/>, approved and sent to the bank by
    /// the Executive, confirmed by <paramref name="session"/>. Returns the cost as it reads now.
    /// </summary>
    public static async Task<JsonElement> PaidCostAsync(this ProjectsApi api, Session session, int projectId, JsonElement cost)
    {
        var response = await api.PayAsync(session, projectId, cost);
        await EnsureStatus(response, HttpStatusCode.Created);
        await api.SettleAsync(session, await Json(response));

        var read = await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/equipment-costs/{cost.Id()}", session);
        await EnsureStatus(read, HttpStatusCode.OK);
        return await Json(read);
    }

    public static Task<HttpResponseMessage> VoidPaymentAsync(this ProjectsApi api, Session session, int projectId, JsonElement cost) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/equipment-costs/{cost.Id()}/payment/void", session,
            new { reason = "Paid from the wrong account", rowVersion = cost.GetProperty("rowVersion").GetString() });

    public static async Task<JsonElement> ListCostsAsync(this ProjectsApi api, Session session, int projectId, string query = "")
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/equipment-costs?pageSize=100{query}", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    public static async Task<JsonElement> SummaryAsync(this ProjectsApi api, Session session, int projectId)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/equipment-summary", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    /// <summary>Runs a query on the Fleet tables of the running API.</summary>
    public static async Task<T> FleetQueryAsync<T>(this ProjectsApi api, Func<FleetDbContext, Task<T>> query)
    {
        await using var scope = api.Access.App.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<FleetDbContext>());
    }

    /// <summary>Asks a service of the running API, in its own scope, the way another module would.</summary>
    public static async Task<T> WithServicesAsync<T>(this ProjectsApi api, Func<IServiceProvider, Task<T>> call)
    {
        await using var scope = api.Access.App.Services.CreateAsyncScope();
        return await call(scope.ServiceProvider);
    }

    public static int Id(this JsonElement element) => element.GetProperty("id").GetInt32();

    public static decimal Money(this JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    internal sealed record Cast(
        Session Executive, Session Manager, Session OtherManager, Session Engineer, Session Accountant, int ProjectA, int ProjectB);
}
