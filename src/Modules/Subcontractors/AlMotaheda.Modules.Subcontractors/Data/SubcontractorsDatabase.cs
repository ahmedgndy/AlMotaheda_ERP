using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AlMotaheda.Modules.Subcontractors.Data;

/// <summary>SQL Server settings shared by the running application and the <c>dotnet ef</c> tools.</summary>
internal static class SubcontractorsDatabase
{
    public static void Configure(SqlServerDbContextOptionsBuilder sql) =>
        sql.MigrationsHistoryTable(SubcontractorsDbContext.MigrationsHistoryTable, SubcontractorsDbContext.Schema);
}

/// <summary>
/// Lets <c>dotnet ef</c> build the model without starting the API. Creating migrations and SQL scripts needs
/// no database; the connection string here is only used by <c>dotnet ef database update</c>.
/// </summary>
/// <remarks>
/// The Subcontractors tables point to <c>projects</c> and <c>access</c> tables, so those migrations must be applied first.
/// </remarks>
/// <example>
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project src/Modules/Subcontractors/AlMotaheda.Modules.Subcontractors --output-dir Data/Migrations
/// dotnet ef migrations script --idempotent --project src/Modules/Subcontractors/AlMotaheda.Modules.Subcontractors -o sub.sql
/// </code>
/// </example>
internal sealed class SubcontractorsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<SubcontractorsDbContext>
{
    private const string LocalDevelopmentDatabase =
        "Server=localhost,1433;Database=AlMotahedaErp;Integrated Security=false;TrustServerCertificate=true";

    public SubcontractorsDbContext CreateDbContext(string[] args)
    {
        var connectionString = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : LocalDevelopmentDatabase;

        var options = new DbContextOptionsBuilder<SubcontractorsDbContext>()
            .UseSqlServer(connectionString, SubcontractorsDatabase.Configure)
            .Options;

        return new SubcontractorsDbContext(options);
    }
}
