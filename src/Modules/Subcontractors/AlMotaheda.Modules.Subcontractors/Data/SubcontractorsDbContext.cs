using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Data;

/// <summary>
/// The Subcontractors module's tables, all in the <c>sub</c> schema. No other module reads them: other modules ask
/// through contracts.
/// </summary>
internal sealed class SubcontractorsDbContext(DbContextOptions<SubcontractorsDbContext> options) : DbContext(options)
{
    public const string Schema = "sub";

    /// <summary>The module keeps its own migration history, so each module's migrations stay independent.</summary>
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    public DbSet<Subcontractor> Subcontractors => Set<Subcontractor>();
    public DbSet<SubContract> SubContracts => Set<SubContract>();
    public DbSet<ContractItem> ContractItems => Set<ContractItem>();
    public DbSet<ProgressMeasurement> ProgressMeasurements => Set<ProgressMeasurement>();
    public DbSet<ManpowerSheet> ManpowerSheets => Set<ManpowerSheet>();
    public DbSet<SubPayment> SubPayments => Set<SubPayment>();
    public DbSet<SubPaymentAllocation> SubPaymentAllocations => Set<SubPaymentAllocation>();
    public DbSet<ContractVariation> ContractVariations => Set<ContractVariation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(SubcontractorsDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        StorageConventions.Apply(configurationBuilder);
}
