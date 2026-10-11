using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AlMotaheda.Modules.Access.Data;

/// <summary>SQL Server settings shared by the running application and the <c>dotnet ef</c> tools.</summary>
internal static class AccessDatabase
{
    /// <summary>The connection string name in configuration: <c>ConnectionStrings:Erp</c>. One database, one schema per module.</summary>
    public const string ConnectionStringName = "Erp";

    public static void Configure(SqlServerDbContextOptionsBuilder sql) =>
        sql.MigrationsHistoryTable(AccessDbContext.MigrationsHistoryTable, AccessDbContext.Schema);
}

/// <summary>
/// Lets <c>dotnet ef</c> build the model without starting the API. Creating migrations and SQL scripts needs
/// no database; the connection string here is only used by <c>dotnet ef database update</c>.
/// </summary>
/// <example>
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project src/Modules/Access/AlMotaheda.Modules.Access --output-dir Data/Migrations
/// dotnet ef migrations script --idempotent --project src/Modules/Access/AlMotaheda.Modules.Access -o access.sql
/// dotnet ef database update --project src/Modules/Access/AlMotaheda.Modules.Access --connection "&lt;connection string&gt;"
/// </code>
/// </example>
internal sealed class AccessDbContextDesignTimeFactory : IDesignTimeDbContextFactory<AccessDbContext>
{
    private const string LocalDevelopmentDatabase =
        "Server=localhost,1433;Database=AlMotahedaErp;Integrated Security=false;TrustServerCertificate=true";

    public AccessDbContext CreateDbContext(string[] args)
    {
        var connectionString = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : LocalDevelopmentDatabase;

        var options = new DbContextOptionsBuilder<AccessDbContext>()
            .UseSqlServer(connectionString, AccessDatabase.Configure)
            .Options;

        return new AccessDbContext(options);
    }
}
