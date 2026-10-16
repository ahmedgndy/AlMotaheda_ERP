using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AlMotaheda.Modules.Workforce.Data;

/// <summary>SQL Server settings shared by the running application and the <c>dotnet ef</c> tools.</summary>
internal static class WorkforceDatabase
{
    public static void Configure(SqlServerDbContextOptionsBuilder sql) =>
        sql.MigrationsHistoryTable(WorkforceDbContext.MigrationsHistoryTable, WorkforceDbContext.Schema);
}

/// <summary>
/// Lets <c>dotnet ef</c> build the model without starting the API. Creating migrations and SQL scripts needs
/// no database; the connection string here is only used by <c>dotnet ef database update</c>.
/// </summary>
/// <remarks>
/// The Workforce tables point to <c>projects</c> and <c>access</c> tables, so those migrations must be applied first.
/// </remarks>
/// <example>
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project src/Modules/Workforce/AlMotaheda.Modules.Workforce --output-dir Data/Migrations
/// dotnet ef migrations script --idempotent --project src/Modules/Workforce/AlMotaheda.Modules.Workforce -o workforce.sql
/// </code>
/// </example>
internal sealed class WorkforceDbContextDesignTimeFactory : IDesignTimeDbContextFactory<WorkforceDbContext>
{
    private const string LocalDevelopmentDatabase =
        "Server=localhost,1433;Database=AlMotahedaErp;Integrated Security=false;TrustServerCertificate=true";

    public WorkforceDbContext CreateDbContext(string[] args)
    {
        var connectionString = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : LocalDevelopmentDatabase;

        var options = new DbContextOptionsBuilder<WorkforceDbContext>()
            .UseSqlServer(connectionString, WorkforceDatabase.Configure)
            .Options;

        return new WorkforceDbContext(options);
    }
}
