using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Office.Data;
using Microsoft.Extensions.DependencyInjection;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Office.Tests.Database;

/// <summary>The people of an office test: the reviewers, two custodians (one holds the custody), and an engineer.</summary>
internal sealed record OfficeCast(
    Session Executive,
    Session Accountant,
    Session SecondAccountant,
    Session Custodian,
    Session OtherCustodian,
    Session Engineer,
    int CustodyId);

/// <summary>The office calls, the way the app makes them.</summary>
internal static class OfficeApi
{
    public static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");

    /// <summary>A fresh database with the gating price policy and a private folder for the photos.</summary>
    public static Task<AccessApi> StartAsync(SqlServerFixture database) =>
        AccessApi.StartAsync(database, new Dictionary<string, string?>
        {
            ["Projects:PriceCheckPolicy"] = "ExecutiveApproval",
            ["Office:Storage:RootPath"] = Path.Combine(Path.GetTempPath(), "almotaheda-office-tests", Guid.NewGuid().ToString("N")),
            ["Office:RoutineApprovalLimit"] = "5000",
        });

    public static async Task<OfficeCast> CastAsync(this AccessApi api, decimal lowBalanceThreshold = 0m)
    {
        var executive = await api.SignInExecutiveAsync();
        var (_, accountant) = await api.CreateSignedInUserAsync(executive, "accountant");
        var (_, secondAccountant) = await api.CreateSignedInUserAsync(executive, "accountant");
        var (_, custodian) = await api.CreateSignedInUserAsync(executive, "office_admin");
        var (_, otherCustodian) = await api.CreateSignedInUserAsync(executive, "office_admin");
        var (_, engineer) = await api.CreateSignedInUserAsync(executive, "engineer");

        var custody = await api.CreatedAsync(accountant, "/api/office/custodies", new
        {
            nameAr = $"عهدة المكتب {Guid.NewGuid():N}"[..20],
            nameEn = $"Office cash {Guid.NewGuid():N}"[..20],
            holderUserId = custodian.UserId,
            lowBalanceThreshold,
        });

        return new OfficeCast(executive, accountant, secondAccountant, custodian, otherCustodian, engineer, custody.Id());
    }

    public static async Task<JsonElement> CreatedAsync(this AccessApi api, Session session, string path, object body)
    {
        var response = await api.SendAsync(HttpMethod.Post, path, session, body);
        await EnsureStatus(response, HttpStatusCode.Created);
        return await Json(response);
    }

    public static async Task<JsonElement> OkAsync(this AccessApi api, Session session, HttpMethod method, string path, object? body = null)
    {
        var response = await api.SendAsync(method, path, session, body);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    public static Task<JsonElement> GetAsync(this AccessApi api, Session session, string path) => api.OkAsync(session, HttpMethod.Get, path);

    public static async Task<JsonElement> BalanceAsync(this AccessApi api, Session session, int custodyId) =>
        (await api.GetAsync(session, $"/api/office/custodies/{custodyId}")).GetProperty("balance");

    public static decimal Money(this JsonElement element, string name) => element.GetProperty(name).GetDecimal();

    public static int Id(this JsonElement element) => element.GetProperty("id").GetInt32();

    public static string RowVersion(this JsonElement element) => element.GetProperty("rowVersion").GetString()!;

    public static string Status(this JsonElement element) => element.GetProperty("status").GetString()!;

    public static IReadOnlyList<string> Strings(this JsonElement element, string name) =>
        element.GetProperty(name).EnumerateArray().Select(value => value.GetString()!).ToList();

    /// <summary>A small, unique JPEG-looking file (the server reads only its first bytes to decide the type).</summary>
    public static byte[] Jpeg() => [0xFF, 0xD8, 0xFF, 0xE0, .. Guid.NewGuid().ToByteArray(), .. Guid.NewGuid().ToByteArray(), 0xFF, 0xD9];

    public static byte[] Pdf() => [.. "%PDF-1.7\n"u8, .. Guid.NewGuid().ToByteArray()];

    public static async Task<HttpResponseMessage> UploadAsync(this AccessApi api, Session session, string purpose, byte[] bytes, Guid? key = null)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        content.Add(file, "file", "receipt.jpg");
        content.Add(new StringContent(purpose), "purpose");
        content.Add(new StringContent((key ?? Guid.NewGuid()).ToString()), "idempotencyKey");
        content.Add(new StringContent("Camera"), "captureSource");
        content.Add(new StringContent(DateTime.UtcNow.ToString("O")), "capturedAt");
        content.Add(new StringContent("Pixel test"), "deviceModel");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/office/attachments") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return await api.App.Client.SendAsync(request);
    }

    public static async Task<int> PhotoAsync(this AccessApi api, Session session, string purpose = "Receipt", byte[]? bytes = null)
    {
        var response = await api.UploadAsync(session, purpose, bytes ?? Jpeg());
        await EnsureStatus(response, HttpStatusCode.Created);
        return (await Json(response)).Id();
    }

