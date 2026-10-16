using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AlMotaheda.Modules.HR.Data;

/// <summary>SQL Server settings shared by the running application and the <c>dotnet ef</c> tools.</summary>
internal static class HrDatabase
{
    public static void Configure(SqlServerDbContextOptionsBuilder sql) =>
        sql.MigrationsHistoryTable(HrDbContext.MigrationsHistoryTable, HrDbContext.Schema);
}

/// <summary>
/// Lets <c>dotnet ef</c> build the model without starting the API. Creating migrations and SQL scripts needs
/// no database; the connection string here is only used by <c>dotnet ef database update</c>.
/// </summary>
/// <example>
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project src/Modules/HR/AlMotaheda.Modules.HR --output-dir Data/Migrations
/// dotnet ef migrations script --idempotent --project src/Modules/HR/AlMotaheda.Modules.HR -o hr.sql
/// </code>
/// </example>
internal sealed class HrDbContextDesignTimeFactory : IDesignTimeDbContextFactory<HrDbContext>
{
    private const string LocalDevelopmentDatabase =
        "Server=localhost,1433;Database=AlMotahedaErp;Integrated Security=false;TrustServerCertificate=true";

    public HrDbContext CreateDbContext(string[] args)
    {
        var connectionString = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : LocalDevelopmentDatabase;

        var options = new DbContextOptionsBuilder<HrDbContext>()
            .UseSqlServer(connectionString, HrDatabase.Configure)
            .Options;

        return new HrDbContext(options);
    }
}

/// <summary>The application-lock names of this module (see <see cref="SqlAppLock"/>).</summary>
internal static class AppLock
{
    /// <summary>Everything that changes what a month pays: its sheet, its payroll inputs, the salary structures.</summary>
    public const string Payroll = "AlMotaheda.HR.Payroll";

    /// <summary>Takes the lock called <paramref name="resource"/>. Must run inside a transaction. False on timeout.</summary>
    public static Task<bool> TryAcquireAsync(HrDbContext db, string resource, CancellationToken cancellationToken) =>
        SqlAppLock.TryAcquireAsync(db, resource, cancellationToken);
}
