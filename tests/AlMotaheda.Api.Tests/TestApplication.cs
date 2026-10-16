using AlMotaheda.Api.Infrastructure.HealthChecks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

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
    /// <summary>The token signing key every test host uses (48 random bytes, base64). Tests sign their own tokens with it.</summary>
    public const string TestSigningKey = "5VCqt/dpsqpPVWrnXWbyFoaRj4yGrULMc4k6cP4DmmES84K65SQnMLaRgLsx4Nzn";

    /// <summary>
    /// The connection string used when a test doesn't supply a real database. Nothing listens there: tests that
    /// use it must not reach the database. The database health check is removed for these tests.
    /// </summary>
    public const string NoDatabaseConnectionString =
        "Server=127.0.0.1,9;Database=NoDatabase;User Id=test;Password=test;Connect Timeout=1;TrustServerCertificate=true";

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

        // .NET checks service lifetimes only in Development by default. Check them in every test,
        // so a lifetime bug (for example, a singleton holding a scoped database context) fails here.
        builder.Host.UseDefaultServiceProvider(options =>
        {
            options.ValidateScopes = true;
            options.ValidateOnBuild = true;
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Keep test output readable. Warnings and errors still show.
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Microsoft"] = "Warning",
            ["Logging:LogLevel:Microsoft.AspNetCore.HttpLogging.HttpLoggingMiddleware"] = "Warning",

            // Required settings, so every test host starts. Tests can override them.
            ["ConnectionStrings:Erp"] = NoDatabaseConnectionString,
            ["Access:Tokens:SigningKey"] = TestSigningKey,
            // The Archive module checks its storage folder at startup and requires one in Production.
            ["Archive:Storage:RootPath"] = Path.Combine(Path.GetTempPath(), "almotaheda-api-tests", "archive"),

            // A "Development" test host also reads the developer's user-secrets. Keep the developer's local settings
            // (migrate on start, first-Executive bootstrap) out of the tests; tests that need them set them again.
            ["Database:ApplyMigrationsOnStartup"] = "false",
            ["Access:Bootstrap:Email"] = null,
            ["Access:Bootstrap:Password"] = null,
        });

        if (configuration is not null)
        {
            builder.Configuration.AddInMemoryCollection(configuration);
        }

        var usesRealDatabase = builder.Configuration.GetConnectionString("Erp") != NoDatabaseConnectionString;
        if (!usesRealDatabase)
        {
            // Without a database the outbox dispatcher could only fail and retry in the background.
            builder.Configuration["Platform:Outbox:Enabled"] = "false";
        }

        builder.AddApiHost();

        if (!usesRealDatabase)
        {
            builder.Services.Configure<HealthCheckServiceOptions>(options =>
            {
                foreach (var registration in options.Registrations.Where(r => r.Name == DatabaseHealthCheck.Name).ToList())
                {
                    options.Registrations.Remove(registration);
                }
            });
        }
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
