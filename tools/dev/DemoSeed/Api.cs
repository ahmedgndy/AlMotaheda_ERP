using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AlMotaheda.DemoSeed;

/// <summary>An API answer that was not 2xx: the status, the ProblemDetails <c>code</c> (when there is one) and the body.</summary>
internal sealed class ApiError(HttpStatusCode status, string? code, string body, string request)
    : Exception($"{request} -> {(int)status} {code} {Trim(body)}")
{
    public HttpStatusCode Status { get; } = status;
    public string? Code { get; } = code;

    private static string Trim(string body) => body.Length > 600 ? body[..600] + "…" : body;
}

/// <summary>
/// One signed-in demo user. Signs in through <c>POST /api/auth/login</c> like the app (no shortcut), waits out the
/// login rate limit, and signs in again when the access token has expired.
/// </summary>
internal sealed class Session
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private string? _token;

    public Session(HttpClient http, string email, string password)
    {
        _http = http;
        Email = email;
        Password = password;
    }

    public string Email { get; }
    public string Password { get; private set; }
    public int UserId { get; private set; }
    public bool MustChangePassword { get; private set; }

    /// <summary>Signs in; false when the password is wrong (401), so the caller can try another one.</summary>
    public async Task<bool> TryLoginAsync()
    {
        while (true)
        {
            using var response = await _http.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password, rememberMe = false }, Json);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                Log.Info($"  (login rate limit: waiting {wait.TotalSeconds:0} s)");
                await Task.Delay(wait + TimeSpan.FromSeconds(1));
                continue;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            {
                return false;
            }

            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                throw new ApiError(response.StatusCode, CodeOf(body), body, $"POST /api/auth/login ({Email})");
            }

            var session = JsonNode.Parse(body)!;
            _token = (string)session["accessToken"]!;
            MustChangePassword = (bool)session["mustChangePassword"]!;
            UserId = (int)session["user"]!["id"]!;
            return true;
        }
    }

    public async Task LoginAsync()
    {
        if (!await TryLoginAsync())
        {
            throw new InvalidOperationException($"Cannot sign in as {Email}: the password in .local is not this account's.");
        }
    }

    /// <summary>The first sign-in's forced change, through <c>POST /api/auth/change-password</c>.</summary>
    public async Task ChangePasswordAsync(string newPassword)
    {
        await PostAsync("/api/auth/change-password", new { currentPassword = Password, newPassword });
        Password = newPassword;
        await LoginAsync();
    }

    public Task<JsonNode> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null);

    public Task<JsonNode> PostAsync(string path, object? body = null) => SendAsync(HttpMethod.Post, path, body ?? new { });

    public Task<JsonNode> PutAsync(string path, object body) => SendAsync(HttpMethod.Put, path, body);

    /// <summary>A multipart upload: the form fields and one file.</summary>
    public async Task<JsonNode> UploadAsync(string path, IReadOnlyDictionary<string, string> fields, string fileName, byte[] content, string contentType, string? idempotencyKey = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var form = new MultipartFormDataContent();
            foreach (var (name, value) in fields)
            {
                form.Add(new StringContent(value), name);
            }

            var file = new ByteArrayContent(content);
            file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(file, "file", fileName);

            using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = form };
            if (idempotencyKey is not null)
            {
                request.Headers.Add("Idempotency-Key", idempotencyKey);
            }

            var result = await ExecuteAsync(request, $"POST {path}", attempt);
            if (result is not null)
            {
                return result;
            }
        }
    }

    private async Task<JsonNode> SendAsync(HttpMethod method, string path, object? body)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: Json);
            }

            var result = await ExecuteAsync(request, $"{method} {path}", attempt);
            if (result is not null)
            {
                return result;
            }
        }
    }

    /// <summary>The answer, or null when the token expired and the call should be sent again.</summary>
    private async Task<JsonNode?> ExecuteAsync(HttpRequestMessage request, string label, int attempt)
    {
        if (_token is null)
        {
            await LoginAsync();
        }

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        using var response = await _http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
        {
            await LoginAsync();
            return null;
        }

        if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < 5)
        {
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
            Log.Info($"  (rate limit on {label}: waiting {wait.TotalSeconds:0} s)");
            await Task.Delay(wait + TimeSpan.FromSeconds(1));
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new ApiError(response.StatusCode, CodeOf(text), text, $"{label} as {Email}");
        }

        return string.IsNullOrWhiteSpace(text) ? new JsonObject() : JsonNode.Parse(text)!;
    }

    private static string? CodeOf(string body)
    {
        try
        {
            return JsonNode.Parse(body)?["code"]?.GetValue<string>();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal static class Log
{
    public static int Created { get; private set; }
    public static int Reused { get; private set; }

    public static void Section(string title) => Console.WriteLine($"\n== {title}");

    public static void Info(string message) => Console.WriteLine(message);

    public static void New(string what)
    {
        Created++;
        Console.WriteLine($"  + {what}");
    }

    public static void Kept(string what)
    {
        Reused++;
        Console.WriteLine($"  = {what}");
    }
}

internal static class JsonExtensions
{
    public static int Id(this JsonNode node, string name = "id") => (int)node[name]!;

    public static string Str(this JsonNode node, string name) => (string?)node[name] ?? "";

    public static string? RowVersion(this JsonNode node) => (string?)node["rowVersion"];

    public static IEnumerable<JsonNode> Items(this JsonNode node, string name = "items") =>
        (node[name] as JsonArray ?? []).Where(item => item is not null).Select(item => item!);
}
