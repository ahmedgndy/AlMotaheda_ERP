using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Data;

/// <summary>
/// The Fleet module's tables, all in the <c>fleet</c> schema. No other module reads them: other modules ask
/// through contracts.
/// </summary>
internal sealed class FleetDbContext(DbContextOptions<FleetDbContext> options) : DbContext(options)
{
    public const string Schema = "fleet";

    /// <summary>The module keeps its own migration history, so each module's migrations stay independent.</summary>
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    /// <summary>The company's equipment register: one row per machine for its whole life.</summary>
    public DbSet<Equipment> Equipment => Set<Equipment>();

    public DbSet<EquipmentRental> EquipmentRentals => Set<EquipmentRental>();

    public DbSet<EquipmentAssignment> EquipmentAssignments => Set<EquipmentAssignment>();

    public DbSet<EquipmentCost> EquipmentCosts => Set<EquipmentCost>();
    public DbSet<EquipmentCostAdjustment> EquipmentCostAdjustments => Set<EquipmentCostAdjustment>();

    /// <summary>Every voided payment of an equipment cost: written at the void, never changed.</summary>
    public DbSet<VoidedEquipmentCostPayment> VoidedEquipmentCostPayments => Set<VoidedEquipmentCostPayment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FleetDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        StorageConventions.Apply(configurationBuilder);
}
