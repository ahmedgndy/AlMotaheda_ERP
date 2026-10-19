using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Data;

/// <summary>The company price list (price management) and the cost alerts.</summary>
internal sealed partial class ProjectsDbContext
{
    public DbSet<PriceItem> PriceItems => Set<PriceItem>();
    public DbSet<PriceRange> PriceRanges => Set<PriceRange>();
    public DbSet<CostAlert> CostAlerts => Set<CostAlert>();
}
