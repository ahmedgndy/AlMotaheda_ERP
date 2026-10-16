using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AlMotaheda.Modules.Procurement.Data;

/// <summary>SQL Server settings shared by the running application and the <c>dotnet ef</c> tools.</summary>
internal static class ProcurementDatabase
{
    public static void Configure(SqlServerDbContextOptionsBuilder sql) =>
        sql.MigrationsHistoryTable(ProcurementDbContext.MigrationsHistoryTable, ProcurementDbContext.Schema);
}

/// <summary>
/// Lets <c>dotnet ef</c> build the model without starting the API. Creating migrations and SQL scripts needs
/// no database; the connection string here is only used by <c>dotnet ef database update</c>.
/// </summary>
/// <example>
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project src/Modules/Procurement/AlMotaheda.Modules.Procurement --output-dir Data/Migrations
/// dotnet ef migrations script --idempotent --project src/Modules/Procurement/AlMotaheda.Modules.Procurement -o proc.sql
/// </code>
/// </example>
internal sealed class ProcurementDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ProcurementDbContext>
{
    private const string LocalDevelopmentDatabase =
        "Server=localhost,1433;Database=AlMotahedaErp;Integrated Security=false;TrustServerCertificate=true";

    public ProcurementDbContext CreateDbContext(string[] args)
    {
        var connectionString = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : LocalDevelopmentDatabase;

        var options = new DbContextOptionsBuilder<ProcurementDbContext>()
            .UseSqlServer(connectionString, ProcurementDatabase.Configure)
            .Options;

        return new ProcurementDbContext(options);
    }
}

/// <summary>The application-lock names of this module (see <see cref="SqlAppLock"/>).</summary>
internal static class AppLock
{
    /// <summary>
    /// The procurement documents of one project: quantities move along PR → PO → GRN → invoice, so every change that
    /// checks "what is left" takes this lock first.
    /// </summary>
    public static string Project(int projectId) => $"AlMotaheda.Procurement.Project.{projectId}";

    /// <summary>Takes the lock called <paramref name="resource"/>. Must run inside a transaction. False on timeout.</summary>
    public static Task<bool> TryAcquireAsync(ProcurementDbContext db, string resource, CancellationToken cancellationToken) =>
        SqlAppLock.TryAcquireAsync(db, resource, cancellationToken);
}
