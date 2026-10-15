using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Archive.Data;

/// <summary>
/// The Archive module's tables, all in the <c>docs</c> schema. No other module reads them: other modules ask
/// through contracts.
/// </summary>
internal sealed class ArchiveDbContext(DbContextOptions<ArchiveDbContext> options) : DbContext(options)
{
    public const string Schema = "docs";

    /// <summary>The module keeps its own migration history, so each module's migrations stay independent.</summary>
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<DocumentLink> DocumentLinks => Set<DocumentLink>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ArchiveDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        StorageConventions.Apply(configurationBuilder);
}
