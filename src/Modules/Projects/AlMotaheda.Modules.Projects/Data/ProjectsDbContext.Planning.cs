using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Data;

/// <summary>The tables of budgets, daily site reports and report requests. The catalog tables are in the main file.</summary>
internal sealed partial class ProjectsDbContext
{
    public DbSet<Budget> Budgets => Set<Budget>();
    public DbSet<BudgetLine> BudgetLines => Set<BudgetLine>();

    public DbSet<DailyReport> DailyReports => Set<DailyReport>();
    public DbSet<DailyReportLine> DailyReportLines => Set<DailyReportLine>();
    public DbSet<DailyReportMaterial> DailyReportMaterials => Set<DailyReportMaterial>();
    public DbSet<DailyReportEquipment> DailyReportEquipment => Set<DailyReportEquipment>();

    public DbSet<ReportRequest> ReportRequests => Set<ReportRequest>();
}
