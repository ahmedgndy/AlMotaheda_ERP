using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Data;

/// <summary>
/// The Workforce module's tables, all in the <c>workforce</c> schema. No other module reads them: other modules ask
/// through contracts.
/// </summary>
internal sealed class WorkforceDbContext(DbContextOptions<WorkforceDbContext> options) : DbContext(options)
{
    public const string Schema = "workforce";

    /// <summary>The module keeps its own migration history, so each module's migrations stay independent.</summary>
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<Worker> Workers => Set<Worker>();
    public DbSet<WorkerAssignment> WorkerAssignments => Set<WorkerAssignment>();
    public DbSet<DailySheet> DailySheets => Set<DailySheet>();
    public DbSet<AttendanceLine> AttendanceLines => Set<AttendanceLine>();
    public DbSet<LineAdjustment> LineAdjustments => Set<LineAdjustment>();
    public DbSet<LaborAllocation> LaborAllocations => Set<LaborAllocation>();
    public DbSet<WorkerPayment> WorkerPayments => Set<WorkerPayment>();
    public DbSet<WorkerPaymentAllocation> WorkerPaymentAllocations => Set<WorkerPaymentAllocation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WorkforceDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        StorageConventions.Apply(configurationBuilder);
}
