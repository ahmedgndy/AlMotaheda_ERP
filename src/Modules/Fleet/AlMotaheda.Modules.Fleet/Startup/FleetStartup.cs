using AlMotaheda.Modules.Fleet.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Fleet.Startup;

/// <summary>
/// Applies pending Fleet migrations once before the API accepts requests, only when
/// <c>Database:ApplyMigrationsOnStartup</c> is true (meant for development; servers run the reviewed script).
/// </summary>
/// <remarks>
/// The Fleet tables point to <c>projects.Project</c>, <c>projects.Supplier</c> and <c>access.User</c>, so the Access
/// and Projects migrations must run first. Hosted services
/// start one after another in registration order, and <c>ModuleCatalog</c> lists Projects before this module (a
/// host test checks that order). If the Projects tables are still missing, startup stops here with a clear
/// message instead of a foreign-key error halfway through the migration.
/// </remarks>
internal sealed class FleetStartup(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<FleetStartup> logger) : IHostedService
{
    public const string ApplyMigrationsSetting = "Database:ApplyMigrationsOnStartup";

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>(ApplyMigrationsSetting))
        {
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FleetDbContext>();

        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        if (!await ProjectTablesExistAsync(db, cancellationToken))
        {
            throw new InvalidOperationException(
                "The Fleet migrations need the projects.Project and projects.Supplier tables. Apply the Access and Projects "
                + "migrations first (the Projects module must be listed before Fleet in ModuleCatalog).");
        }

        await db.Database.MigrateAsync(cancellationToken);
        logger.LogInformation("Applied {Count} Fleet migration(s): {Migrations}", pending.Count, string.Join(", ", pending));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task<bool> ProjectTablesExistAsync(FleetDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            var found = await db.Database
                .SqlQuery<int>($"SELECT CASE WHEN OBJECT_ID(N'[projects].[Project]', N'U') IS NULL OR OBJECT_ID(N'[projects].[Supplier]', N'U') IS NULL THEN 0 ELSE 1 END AS [Value]")
                .ToListAsync(cancellationToken);

            return found.Count == 1 && found[0] == 1;
        }
        catch (SqlException)
        {
            // The database itself doesn't exist yet, so neither do the projects tables.
            return false;
        }
    }
}
