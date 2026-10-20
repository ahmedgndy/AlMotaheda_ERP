using AlMotaheda.Modules.Fleet.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Fleet.Startup;

/// <summary>
/// Applies pending Fleet migrations once before the API accepts requests, only when
/// <c>Database:ApplyMigrationsOnStartup</c> is true (meant for development; servers run the reviewed script).
/// </summary>
/// <remarks>The Fleet tables point to <c>projects.Project</c>, <c>projects.ProjectItem</c>, <c>projects.Supplier</c> and <c>access.User</c>, so the Access and Projects migrations run first. See <see cref="ModuleMigrations"/>.</remarks>
internal sealed class FleetStartup(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<FleetStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>(ModuleMigrations.ApplyMigrationsSetting))
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();
        await ModuleMigrations.ApplyAsync(db, "Fleet", ["projects.Project", "projects.ProjectItem", "projects.Supplier"], "Access and Projects", logger, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
