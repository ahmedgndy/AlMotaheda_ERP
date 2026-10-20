using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Projects.Tests.Database.Workspace;

/// <summary>
/// Calls the custody and supplier endpoints the way the frontend will, on top of <see cref="ProjectsApi"/>. Every
/// helper asserts the expected status, so a test reads as the business flow.
/// </summary>
internal static class FinancialWorkspaceApi
{
    /// <summary>The cast of the money flows, with every user's id: an Executive, a manager, a site engineer, an accountant.</summary>
    public static async Task<Team> TeamAsync(this ProjectsApi api, string code = "BDR-FIN-001")
    {
        var executive = await api.SignInExecutiveAsync();
        var (managerId, manager) = await api.CreateSignedInUserAsync(executive, "manager");
        var (engineerId, engineer) = await api.CreateSignedInUserAsync(executive, "engineer");
        var (accountantId, accountant) = await api.CreateSignedInUserAsync(executive, "accountant");
        var project = await api.CreateProjectAsync(executive, code, managerId, siteEngineers: [engineerId]);
        return new Team(executive, manager, managerId, engineer, engineerId, accountant, accountantId, project.GetProperty("id").GetInt32());
    }

    /// <summary>A custody funding taken all the way: asked by <paramref name="asker"/>, approved and paid in cash by the Executive.</summary>
    public static async Task<JsonElement> FundAsync(this ProjectsApi api, Session asker, Session confirmer, int projectId, decimal amount, int? holderUserId = null)
    {
        var asked = await api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/custody/fundings", asker, new
        {
            holderUserId,
            amount,
            method = "Cash",
            // Before the purchases of the examples (recorded on 2026-01-10), so the statement reads in order.
            requestedDate = "2026-01-05",
            idempotencyKey = Guid.NewGuid().ToString(),
        });
        await EnsureStatus(asked, HttpStatusCode.Created);
        return await api.SettleAsync(confirmer, await Json(asked));
    }

    public static async Task<JsonElement> CustodyAsync(this ProjectsApi api, Session session, int projectId, int holderUserId)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/custody/{holderUserId}", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    public static async Task<decimal> BalanceAsync(this ProjectsApi api, Session session, int projectId, int holderUserId) =>
        (await api.CustodyAsync(session, projectId, holderUserId)).GetProperty("custody").GetProperty("balance").GetDecimal();

    public static Task<HttpResponseMessage> ReturnAsync(this ProjectsApi api, Session session, int projectId, int holderUserId, decimal amount, Guid? key = null) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/custody/returns", session, new
        {
            holderUserId,
            amount,
            returnDate = "2026-01-12",
            method = "Cash",
            idempotencyKey = (key ?? Guid.NewGuid()).ToString(),
        });

    /// <summary>A material bought on site and paid in cash from the recorder's custody, from a merchant with no supplier record.</summary>
    public static Dictionary<string, object?> CashPurchaseBody(string itemName, decimal quantity, decimal unitPrice, int? projectItemId, Guid? key = null) => new()
    {
        ["kind"] = "Supply",
        ["expenseDate"] = "2026-01-10",
        ["payeeName"] = "Sand merchant",
        ["itemName"] = itemName,
        ["unit"] = "M3",
        ["quantity"] = quantity,
        ["unitPrice"] = unitPrice,
        ["channel"] = "Custody",
        ["projectItemId"] = projectItemId,
        ["idempotencyKey"] = (key ?? Guid.NewGuid()).ToString(),
    };

    public static async Task<JsonElement> AgreeAsync(
        this ProjectsApi api, Session session, int projectId, int supplierId, int projectItemId, string material, string unit, decimal? quantity = null,
        decimal? unitPrice = null)
    {
        var response = await api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/supply-agreements", session, new
        {
            supplierId,
            projectItemId,
            materialName = material,
            unit,
            agreedQuantity = quantity,
            agreedUnitPrice = unitPrice,
        });
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    public static async Task<JsonElement> ProjectSuppliersAsync(this ProjectsApi api, Session session, int projectId)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/suppliers", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    public static async Task<JsonElement> SupplierAccountAsync(this ProjectsApi api, Session session, int supplierId)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/suppliers/{supplierId}/account?pageSize=100", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    /// <summary>The row of an item on the items table.</summary>
    public static async Task<JsonElement> ItemRowAsync(this ProjectsApi api, Session session, int projectId, int itemId)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/items", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return (await Json(response)).GetProperty("items").EnumerateArray().Single(row => row.GetProperty("id").GetInt32() == itemId);
    }

    public static decimal Dec(this JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    public sealed record Team(
        Session Executive, Session Manager, int ManagerId, Session Engineer, int EngineerId, Session Accountant, int AccountantId, int ProjectId);
}
