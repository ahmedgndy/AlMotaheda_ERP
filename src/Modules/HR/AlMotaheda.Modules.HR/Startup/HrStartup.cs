using AlMotaheda.Modules.HR.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.HR.Startup;

/// <summary>
/// Applies pending HR migrations once before the API accepts requests, only when
/// <c>Database:ApplyMigrationsOnStartup</c> is true (meant for development; servers run the reviewed script).
/// </summary>
/// <remarks>The HR tables point to no other module's tables; every save writes the Platform audit tables, so Platform runs first.</remarks>
internal sealed class HrStartup(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<HrStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>(ModuleMigrations.ApplyMigrationsSetting))
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<HrDbContext>();
        await ModuleMigrations.ApplyAsync(db, "HR", [], "Platform", logger, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
