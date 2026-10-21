using Microsoft.Extensions.FileProviders;

// Development-only. Serves the Flutter web build (built with --dart-define=API_BASE_URL=/) and forwards every /api
// request to the local demo API, so the app and the API share one origin: no CORS, and the API's refresh cookie
// (Secure, SameSite=Strict, HttpOnly) is kept by the browser.
//
// Each demo account can use its own host name, http://a1.localhost:8100 … http://a12.localhost:8100: browsers send
// *.localhost to this machine and keep cookies and storage per host name, so the accounts stay signed in side by side.
//
//   dotnet run --project tools/dev/WebDemo -- --web <flutter>/build/web [--api https://localhost:7200] [--port 8100]
//       [--accounts .local/web-accounts.json --accounts-env .local/demo-accounts.env]   (lists them at /accounts)

var options = Args.Parse(args);
if (!Uri.TryCreate(options.Api, UriKind.Absolute, out var api) || !(api.IsLoopback || api.Host == "localhost"))
{
    Console.Error.WriteLine($"Refusing: the web demo only forwards to an API on this machine (got {options.Api}).");
    return 1;
}

if (!File.Exists(Path.Combine(options.Web, "index.html")))
{
    Console.Error.WriteLine($"No Flutter web build at {options.Web} (flutter build web --dart-define=API_BASE_URL=/).");
    return 1;
}

var builder = WebApplication.CreateBuilder();
builder.WebHost.UseUrls($"http://localhost:{options.Port}");
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// The demo API uses the .NET development certificate; this forwarder only ever talks to that local API.
builder.Services.AddHttpClient("api", client => client.BaseAddress = api)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true },
    });

var app = builder.Build();

app.Map("/api/{**rest}", async (HttpContext context, IHttpClientFactory factory) =>
{
    var client = factory.CreateClient("api");
    using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), context.Request.Path + context.Request.QueryString);
    if (context.Request.ContentLength > 0 || context.Request.Headers.TransferEncoding.Count > 0)
    {
        request.Content = new StreamContent(context.Request.Body);
    }

    foreach (var (name, values) in context.Request.Headers)
    {
        if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        if (!request.Headers.TryAddWithoutValidation(name, values.ToArray()))
        {
            request.Content?.Headers.TryAddWithoutValidation(name, values.ToArray());
        }
    }

    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
    context.Response.StatusCode = (int)response.StatusCode;
    foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
    {
        // Kestrel writes its own framing; the body is streamed as it comes (server-sent events included).
        if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        context.Response.Headers[name] = values.ToArray();
    }

    await using var body = await response.Content.ReadAsStreamAsync(context.RequestAborted);
    var buffer = new byte[16 * 1024];
    int read;
    while ((read = await body.ReadAsync(buffer, context.RequestAborted)) > 0)
    {
        await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), context.RequestAborted);
        await context.Response.Body.FlushAsync(context.RequestAborted);
    }
});

// A page that lists the demo accounts, each with its own host name (its own cookies), for side-by-side testing.
app.MapGet("/accounts", () => Results.Content(AccountsPage.Render(options.Accounts, options.AccountsEnv, options.Port), "text/html; charset=utf-8"));

var files = new PhysicalFileProvider(Path.GetFullPath(options.Web));
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ServeUnknownFileTypes = true });
app.MapFallback(async context =>
{
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(files.GetFileInfo("index.html"));
});

Console.WriteLine($"Web demo: http://localhost:{options.Port} (or http://a1.localhost:{options.Port} … a12) → API {api}");
await app.RunAsync();
return 0;

internal static class AccountsPage
{
    /// <summary><paramref name="accounts"/>: JSON [{host, name, role, email, try}]; the shared demo password is read from <paramref name="envFile"/> (DEMO_PASSWORD).</summary>
    public static string Render(string? accounts, string? envFile, int port)
    {
        if (accounts is null || !File.Exists(accounts))
        {
            return "<p>Start with --accounts .local/web-accounts.json to list the demo accounts here.</p>";
        }

        var password = envFile is not null && File.Exists(envFile)
            ? File.ReadAllLines(envFile).Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("DEMO_PASSWORD=", StringComparison.Ordinal))?["DEMO_PASSWORD=".Length..].Trim('\'', '"') ?? ""
            : "";
        var rows = System.Text.Json.JsonDocument.Parse(File.ReadAllText(accounts)).RootElement.EnumerateArray().Select(account =>
        {
            string Field(string name) => System.Net.WebUtility.HtmlEncode(account.GetProperty(name).GetString() ?? "");
            var url = $"http://{Field("host")}.localhost:{port}/";
            return $"<tr><td><a href=\"{url}\" target=\"{Field("host")}\">{url}</a></td><td>{Field("name")}</td><td>{Field("role")}</td>"
                   + $"<td><code>{Field("email")}</code> <button onclick=\"navigator.clipboard.writeText('{Field("email")}')\">copy</button></td><td>{Field("try")}</td></tr>";
        });
        var secret = System.Net.WebUtility.HtmlEncode(password);
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
            <title>Demo accounts</title>
            <style>
              body{font:14px system-ui,sans-serif;margin:24px;color:#14213d;background:#fff}
              table{border-collapse:collapse;width:100%}td,th{border-bottom:1px solid #e5e7eb;padding:8px;text-align:left;vertical-align:top}
              th{background:#f3f4f6}code{background:#f3f4f6;padding:2px 4px;border-radius:4px}button{font-size:12px;margin-left:4px}
              .note{background:#fff7e6;border:1px solid #f5d08a;padding:10px;border-radius:6px;margin:12px 0}
            </style></head><body>
            <h1>AlMotaheda demo accounts (development only)</h1>
            <div class="note">Every account uses the password <code>{{secret}}</code> <button onclick="navigator.clipboard.writeText('{{secret}}')">copy</button>.
            Each link has its own host name, so each account stays signed in in its own tab. The API allows 10 sign-ins a minute.</div>
            <table><tr><th>Open</th><th>Name</th><th>Role</th><th>E-mail</th><th>What to try</th></tr>{{string.Join("", rows)}}</table>
            </body></html>
            """;
    }
}

internal sealed record Args(string Web, string Api, int Port, string? Accounts, string? AccountsEnv)
{
    public static Args Parse(string[] args)
    {
        string Value(string name, string fallback)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }

        string? Optional(string name) => Array.IndexOf(args, name) is var index and >= 0 && index + 1 < args.Length ? args[index + 1] : null;

        return new Args(Value("--web", "build/web"), Value("--api", "https://localhost:7200"), int.Parse(Value("--port", "8100")), Optional("--accounts"), Optional("--accounts-env"));
    }
}
