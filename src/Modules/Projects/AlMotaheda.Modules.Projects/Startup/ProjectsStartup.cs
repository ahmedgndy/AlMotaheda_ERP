using AlMotaheda.Modules.Projects.Data;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Projects.Startup;

/// <summary>
/// Applies pending Projects migrations once before the API accepts requests, only when
/// <c>Database:ApplyMigrationsOnStartup</c> is true (meant for development; servers run the reviewed script).
/// </summary>
/// <remarks>The Projects tables point to <c>access.User</c>, so the Access migrations run first. See <see cref="ModuleMigrations"/>.</remarks>
internal sealed class ProjectsStartup(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<ProjectsStartup> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>(ModuleMigrations.ApplyMigrationsSetting))
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ProjectsDbContext>();
        await ModuleMigrations.ApplyAsync(db, "Projects", ["access.User"], "Access", logger, cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
