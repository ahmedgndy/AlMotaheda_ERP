using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AlMotaheda.Modules.Projects.Data;

/// <summary>SQL Server settings shared by the running application and the <c>dotnet ef</c> tools.</summary>
internal static class ProjectsDatabase
{
    public static void Configure(SqlServerDbContextOptionsBuilder sql) =>
        sql.MigrationsHistoryTable(ProjectsDbContext.MigrationsHistoryTable, ProjectsDbContext.Schema);
}

/// <summary>
/// Lets <c>dotnet ef</c> build the model without starting the API. Creating migrations and SQL scripts needs
/// no database; the connection string here is only used by <c>dotnet ef database update</c>.
/// </summary>
/// <remarks>
/// The Projects tables point to <c>access.User</c>, so the Access migrations must be applied first.
/// </remarks>
/// <example>
/// <code>
/// dotnet ef migrations add &lt;Name&gt; --project src/Modules/Projects/AlMotaheda.Modules.Projects --output-dir Data/Migrations
/// dotnet ef migrations script --idempotent --project src/Modules/Projects/AlMotaheda.Modules.Projects -o projects.sql
/// dotnet ef database update --project src/Modules/Projects/AlMotaheda.Modules.Projects --connection "&lt;connection string&gt;"
/// </code>
/// </example>
internal sealed class ProjectsDbContextDesignTimeFactory : IDesignTimeDbContextFactory<ProjectsDbContext>
{
    private const string LocalDevelopmentDatabase =
        "Server=localhost,1433;Database=AlMotahedaErp;Integrated Security=false;TrustServerCertificate=true";

    public ProjectsDbContext CreateDbContext(string[] args)
    {
        var connectionString = args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]) ? args[0] : LocalDevelopmentDatabase;

        var options = new DbContextOptionsBuilder<ProjectsDbContext>()
            .UseSqlServer(connectionString, ProjectsDatabase.Configure)
            .Options;

        return new ProjectsDbContext(options);
    }
}
