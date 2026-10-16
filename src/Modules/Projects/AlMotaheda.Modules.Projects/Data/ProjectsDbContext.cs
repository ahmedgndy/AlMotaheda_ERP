using Microsoft.EntityFrameworkCore;

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
    public DbSet<ExpenseAdjustment> ExpenseAdjustments => Set<ExpenseAdjustment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ProjectsDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        StorageConventions.Apply(configurationBuilder);
}
