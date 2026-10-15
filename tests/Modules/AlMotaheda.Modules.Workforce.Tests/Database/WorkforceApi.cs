using System.Net;
using System.Text.Json;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database;
using Microsoft.Extensions.DependencyInjection;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Workforce.Tests.Database;

/// <summary>
/// Calls the Workforce endpoints (and the few Projects endpoints a labor flow needs) the way the frontend will, on
/// top of <see cref="ProjectsApi"/>. Every helper asserts the expected status, so a test reads as the business flow.
/// </summary>
internal static class WorkforceApi
{
    /// <summary>Trade ids of the seeded trades (see <c>TradeSeed</c>).</summary>
    public const int Mason = 3;
    public const int Plumber = 10;
    public const int Guard = 13;

    /// <summary>Today at the company: sheets must be for today or one of the 7 days before.</summary>
    public static DateOnly Today(this ProjectsApi api) => api.Access.App.Services.GetRequiredService<CompanyClock>().Today;

    public static string Iso(this DateOnly date) => date.ToString("yyyy-MM-dd");

    /// <summary>
    /// The usual cast: an Executive (adds workers, items and pays), a project with a manager (approves sheets) and a
    /// site engineer (records sheets), and an accountant (records worker payments).
    /// </summary>
    public static async Task<Cast> CastAsync(this ProjectsApi api, string code = "BDR-LAB-001")
    {
        var executive = await api.SignInExecutiveAsync();
        var (managerId, manager) = await api.CreateSignedInUserAsync(executive, "manager");
        var (engineerId, engineer) = await api.CreateSignedInUserAsync(executive, "engineer");
        var (_, accountant) = await api.CreateSignedInUserAsync(executive, "accountant");
        var project = await api.CreateProjectAsync(executive, code, managerId, siteEngineers: [engineerId]);
        return new Cast(executive, manager, engineer, engineerId, accountant, project.Id(), managerId);
    }

    public static async Task<int> CreateProjectAsync(this ProjectsApi api, Cast cast, string code)
    {
        var project = await api.CreateProjectAsync(cast.Executive, code, cast.ManagerId, siteEngineers: [cast.EngineerId]);
        return project.Id();
    }

