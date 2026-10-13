using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace AlMotaheda.Modules.Projects.Data;

/// <summary>
/// The Projects module's tables, all in the <c>projects</c> schema. No other module reads them: other modules ask
/// through contracts.
/// </summary>
internal sealed partial class ProjectsDbContext(DbContextOptions<ProjectsDbContext> options) : DbContext(options)
{
    public const string Schema = "projects";

    /// <summary>The module keeps its own migration history, so each module's migrations stay independent.</summary>
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMember> ProjectMembers => Set<ProjectMember>();

    // Shared by the workspace features. Each feature area adds its own tables in a partial file next to this one
    // (ProjectsDbContext.<Area>.cs), so areas don't edit the same lines.
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<ProjectItem> ProjectItems => Set<ProjectItem>();
    public DbSet<CatalogItem> CatalogItems => Set<CatalogItem>();
    public DbSet<ItemPrice> ItemPrices => Set<ItemPrice>();
    public DbSet<ProjectExpense> ProjectExpenses => Set<ProjectExpense>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ProjectsDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Moments are stored as UTC DATETIME2(3) and read back marked as UTC, like the Access module.
        // Business dates (start, end) are DateOnly and stored as DATE.
        configurationBuilder.Properties<DateTime>()
            .HaveColumnType("datetime2(3)")
            .HaveConversion<UtcDateTimeConverter>();

        // Codes and enum names are ASCII. Free text (names, addresses, notes) is configured per column as Unicode.
        configurationBuilder.Properties<string>().AreUnicode(false);

        // Money is DECIMAL(18,2) unless a column says otherwise (quantities are configured as (18,3)).
        configurationBuilder.Properties<decimal>().HavePrecision(18, 2);
    }

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
        value => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value,
        value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