    public static Dictionary<string, object?> ExpenseBody(
        int custodyId,
        decimal quantity,
        decimal unitPrice,
        IReadOnlyList<int> attachmentIds,
        string category = "Stationery",
        string unit = "Piece",
        string? vendor = null,
        string? invoiceNo = null,
        int? priceItemId = null,
        string? priceJustification = null,
        int? spendRequestId = null,
        decimal? tax = null,
        Guid? key = null) => new()
    {
        ["custodyId"] = custodyId,
        ["category"] = category,
        ["priceItemId"] = priceItemId,
        ["description"] = "Office supplies",
        ["vendor"] = vendor,
        ["invoiceNo"] = invoiceNo,
        ["quantity"] = quantity,
        ["unit"] = unit,
        ["unitPrice"] = unitPrice,
        ["tax"] = tax,
        ["expenseDate"] = Today,
        ["spendRequestId"] = spendRequestId,
        ["priceJustification"] = priceJustification,
        ["attachmentIds"] = attachmentIds,
        ["idempotencyKey"] = key ?? Guid.NewGuid(),
    };

    /// <summary>Records a routine expense with one new receipt photo.</summary>
    public static async Task<JsonElement> ExpenseAsync(this AccessApi api, OfficeCast cast, decimal quantity, decimal unitPrice, string? vendor = null, string? invoiceNo = null)
    {
        var receipt = await api.PhotoAsync(cast.Custodian);
        return await api.CreatedAsync(cast.Custodian, "/api/office/expenses", ExpenseBody(cast.CustodyId, quantity, unitPrice, [receipt], vendor: vendor, invoiceNo: invoiceNo));
    }

    public static async Task<HttpResponseMessage> DecideExpenseAsync(this AccessApi api, Session session, int expenseId, string action, string? note = "Checked")
    {
        var current = await api.GetAsync(session, $"/api/office/expenses/{expenseId}");
        return await api.SendAsync(HttpMethod.Post, $"/api/office/expenses/{expenseId}/{action}", session, new { note, rowVersion = current.RowVersion() });
    }

    public static async Task<JsonElement> DecidedExpenseAsync(this AccessApi api, Session session, int expenseId, string action, string? note = "Checked")
    {
        var response = await api.DecideExpenseAsync(session, expenseId, action, note);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    /// <summary>
    /// The whole funding path: the custodian asks, the Accountant agrees (Finance gets a payment request), the
    /// Executive pays it in Finance, the custodian confirms the cash. Returns the funding request as last seen.
    /// </summary>
    public static async Task<JsonElement> FundAsync(this AccessApi api, OfficeCast cast, decimal amount, decimal? approved = null, decimal? received = null, string? note = null)
    {
        var funding = await api.CreatedAsync(cast.Custodian, "/api/office/funding-requests", new
        {
            custodyId = cast.CustodyId, amount, reason = "Office cash for the week", idempotencyKey = Guid.NewGuid(),
        });
        funding = await api.OkAsync(cast.Accountant, HttpMethod.Post, $"/api/office/funding-requests/{funding.Id()}/approve",
            new { approvedAmount = approved, method = "Cash", rowVersion = funding.RowVersion() });
        Assert.Equal("Approved", funding.Status());

        await api.PayInFinanceAsync(cast.Executive, funding.GetProperty("paymentRequestId").GetInt32());
        funding = await api.GetAsync(cast.Custodian, $"/api/office/funding-requests/{funding.Id()}");
        Assert.Equal("Disbursed", funding.Status());

        return await api.OkAsync(cast.Custodian, HttpMethod.Post, $"/api/office/funding-requests/{funding.Id()}/confirm-receipt",
            new { receivedAmount = received, note, rowVersion = funding.RowVersion() });
    }

    public static async Task PayInFinanceAsync(this AccessApi api, Session executive, int paymentRequestId)
    {
        var detail = await api.GetAsync(executive, $"/api/payment-requests/{paymentRequestId}");
        var paid = await api.SendAsync(HttpMethod.Post, $"/api/payment-requests/{paymentRequestId}/approve-and-pay", executive,
            new { paidOn = Today, rowVersion = detail.GetProperty("rowVersion").GetString() });
        await EnsureStatus(paid, HttpStatusCode.OK);
    }

    public static async Task<T> OfficeQueryAsync<T>(this AccessApi api, Func<OfficeDbContext, Task<T>> query)
    {
        await using var scope = api.App.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<OfficeDbContext>());
    }

    /// <summary>The dispatcher stores notifications in the background after the commit: wait until one of this type is there.</summary>
    public static async Task<List<JsonElement>> NotificationsAsync(this AccessApi api, Session session, string type, int atLeast = 1)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var body = await api.GetAsync(session, "/api/notifications?pageSize=100");
            var found = body.GetProperty("items").EnumerateArray().Where(item => item.GetProperty("type").GetString() == type).ToList();
            if (found.Count >= atLeast || timer.Elapsed > TimeSpan.FromSeconds(30))
            {
                return found;
            }

            await Task.Delay(250);
        }
    }
}
