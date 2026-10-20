using AlMotaheda.Modules.Office.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Office.Startup;

/// <summary>
/// Applies pending Office migrations once before the API accepts requests, only when
/// <c>Database:ApplyMigrationsOnStartup</c> is true (meant for development; servers run the reviewed script).
/// </summary>
/// <remarks>The office tables point to no other module's tables; every save writes the Platform audit tables, so Platform runs first.</remarks>
internal sealed class OfficeStartup(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<OfficeStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>(ModuleMigrations.ApplyMigrationsSetting))
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OfficeDbContext>();
        await ModuleMigrations.ApplyAsync(db, "Office", [], "Platform", logger, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
