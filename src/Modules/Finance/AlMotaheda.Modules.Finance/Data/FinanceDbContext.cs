using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Finance.Data;

/// <summary>The Finance module's tables, all in the <c>finance</c> schema. No other module reads them.</summary>
internal sealed class FinanceDbContext(DbContextOptions<FinanceDbContext> options) : DbContext(options)
{
    public const string Schema = "finance";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    public DbSet<PaymentRequest> PaymentRequests => Set<PaymentRequest>();
    public DbSet<PaymentRequestLine> PaymentRequestLines => Set<PaymentRequestLine>();
    public DbSet<PaymentRequestEvent> PaymentRequestEvents => Set<PaymentRequestEvent>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FinanceDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        StorageConventions.Apply(configurationBuilder);
}
