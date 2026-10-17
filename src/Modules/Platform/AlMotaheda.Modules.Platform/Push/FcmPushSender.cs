using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AlMotaheda.Modules.Platform.Common;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Platform.Push;

/// <summary>What one push carries: the stored notification's text and the record it opens. Never amounts or names.</summary>
internal sealed record PushMessage(long NotificationId, string Type, string TitleAr, string TitleEn, string EntityType, string EntityKey, int? ProjectId);

/// <summary>The answer for one device: sent, the token is dead (disable it), or a passing failure (try next time).</summary>
internal enum PushResult { Sent, TokenGone, Failed }

internal interface IPushSender
{
    /// <summary>False when push is not configured; nothing is sent then.</summary>
    bool IsConfigured { get; }

    Task<PushResult> SendAsync(string deviceToken, PushMessage message, CancellationToken cancellationToken);
}

/// <summary>
/// Firebase Cloud Messaging, HTTP v1 API. Signs in to Google with the service account (an RS256 JWT exchanged for an
/// access token, kept until shortly before it expires) using only the .NET crypto classes, so no Firebase SDK is
/// needed on the server.
/// </summary>
internal sealed class FcmPushSender : IPushSender
{
    internal const string HttpClientName = "fcm";
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";

    private readonly IHttpClientFactory _http;
    private readonly TimeProvider _time;
    private readonly PlatformLog _log;
    private readonly ServiceAccount? _account;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;

    public FcmPushSender(IOptions<PushOptions> options, IHttpClientFactory http, TimeProvider time, PlatformLog log)
    {
        _http = http;
        _time = time;
        _log = log;
        _account = Load(options.Value, log);
    }

    public bool IsConfigured => _account is not null;

    public async Task<PushResult> SendAsync(string deviceToken, PushMessage message, CancellationToken cancellationToken)
    {
        if (_account is null)
        {
            return PushResult.Failed;
        }

        var accessToken = await AccessTokenAsync(_account, cancellationToken);
        var client = _http.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{_account.ProjectId}/messages:send")
        {
            Content = JsonContent.Create(Body(deviceToken, message)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return PushResult.Sent;
        }

        var error = await response.Content.ReadAsStringAsync(cancellationToken);
        return IsDeadToken(response.StatusCode, error) ? PushResult.TokenGone : PushResult.Failed;
    }

    /// <summary>The FCM v1 message. The app reads <c>data</c> to open the record; <c>notification</c> is what the phone shows.</summary>
    internal static object Body(string deviceToken, PushMessage message) => new
    {
        message = new
        {
            token = deviceToken,
            notification = new { title = message.TitleAr, body = message.TitleEn },
            data = new Dictionary<string, string>
            {
                ["notificationId"] = message.NotificationId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["type"] = message.Type,
                ["entityType"] = message.EntityType,
                ["entityKey"] = message.EntityKey,
                ["projectId"] = message.ProjectId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "",
            },
            android = new { priority = "HIGH" },
        },
    };

    /// <summary>FCM answers 404 UNREGISTERED for an uninstalled app, and 400 INVALID_ARGUMENT for a malformed token.</summary>
    internal static bool IsDeadToken(HttpStatusCode status, string body) =>
        status == HttpStatusCode.NotFound
        || (status == HttpStatusCode.BadRequest && body.Contains("registration token", StringComparison.OrdinalIgnoreCase))
        || body.Contains("UNREGISTERED", StringComparison.Ordinal);

    private async Task<string> AccessTokenAsync(ServiceAccount account, CancellationToken cancellationToken)
    {
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow();
            if (_accessToken is not null && now < _accessTokenExpiresAt - TimeSpan.FromMinutes(2))
            {
                return _accessToken;
            }

            var client = _http.CreateClient(HttpClientName);
            using var response = await client.PostAsync(account.TokenUri, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = Assertion(account, now),
            }), cancellationToken);
            response.EnsureSuccessStatusCode();
            var token = await response.Content.ReadFromJsonAsync<GoogleToken>(cancellationToken)
                        ?? throw new InvalidOperationException("Google returned no access token.");
            _accessToken = token.AccessToken;
            _accessTokenExpiresAt = now.AddSeconds(token.ExpiresIn);
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>The signed JWT Google exchanges for an access token (RFC 7523).</summary>
    internal static string Assertion(ServiceAccount account, DateTimeOffset now)
    {
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var claims = Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = account.ClientEmail,
            scope = Scope,
            aud = account.TokenUri,
            iat = now.ToUnixTimeSeconds(),
            exp = now.AddHours(1).ToUnixTimeSeconds(),
        }));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(account.PrivateKey);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{claims}.{Encode(signature)}";
    }

    private static ServiceAccount? Load(PushOptions options, PlatformLog log)
    {
        if (!options.Enabled)
        {
            log.PushOff("Push:Enabled is false");
            return null;
        }

        if (string.IsNullOrWhiteSpace(options.FcmServiceAccountFile) || !File.Exists(options.FcmServiceAccountFile))
        {
            log.PushOff("no Firebase service-account file is configured (Push:FcmServiceAccountFile)");
            return null;
        }

        try
        {
            var account = JsonSerializer.Deserialize<ServiceAccount>(File.ReadAllText(options.FcmServiceAccountFile));
            if (account is null || string.IsNullOrEmpty(account.ProjectId) || string.IsNullOrEmpty(account.ClientEmail) || string.IsNullOrEmpty(account.PrivateKey))
            {
                log.PushOff("the Firebase service-account file is incomplete");
                return null;
            }

            return account;
        }
        catch (JsonException)
        {
            log.PushOff("the Firebase service-account file is not valid JSON");
            return null;
        }
    }

    internal sealed record ServiceAccount(
        [property: JsonPropertyName("project_id")] string ProjectId,
        [property: JsonPropertyName("client_email")] string ClientEmail,
        [property: JsonPropertyName("private_key")] string PrivateKey,
        [property: JsonPropertyName("token_uri")] string TokenUri = "https://oauth2.googleapis.com/token");

    private sealed record GoogleToken(
        [property: JsonPropertyName("access_token")] string AccessToken,
        [property: JsonPropertyName("expires_in")] int ExpiresIn);
}
