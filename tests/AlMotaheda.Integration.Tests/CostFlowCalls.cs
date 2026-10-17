using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database;

namespace AlMotaheda.Integration.Tests;

/// <summary>
/// The calls the cost-flow tests make, the way the apps make them (docs/cost-flow). Each returns the response body
/// after checking the status, so a test reads as the business story it checks.
/// </summary>
internal static class CostFlowCalls
{
    /// <summary>Every role a cost-flow story needs, on one project: a manager and a site engineer on it.</summary>
    internal sealed record People(
        Session Executive, Session Manager, int ManagerId, Session Engineer, int EngineerId, Session Accountant, Session FinancialManager, int ProjectId);

    public static async Task<People> PeopleAsync(ProjectsApi api, string code = "CF-001")
    {
        var executive = await api.SignInExecutiveAsync();
        var (managerId, manager) = await api.CreateSignedInUserAsync(executive, "manager");
        var (engineerId, engineer) = await api.CreateSignedInUserAsync(executive, "engineer");
        var (_, accountant) = await api.CreateSignedInUserAsync(executive, "accountant");
        var (_, financialManager) = await api.CreateSignedInUserAsync(executive, "financial_manager");
        var project = await api.CreateProjectAsync(executive, code, managerId, siteEngineers: [engineerId]);
        return new People(executive, manager, managerId, engineer, engineerId, accountant, financialManager, Id(project));
    }

    public static async Task<JsonElement> MasterItemAsync(
        ProjectsApi api, Session session, string nameEn, string kind, string category, string unit, decimal? price = null, string effectiveFrom = "2026-01-01")
    {
        var body = await Send(api, session, HttpMethod.Post, "/api/catalog/items", new
        {
            nameAr = $"صنف {nameEn}",
            nameEn,
            kind,
            category,
            unit,
            price,
            effectiveFrom,
        }, HttpStatusCode.Created);
        return body.GetProperty("item");
    }

    /// <summary>Selects master items for a project; returns master id → the project record it became.</summary>
    public static async Task<Dictionary<int, int>> SelectAsync(ProjectsApi api, Session session, int projectId, params int[] masterItemIds)
    {
        var body = await Send(api, session, HttpMethod.Post, $"/api/projects/{projectId}/catalog-selection", new { masterItemIds });
        return body.GetProperty("added").EnumerateArray()
            .ToDictionary(row => row.GetProperty("masterItemId").GetInt32(), row => row.GetProperty("projectRecordId").GetInt32());
    }

    public static Task<JsonElement> Get(ProjectsApi api, Session session, string path) => Send(api, session, HttpMethod.Get, path, null);

    public static async Task<JsonElement> Send(ProjectsApi api, Session session, HttpMethod method, string path, object? body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await api.SendAsync(method, path, session, body);
        await AccessApi.EnsureStatus(response, expected);
        return response.StatusCode == HttpStatusCode.NoContent ? default : await AccessApi.Json(response);
    }

    /// <summary>Sends and expects a refusal; returns the problem code (or null).</summary>
    public static async Task<string?> Refused(ProjectsApi api, Session session, HttpMethod method, string path, object? body, HttpStatusCode expected)
    {
        var response = await api.SendAsync(method, path, session, body);
        await AccessApi.EnsureStatus(response, expected);
        return response.Content.Headers.ContentLength is 0 ? null : await AccessApi.CodeOf(response);
    }

    public static JsonElement Group(JsonElement overview, string costGroup) =>
        overview.GetProperty("groups").EnumerateArray().Single(group => group.GetProperty("costGroup").GetString() == costGroup);

    public static JsonElement ItemRow(JsonElement items, int itemId) =>
        items.GetProperty("items").EnumerateArray().Single(item => Id(item) == itemId);

    public static int Id(JsonElement element) => element.GetProperty("id").GetInt32();

    public static string Version(JsonElement element) => element.GetProperty("rowVersion").GetString()!;

    public static decimal Money(JsonElement element, string name) => element.GetProperty(name).GetDecimal();

    public static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The caller's notifications, newest first.</summary>
    public static async Task<List<JsonElement>> NotificationsAsync(ProjectsApi api, Session session) =>
        (await Get(api, session, "/api/notifications?pageSize=100")).GetProperty("items").EnumerateArray().ToList();

    /// <summary>Waits (up to 30 s) for the outbox to deliver a notification that matches.</summary>
    public static async Task<JsonElement> WaitForNotificationAsync(ProjectsApi api, Session session, Func<JsonElement, bool> match)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if ((await NotificationsAsync(api, session)).FirstOrDefault(match) is { ValueKind: JsonValueKind.Object } found)
            {
                return found;
            }

            await Task.Delay(500);
        }

        throw new Xunit.Sdk.XunitException("Waited 30 seconds for a notification.");
    }

    public static Func<JsonElement, bool> About(string type, string entityType, int entityKey) => notification =>
        notification.GetProperty("type").GetString() == type
        && notification.GetProperty("entityType").GetString() == entityType
        && notification.GetProperty("entityKey").GetString() == entityKey.ToString(CultureInfo.InvariantCulture);

    /// <summary>Uploads a small PDF to a project's archive and returns the document.</summary>
    public static async Task<JsonElement> UploadPdfAsync(ProjectsApi api, Session session, int projectId, string title)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(title), "title" },
            { new StringContent("Contract"), "category" },
            { new ByteArrayContent(Pad("%PDF-1.7\n1 0 obj\n"u8.ToArray(), 2048)), "file", "supplier-contract.pdf" },
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/projects/{projectId}/documents") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        var response = await api.Access.App.Client.SendAsync(request);
        await AccessApi.EnsureStatus(response, HttpStatusCode.Created);
        return await AccessApi.Json(response);
    }

    private static byte[] Pad(byte[] head, int size)
    {
        var bytes = new byte[size];
        head.CopyTo(bytes, 0);
        for (var index = head.Length; index < size; index++)
        {
            bytes[index] = (byte)'A';
        }

        return bytes;
    }
}