    public static async Task<int> CreateItemAsync(this ProjectsApi api, Session session, int projectId, string nameEn, string category = "Mep")
    {
        var response = await api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/items", session, new
        {
            nameAr = $"بند {nameEn}",
            nameEn,
            category,
            unit = "Lot",
        });
        await EnsureStatus(response, HttpStatusCode.Created);
        return (await Json(response)).Id();
    }

    /// <summary>A new worker of <paramref name="tradeId"/>, assigned to <paramref name="projectId"/> from 10 days ago.</summary>
    public static async Task<int> HireAsync(this ProjectsApi api, Session executive, int projectId, int tradeId, string nameEn, decimal? dailyRate = null)
    {
        var created = await api.SendAsync(HttpMethod.Post, "/api/workers", executive, new
        {
            nameAr = $"عامل {nameEn}",
            nameEn,
            tradeId,
            dailyRate,
        });
        await EnsureStatus(created, HttpStatusCode.Created);
        var workerId = (await Json(created)).Id();

        await api.AssignAsync(executive, projectId, workerId);
        return workerId;
    }

    public static async Task AssignAsync(this ProjectsApi api, Session executive, int projectId, int workerId, decimal? dailyRate = null)
    {
        var assigned = await api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/workers", executive, new
        {
            workerId,
            dailyRate,
            startDate = api.Today().AddDays(-10).Iso(),
        });
        await EnsureStatus(assigned, HttpStatusCode.Created);
    }

    /// <summary>One line of a sheet form.</summary>
    public static object Line(int workerId, decimal days = 1m, (int ItemId, decimal Portion)[]? allocations = null, decimal overtimeHours = 0m,
        (string Kind, string Type, decimal Amount, string? Reason)[]? adjustments = null) => new
        {
            workerId,
            days,
            overtimeHours,
            adjustments = (adjustments ?? []).Select(adjustment => new
            {
                kind = adjustment.Kind,
                type = adjustment.Type,
                amount = adjustment.Amount,
                reason = adjustment.Reason,
            }).ToArray(),
            allocations = (allocations ?? []).Select(allocation => new { itemId = allocation.ItemId, portion = allocation.Portion }).ToArray(),
        };

    public static Task<HttpResponseMessage> RecordSheetAsync(this ProjectsApi api, Session engineer, int projectId, DateOnly workDate, bool submit, params object[] lines) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/labor/sheets", engineer, new
        {
            workDate = workDate.Iso(),
            shift = "Day",
            submit,
            lines,
        });

    /// <summary>The engineer records and submits a sheet; the manager approves it. Returns the approved sheet.</summary>
    public static async Task<JsonElement> RecordApprovedSheetAsync(this ProjectsApi api, Cast cast, int projectId, DateOnly workDate, params object[] lines)
    {
        var recorded = await api.RecordSheetAsync(cast.Engineer, projectId, workDate, submit: true, lines);
        await EnsureStatus(recorded, HttpStatusCode.Created);
        var sheet = await Json(recorded);

        var approved = await api.ReviewAsync(cast.Manager, projectId, sheet, "approve");
        await EnsureStatus(approved, HttpStatusCode.OK);
        return await Json(approved);
    }

    public static Task<HttpResponseMessage> ReviewAsync(this ProjectsApi api, Session reviewer, int projectId, JsonElement sheet, string action, string? note = null) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/labor/sheets/{sheet.Id()}/{action}", reviewer,
            new { note, rowVersion = sheet.GetProperty("rowVersion").GetString() });

    public static Task<HttpResponseMessage> AllocateLineAsync(this ProjectsApi api, Session session, int projectId, int lineId, params (int ItemId, decimal Portion)[] allocations) =>
        api.SendAsync(HttpMethod.Put, $"/api/projects/{projectId}/labor/lines/{lineId}/allocations", session, new
        {
            allocations = allocations.Select(allocation => new { itemId = allocation.ItemId, portion = allocation.Portion }).ToArray(),
        });

    public static async Task<JsonElement> SummaryAsync(this ProjectsApi api, Session session, int projectId)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/projects/{projectId}/labor/summary", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    public static Task<HttpResponseMessage> PayAsync(this ProjectsApi api, Session session, int workerId, decimal amount, Guid? key = null) =>
        api.SendAsync(HttpMethod.Post, $"/api/workers/{workerId}/payments", session, new
        {
            paymentDate = api.Today().Iso(),
            amount,
            method = "Cash",
            reference = "Site cash",
            idempotencyKey = (key ?? Guid.NewGuid()).ToString(),
        });

    public static async Task<JsonElement> AccountAsync(this ProjectsApi api, Session session, int workerId)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/workers/{workerId}/account", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    /// <summary>Runs <paramref name="call"/> with a service from a fresh request scope, as the Projects module would.</summary>
    public static async Task<T> WithServiceAsync<TService, T>(this ProjectsApi api, Func<TService, Task<T>> call)
        where TService : notnull
    {
        await using var scope = api.Access.App.Services.CreateAsyncScope();
        return await call(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public static int Id(this JsonElement element) => element.GetProperty("id").GetInt32();

    public static decimal Money(this JsonElement element, string property) => element.GetProperty(property).GetDecimal();

    /// <summary>The lines of a sheet view, by worker id.</summary>
    public static Dictionary<int, JsonElement> LinesByWorker(this JsonElement sheet) =>
        sheet.GetProperty("lines").EnumerateArray().ToDictionary(line => line.GetProperty("workerId").GetInt32());

    internal sealed record Cast(Session Executive, Session Manager, Session Engineer, int EngineerId, Session Accountant, int ProjectId, int ManagerId);
}
