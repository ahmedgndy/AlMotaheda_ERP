using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AlMotaheda.Modules.Finance.Data;

/// <summary>SQL Server settings shared by the running application and the <c>dotnet ef</c> tools.</summary>
internal static class FinanceDatabase
{
    public static void Configure(SqlServerDbContextOptionsBuilder sql) =>
        sql.MigrationsHistoryTable(FinanceDbContext.MigrationsHistoryTable, FinanceDbContext.Schema);
}

/// <summary>
/// Lets <c>dotnet ef</c> build the model without starting the API. Creating migrations and SQL scripts needs
/// no database; the connection string here is only used by <c>dotnet ef database update</c>.
/// </summary>
/// <remarks>
/// The Finance migrations copy the payments of the other modules into the ledger once, so they run last.
/// </remarks>
/// <example>
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project src/Modules/Finance/AlMotaheda.Modules.Finance --output-dir Data/Migrations
/// dotnet ef migrations script --idempotent --project src/Modules/Finance/AlMotaheda.Modules.Finance -o finance.sql
/// </code>
/// </example>
internal sealed class FinanceDbContextDesignTimeFactory : IDesignTimeDbContextFactory<FinanceDbContext>
{
    private const string LocalDevelopmentDatabase =
        "Server=localhost,1433;Database=AlMotahedaErp;Integrated Security=false;TrustServerCertificate=true";

    public FinanceDbContext CreateDbContext(string[] args)
    {
        var connectionString = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : LocalDevelopmentDatabase;

        var options = new DbContextOptionsBuilder<FinanceDbContext>()
            .UseSqlServer(connectionString, FinanceDatabase.Configure)
            .Options;

        return new FinanceDbContext(options);
    }
}
