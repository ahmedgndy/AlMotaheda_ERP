using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Tests.Database;
using AlMotaheda.Modules.Subcontractors.Data;
using Microsoft.Extensions.DependencyInjection;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Subcontractors.Tests.Database;

/// <summary>
/// Calls the subcontractor endpoints the way the frontend will, on top of <see cref="ProjectsApi"/>. Every helper
/// asserts the expected status, so a test reads as the business flow.
/// </summary>
/// <remarks>
/// The default roles: a Manager records (subcontractors VCEF), the Executive approves and voids (the only role
/// with subcontractors Approve and payments Approve by default), the Accountant pays (payments C).
/// </remarks>
internal static class SubcontractsApi
{
    public static async Task<int> CreateCompanyAsync(this ProjectsApi api, Session session, string nameEn, string specialty = "Civil")
    {
        var response = await api.SendAsync(HttpMethod.Post, "/api/subcontractors", session, new
        {
            nameAr = $"شركة {nameEn}",
            nameEn,
            specialty,
            contactName = "Eng. Sameh",
            phone = "+20 100 733 1188",
            email = "projects@example.eg",
        });
        await EnsureStatus(response, HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetInt32();
    }

    public static Dictionary<string, object?> ContractBody(int subcontractorId, string model, decimal value, Dictionary<string, object?>? changes = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["subcontractorId"] = subcontractorId,
            ["model"] = model,
            ["value"] = value,
            ["dailyRate"] = model == "DailyRate" ? 400m : null,
            ["retentionPercent"] = 0m,
            ["startDate"] = "2026-01-01",
            ["endDate"] = "2026-12-31",
            ["status"] = "Active",
            ["scope"] = "Concrete works and steel installation",
        };

        foreach (var (key, value2) in changes ?? [])
        {
            body[key] = value2;
        }

        return body;
    }

    public static Task<HttpResponseMessage> PostContractAsync(this ProjectsApi api, Session session, int projectId, Dictionary<string, object?> body) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/subcontracts", session, body);

    /// <summary>Creates a contract; returns its detail (<c>{ contract, items, ... }</c>).</summary>
    public static async Task<JsonElement> CreateContractAsync(this ProjectsApi api, Session session, int projectId, Dictionary<string, object?> body)
    {
        var response = await api.PostContractAsync(session, projectId, body);
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    public static async Task<JsonElement> ContractAsync(this ProjectsApi api, Session session, int projectId, int contractId)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/subcontracts/{contractId}", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    public static async Task<JsonElement> ContractsAsync(this ProjectsApi api, Session session, int projectId)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/subcontracts", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    public static async Task<JsonElement> AddItemAsync(
        this ProjectsApi api, Session session, int projectId, int contractId, decimal quantity, decimal unitPrice, string unit = "M3", int? projectItemId = null)
    {
        var response = await api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/subcontracts/{contractId}/items", session, new
        {
            name = $"Line {Guid.NewGuid():N}"[..12],
            workType = "Concrete",
            unit,
            quantity,
            unitPrice,
            projectItemId,
        });
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    public static Task<HttpResponseMessage> MeasureAsync(this ProjectsApi api, Session session, int projectId, int contractId, int itemId, decimal? quantity, string measuredOn = "2026-03-01", decimal? percentOfLine = null) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/subcontracts/{contractId}/measurements", session,
            new { contractItemId = itemId, measuredOn, quantity, percentOfLine, note = "Measured on site" });

    public static async Task<JsonElement> MeasuredAsync(this ProjectsApi api, Session session, int projectId, int contractId, int itemId, decimal quantity, string measuredOn = "2026-03-01")
    {
        var response = await api.MeasureAsync(session, projectId, contractId, itemId, quantity, measuredOn);
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    /// <summary>Approves or rejects a measurement (<paramref name="kind"/> <c>measurements</c>) or a sheet (<c>manpower</c>).</summary>
    public static Task<HttpResponseMessage> ReviewAsync(this ProjectsApi api, Session session, int projectId, string kind, JsonElement record, string action, string? note = null) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/subcontract-{kind}/{record.Id()}/{action}", session,
            new { note, rowVersion = record.GetProperty("rowVersion").GetString() });

    public static async Task<JsonElement> ApprovedAsync(this ProjectsApi api, Session session, int projectId, string kind, JsonElement record)
    {
        var response = await api.ReviewAsync(session, projectId, kind, record, "approve");
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    public static Task<HttpResponseMessage> RecordManpowerAsync(this ProjectsApi api, Session session, int projectId, int contractId, int workers, decimal days, string toDate = "2026-03-05", int? projectItemId = null) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/subcontracts/{contractId}/manpower", session,
            new { fromDate = "2026-03-01", toDate, workers, days, workType = "Plastering", projectItemId, note = "Floor 3" });

