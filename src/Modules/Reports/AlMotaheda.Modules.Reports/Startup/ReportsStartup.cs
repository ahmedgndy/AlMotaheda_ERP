using AlMotaheda.Modules.Reports.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Reports.Startup;

/// <summary>
/// Applies pending Reports migrations once before the API accepts requests, only when
/// <c>Database:ApplyMigrationsOnStartup</c> is true (meant for development; servers run the reviewed script).
/// </summary>
/// <remarks>The Reports table points to no other module's tables; every save writes the Platform audit tables, so Platform runs first.</remarks>
internal sealed class ReportsStartup(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ReportsStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>(ModuleMigrations.ApplyMigrationsSetting))
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ReportsDbContext>();
        await ModuleMigrations.ApplyAsync(db, "Reports", [], "Platform", logger, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
