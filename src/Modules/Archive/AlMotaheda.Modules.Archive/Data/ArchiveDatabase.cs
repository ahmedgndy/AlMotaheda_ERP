using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AlMotaheda.Modules.Archive.Data;

/// <summary>SQL Server settings shared by the running application and the <c>dotnet ef</c> tools.</summary>
internal static class ArchiveDatabase
{
    public static void Configure(SqlServerDbContextOptionsBuilder sql) =>
        sql.MigrationsHistoryTable(ArchiveDbContext.MigrationsHistoryTable, ArchiveDbContext.Schema);
}

/// <summary>
/// Lets <c>dotnet ef</c> build the model without starting the API. Creating migrations and SQL scripts needs
/// no database; the connection string here is only used by <c>dotnet ef database update</c>.
/// </summary>
/// <remarks>
/// The Archive tables point to <c>projects</c> and <c>access</c> tables, so those migrations must be applied first.
/// </remarks>
/// <example>
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project src/Modules/Archive/AlMotaheda.Modules.Archive --output-dir Data/Migrations
/// dotnet ef migrations script --idempotent --project src/Modules/Archive/AlMotaheda.Modules.Archive -o docs.sql
/// </code>
/// </example>
internal sealed class ArchiveDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ArchiveDbContext>
{
    private const string LocalDevelopmentDatabase =
        "Server=localhost,1433;Database=AlMotahedaErp;Integrated Security=false;TrustServerCertificate=true";

    public ArchiveDbContext CreateDbContext(string[] args)
    {
        var connectionString = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : LocalDevelopmentDatabase;

        var options = new DbContextOptionsBuilder<ArchiveDbContext>()
            .UseSqlServer(connectionString, ArchiveDatabase.Configure)
            .Options;

        return new ArchiveDbContext(options);
    }
}
