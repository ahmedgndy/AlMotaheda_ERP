using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace AlMotaheda.Api.Tests;

/// <summary>
/// Runs the real API host in-process on a free local port, and gives tests an <see cref="HttpClient"/> for it.
/// </summary>
/// <remarks>
/// It uses the same composition as Program.cs (<see cref="ApiHost.AddApiHost"/> and <see cref="ApiHost.UseApiHost"/>),
/// so tests exercise the production pipeline over real HTTP.
/// </remarks>
internal sealed class TestApplication : IAsyncDisposable
{
    private readonly WebApplication _app;

    private TestApplication(WebApplication app)
    {
        _app = app;
        Client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
    }

    public HttpClient Client { get; }

    public IServiceProvider Services => _app.Services;

    /// <param name="environment">Hosting environment name. "Testing" behaves like a non-development environment.</param>
    /// <param name="configuration">Settings applied before the host is composed (same keys as appsettings.json).</param>
    /// <param name="configureBuilder">Runs after the host is composed. Use it to add or replace services.</param>
    /// <param name="configureApp">Runs after the pipeline is built. Use it to map test-only endpoints.</param>
    public static async Task<TestApplication> StartAsync(
        string environment = "Testing",
        IDictionary<string, string?>? configuration = null,
        Action<WebApplicationBuilder>? configureBuilder = null,
        Action<WebApplication>? configureApp = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = environment,
            ApplicationName = typeof(ApiHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory,
        });

        // Port 0 = the operating system picks a free port, so tests can run in parallel.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Keep test output readable. Warnings and errors still show.
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Microsoft"] = "Warning",
            ["Logging:LogLevel:Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware"] = "Warning",
        });

        if (configuration is not null)
        {
            builder.Configuration.AddInMemoryCollection(configuration);
        }

        builder.AddApiHost();
        configureBuilder?.Invoke(builder);

        var app = builder.Build();
        app.UseApiHost();
        configureApp?.Invoke(app);

        await app.StartAsync();
        return new TestApplication(app);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
