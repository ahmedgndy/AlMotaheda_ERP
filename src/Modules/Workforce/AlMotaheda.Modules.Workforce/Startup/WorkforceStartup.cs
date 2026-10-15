using AlMotaheda.Modules.Workforce.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Workforce.Startup;

/// <summary>
/// Applies pending Workforce migrations once before the API accepts requests, only when
/// <c>Database:ApplyMigrationsOnStartup</c> is true (meant for development; servers run the reviewed script).
/// </summary>
/// <remarks>The Workforce tables point to <c>projects.Project</c>, so the Access and Projects migrations run first. See <see cref="ModuleMigrations"/>.</remarks>
internal sealed class WorkforceStartup(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<WorkforceStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>(ModuleMigrations.ApplyMigrationsSetting))
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkforceDbContext>();
        await ModuleMigrations.ApplyAsync(db, "Workforce", ["projects.Project"], "Access and Projects", logger, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
