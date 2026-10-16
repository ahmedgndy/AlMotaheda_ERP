using AlMotaheda.Modules.Platform.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Platform.Startup;

/// <summary>
/// Applies pending Platform migrations once before the API accepts requests, only when
/// <c>Database:ApplyMigrationsOnStartup</c> is true (meant for development; servers run the reviewed script).
/// </summary>
/// <remarks>
/// Platform is first in ModuleCatalog: every module writes audit entries and outbox messages on every save,
/// including the Access module's first-Executive bootstrap, so these tables must exist before anything else runs.
/// The Platform tables point to no other module. See <see cref="ModuleMigrations"/>.
/// </remarks>
internal sealed class PlatformStartup(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<PlatformStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>(ModuleMigrations.ApplyMigrationsSetting))
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        await ModuleMigrations.ApplyAsync(db, "Platform", [], "", logger, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
