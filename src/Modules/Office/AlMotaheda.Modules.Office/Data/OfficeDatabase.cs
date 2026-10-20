using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AlMotaheda.Modules.Office.Data;

/// <summary>SQL Server settings shared by the running application and the <c>dotnet ef</c> tools.</summary>
internal static class OfficeDatabase
{
    public static void Configure(SqlServerDbContextOptionsBuilder sql) =>
        sql.MigrationsHistoryTable(OfficeDbContext.MigrationsHistoryTable, OfficeDbContext.Schema);
}

/// <summary>
/// Lets <c>dotnet ef</c> build the model without starting the API. Creating migrations and SQL scripts needs
/// no database; the connection string here is only used by <c>dotnet ef database update</c>.
/// </summary>
/// <example>
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project src/Modules/Office/AlMotaheda.Modules.Office --output-dir Data/Migrations
/// dotnet ef migrations script --idempotent --project src/Modules/Office/AlMotaheda.Modules.Office -o office.sql
/// </code>
/// </example>
internal sealed class OfficeDbContextDesignTimeFactory : IDesignTimeDbContextFactory<OfficeDbContext>
{
    private const string LocalDevelopmentDatabase =
        "Server=localhost,1433;Database=AlMotahedaErp;Integrated Security=false;TrustServerCertificate=true";

    public OfficeDbContext CreateDbContext(string[] args)
    {
        var connectionString = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : LocalDevelopmentDatabase;

        var options = new DbContextOptionsBuilder<OfficeDbContext>()
            .UseSqlServer(connectionString, OfficeDatabase.Configure)
            .Options;

        return new OfficeDbContext(options);
    }
}

/// <summary>The application-lock names of this module (see <see cref="SqlAppLock"/>).</summary>
internal static class AppLock
{
    /// <summary>
    /// Everything that changes or depends on one custody's balance: ledger postings, approvals that post, counts,
    /// returns, receipts of funds, closing. One lock per custody, so two custodies never wait for each other.
    /// </summary>
    public static string Custody(int custodyId) => $"AlMotaheda.Office.Custody.{custodyId}";

    /// <summary>Takes the lock called <paramref name="resource"/>. Must run inside a transaction. False on timeout.</summary>
    public static Task<bool> TryAcquireAsync(OfficeDbContext db, string resource, CancellationToken cancellationToken) =>
        SqlAppLock.TryAcquireAsync(db, resource, cancellationToken);
}