    public static async Task<JsonElement> ManpowerAsync(this ProjectsApi api, Session session, int projectId, int contractId, int workers, decimal days, string toDate = "2026-03-05")
    {
        var response = await api.RecordManpowerAsync(session, projectId, contractId, workers, days, toDate);
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    public static object PaymentBody(decimal amount, string paymentDate = "2026-03-10", string purpose = "Progress", Guid? key = null) => new
    {
        paymentDate,
        amount,
        method = "Cheque",
        purpose,
        reference = "CHQ-1",
        idempotencyKey = (key ?? Guid.NewGuid()).ToString(),
    };

    public static Task<HttpResponseMessage> PayAsync(this ProjectsApi api, Session session, int projectId, int contractId, object body) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/subcontracts/{contractId}/payments", session, body);

    public static async Task<JsonElement> PaidAsync(this ProjectsApi api, Session session, int projectId, int contractId, decimal amount, string paymentDate = "2026-03-10", string purpose = "Progress")
    {
        var response = await api.PayAsync(session, projectId, contractId, PaymentBody(amount, paymentDate, purpose));
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    public static Task<HttpResponseMessage> VoidAsync(this ProjectsApi api, Session session, int projectId, JsonElement payment, string reason = "Entered twice") =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/subcontract-payments/{payment.Id()}/void", session,
            new { reason, rowVersion = payment.GetProperty("rowVersion").GetString() });

    /// <summary>A project item ("بند"), created by the Executive through the Projects endpoint.</summary>
    public static async Task<JsonElement> CreateProjectItemAsync(this ProjectsApi api, Session executive, int projectId, string nameEn)
    {
        var response = await api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/items", executive, new
        {
            nameAr = $"بند {nameEn}",
            nameEn,
            category = "Concrete",
            unit = "M3",
        });
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    public static Task<HttpResponseMessage> LinkItemAsync(this ProjectsApi api, Session session, int projectId, int contractId, JsonElement item, int? projectItemId) =>
        api.SendAsync(HttpMethod.Put, $"/api/projects/{projectId}/subcontracts/{contractId}/items/{item.Id()}/project-item", session,
            new { projectItemId, rowVersion = item.GetProperty("rowVersion").GetString() });

    public static int Id(this JsonElement element) => element.GetProperty("id").GetInt32();

    public static int ContractId(this JsonElement detail) => detail.GetProperty("contract").Id();

    public static decimal Money(this JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    public static JsonElement Account(this JsonElement detail) => detail.GetProperty("contract").GetProperty("account");

    /// <summary>Runs a query on this module's tables, outside the API, to check what was really written.</summary>
    public static async Task<T> QuerySubAsync<T>(this ProjectsApi api, Func<SubcontractorsDbContext, Task<T>> query)
    {
        await using var scope = api.Access.App.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<SubcontractorsDbContext>());
    }

    /// <summary>Calls one of the contracts this module implements for Projects, as Projects would.</summary>
    public static async Task<T> AskAsync<TService, T>(this ProjectsApi api, Func<TService, Task<T>> question)
        where TService : notnull
    {
        await using var scope = api.Access.App.Services.CreateAsyncScope();
        return await question(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public static Task<ProjectCostFigures> CostFiguresAsync(this ProjectsApi api, int projectId) =>
        api.AskAsync<IEnumerable<IProjectCostSource>, ProjectCostFigures>(sources =>
            sources.Single(source => source.Group == CostGroups.Subcontractors).SummarizeAsync(projectId, null, null, CancellationToken.None));

    public static Task<IReadOnlyList<ItemCostTotal>> ItemTotalsAsync(this ProjectsApi api, int projectId) =>
        api.AskAsync<ISubcontractItemCosts, IReadOnlyList<ItemCostTotal>>(costs => costs.TotalsByItemAsync(projectId, CancellationToken.None));

    /// <summary>The usual cast: an Executive, a manager and an accountant, and a project managed by the manager.</summary>
    public static async Task<Cast> CastAsync(this ProjectsApi api, string code = "BDR-MALL-011")
    {
        var executive = await api.SignInExecutiveAsync();
        var (managerId, manager) = await api.CreateSignedInUserAsync(executive, "manager");
        var (_, accountant) = await api.CreateSignedInUserAsync(executive, "accountant");
        var project = await api.CreateProjectAsync(executive, code, managerId);
        return new Cast(executive, manager, managerId, accountant, project.GetProperty("id").GetInt32());
    }

    internal sealed record Cast(Session Executive, Session Manager, int ManagerId, Session Accountant, int ProjectId);
}
