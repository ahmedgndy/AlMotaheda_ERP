using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Data;

/// <summary>The company price list (price management), the cost alerts, supply agreements and cash custody.</summary>
internal sealed partial class ProjectsDbContext
{
    public DbSet<PriceItem> PriceItems => Set<PriceItem>();
    public DbSet<PriceRange> PriceRanges => Set<PriceRange>();
    public DbSet<PriceCategory> PriceCategories => Set<PriceCategory>();
    public DbSet<PriceUnit> PriceUnits => Set<PriceUnit>();
    public DbSet<PriceException> PriceExceptions => Set<PriceException>();
    public DbSet<PriceExceptionAlert> PriceExceptionAlerts => Set<PriceExceptionAlert>();
    public DbSet<CostAlert> CostAlerts => Set<CostAlert>();
    public DbSet<SupplyAgreement> SupplyAgreements => Set<SupplyAgreement>();
    public DbSet<CustodyEntry> CustodyEntries => Set<CustodyEntry>();
}
