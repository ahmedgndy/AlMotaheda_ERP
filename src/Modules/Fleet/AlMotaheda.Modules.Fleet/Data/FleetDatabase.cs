using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AlMotaheda.Modules.Fleet.Data;

/// <summary>SQL Server settings shared by the running application and the <c>dotnet ef</c> tools.</summary>
internal static class FleetDatabase
{
    /// <summary>The connection string name in configuration: <c>ConnectionStrings:Erp</c>. One database, one schema per module.</summary>
    public const string ConnectionStringName = "Erp";

    public static void Configure(SqlServerDbContextOptionsBuilder sql) =>
        sql.MigrationsHistoryTable(FleetDbContext.MigrationsHistoryTable, FleetDbContext.Schema);
}

/// <summary>
/// Lets <c>dotnet ef</c> build the model without starting the API. Creating migrations and SQL scripts needs
/// no database; the connection string here is only used by <c>dotnet ef database update</c>.
/// </summary>
/// <remarks>
/// The Fleet tables point to <c>projects</c> and <c>access</c> tables, so those migrations must be applied first.
/// </remarks>
/// <example>
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project src/Modules/Fleet/AlMotaheda.Modules.Fleet --output-dir Data/Migrations
/// dotnet ef migrations script --idempotent --project src/Modules/Fleet/AlMotaheda.Modules.Fleet -o fleet.sql
/// </code>
/// </example>
internal sealed class FleetDbContextDesignTimeFactory : IDesignTimeDbContextFactory<FleetDbContext>
{
    private const string LocalDevelopmentDatabase =
        "Server=localhost,1433;Database=AlMotahedaErp;Integrated Security=false;TrustServerCertificate=true";

    public FleetDbContext CreateDbContext(string[] args)
    {
        var connectionString = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : LocalDevelopmentDatabase;

        var options = new DbContextOptionsBuilder<FleetDbContext>()
            .UseSqlServer(connectionString, FleetDatabase.Configure)
            .Options;

        return new FleetDbContext(options);
    }
}
