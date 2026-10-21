using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Office.Data;

/// <summary>
/// The Office module's tables, all in the <c>office</c> schema. No other module reads them: Finance asks through
/// <c>IPaymentSource</c>, Price Control through <c>IPriceExceptionSource</c>.
/// </summary>
internal sealed class OfficeDbContext(DbContextOptions<OfficeDbContext> options) : DbContext(options)
{
    public const string Schema = "office";

    /// <summary>The module keeps its own migration history, so each module's migrations stay independent.</summary>
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    public DbSet<OfficeCustody> Custodies => Set<OfficeCustody>();
    public DbSet<OfficeCustodyPeriod> Periods => Set<OfficeCustodyPeriod>();
    public DbSet<OfficeLedgerEntry> LedgerEntries => Set<OfficeLedgerEntry>();
    public DbSet<OfficeCashReturn> CashReturns => Set<OfficeCashReturn>();
    public DbSet<OfficeFundingRequest> FundingRequests => Set<OfficeFundingRequest>();
    public DbSet<OfficeExpense> Expenses => Set<OfficeExpense>();
    public DbSet<OfficeExpenseApproval> ExpenseApprovals => Set<OfficeExpenseApproval>();
    public DbSet<OfficeSpendRequest> SpendRequests => Set<OfficeSpendRequest>();
    public DbSet<OfficeAttachment> Attachments => Set<OfficeAttachment>();
    public DbSet<OfficeCashCount> CashCounts => Set<OfficeCashCount>();
    public DbSet<OfficeAlert> Alerts => Set<OfficeAlert>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OfficeDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        StorageConventions.Apply(configurationBuilder);
}
