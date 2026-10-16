using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Platform.Data;

/// <summary>
/// The Platform module's tables, in the <c>platform</c> schema. Other modules insert audit entries and outbox
/// messages through <c>AuditTrailInterceptor</c>; only this module reads them.
/// </summary>
internal sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public const string Schema = PlatformTables.Schema;
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<Notification> Notifications => Set<Notification>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PlatformDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        StorageConventions.Apply(configurationBuilder);
}
