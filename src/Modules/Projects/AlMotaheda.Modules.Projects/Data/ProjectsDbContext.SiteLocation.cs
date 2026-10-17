using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Data;

/// <summary>The site engineers' location sharing, their positions and the alerts they cause.</summary>
internal sealed partial class ProjectsDbContext
{
    public DbSet<LocationSharing> LocationSharings => Set<LocationSharing>();
    public DbSet<LocationPing> LocationPings => Set<LocationPing>();
    public DbSet<LocationAlert> LocationAlerts => Set<LocationAlert>();
}
