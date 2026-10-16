using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AlMotaheda.Modules.Reports.Data;

/// <summary>SQL Server settings shared by the running application and the <c>dotnet ef</c> tools.</summary>
internal static class ReportsDatabase
{
    public static void Configure(SqlServerDbContextOptionsBuilder sql) =>
        sql.MigrationsHistoryTable(ReportsDbContext.MigrationsHistoryTable, ReportsDbContext.Schema);
}

/// <summary>
/// Lets <c>dotnet ef</c> build the model without starting the API. Creating migrations and SQL scripts needs
/// no database; the connection string here is only used by <c>dotnet ef database update</c>.
/// </summary>
/// <example>
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project src/Modules/Reports/AlMotaheda.Modules.Reports --output-dir Data/Migrations
/// dotnet ef migrations script --idempotent --project src/Modules/Reports/AlMotaheda.Modules.Reports -o rpt.sql
/// </code>
/// </example>
internal sealed class ReportsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ReportsDbContext>
{
    private const string LocalDevelopmentDatabase =
        "Server=localhost,1433;Database=AlMotahedaErp;Integrated Security=false;TrustServerCertificate=true";

    public ReportsDbContext CreateDbContext(string[] args)
    {
        var connectionString = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : LocalDevelopmentDatabase;

        var options = new DbContextOptionsBuilder<ReportsDbContext>()
            .UseSqlServer(connectionString, ReportsDatabase.Configure)
            .Options;

        return new ReportsDbContext(options);
    }
}

