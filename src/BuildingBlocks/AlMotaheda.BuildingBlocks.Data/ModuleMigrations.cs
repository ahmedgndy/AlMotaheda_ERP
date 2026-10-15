using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AlMotaheda.BuildingBlocks.Data;

/// <summary>
/// Applies a module's pending migrations at startup, only when <c>Database:ApplyMigrationsOnStartup</c> is true
/// (meant for development; servers run the reviewed script).
/// </summary>
/// <remarks>
/// A module's tables point to tables of the modules it depends on (for example <c>projects.Project</c>), so those
/// migrations must run first. Hosted services start one after another in registration order, and
/// <c>ModuleCatalog</c> lists the modules in dependency order (a host test checks it). If a required table is still
/// missing, startup stops with a clear message instead of a foreign-key error halfway through the migration.
/// </remarks>
public static class ModuleMigrations
{
    public const string ApplyMigrationsSetting = "Database:ApplyMigrationsOnStartup";

    /// <param name="module">The module name, for the log and the error message.</param>
    /// <param name="requiredTables">Tables of other modules this module's migrations point to, as <c>schema.Table</c>.</param>
    /// <param name="requiredMigrations">Which migrations to apply first, in words, for the error message.</param>
    public static async Task ApplyAsync(
        DbContext db,
        string module,
        IReadOnlyList<string> requiredTables,
        string requiredMigrations,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        foreach (var table in requiredTables)
        {
            if (!await TableExistsAsync(db, table, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"The {module} migrations need the {string.Join(" and ", requiredTables)} table(s). Apply the "
                    + $"{requiredMigrations} migrations first (those modules must be listed before {module} in ModuleCatalog).");
            }
        }

        await db.Database.MigrateAsync(cancellationToken);
        logger.LogInformation("Applied {Count} {Module} migration(s): {Migrations}", pending.Count, module, string.Join(", ", pending));
    }

    private static async Task<bool> TableExistsAsync(DbContext db, string table, CancellationToken cancellationToken)
    {
        var parts = table.Split('.', 2);
        var name = $"[{parts[0]}].[{parts[1]}]";

        try
        {
            var found = await db.Database
                .SqlQuery<int>($"SELECT CASE WHEN OBJECT_ID({name}, N'U') IS NULL THEN 0 ELSE 1 END AS [Value]")
                .ToListAsync(cancellationToken);

            return found.Count == 1 && found[0] == 1;
        }
        catch (SqlException)
        {
            // The database itself doesn't exist yet, so neither does the table.
            return false;
        }
    }
}
