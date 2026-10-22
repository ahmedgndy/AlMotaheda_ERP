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

    // The treasury and the custody hierarchy (docs/project-workspace/12-treasury-custody.md).
    public DbSet<TreasuryAccount> TreasuryAccounts => Set<TreasuryAccount>();
    public DbSet<TreasuryDeposit> TreasuryDeposits => Set<TreasuryDeposit>();
    public DbSet<CustodyAccount> CustodyAccounts => Set<CustodyAccount>();
    public DbSet<CustodyMovement> CustodyMovements => Set<CustodyMovement>();
    public DbSet<CustodyTransfer> CustodyTransfers => Set<CustodyTransfer>();
    public DbSet<CustodyTransferEvent> CustodyTransferEvents => Set<CustodyTransferEvent>();
    public DbSet<CustodyTransferAlert> CustodyTransferAlerts => Set<CustodyTransferAlert>();
    public DbSet<PayrollBatch> PayrollBatches => Set<PayrollBatch>();
    public DbSet<PayrollBatchAllocation> PayrollBatchAllocations => Set<PayrollBatchAllocation>();
    public DbSet<CustodyCashCount> CustodyCashCounts => Set<CustodyCashCount>();
    public DbSet<CustodyAdjustment> CustodyAdjustments => Set<CustodyAdjustment>();
    public DbSet<CustodyPeriodClose> CustodyPeriodCloses => Set<CustodyPeriodClose>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FinanceDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        StorageConventions.Apply(configurationBuilder);
}
