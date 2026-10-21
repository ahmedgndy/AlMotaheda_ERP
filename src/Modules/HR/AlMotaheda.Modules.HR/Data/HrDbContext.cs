using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.HR.Data;

/// <summary>
/// The HR module's tables, all in the <c>hr</c> schema. No other module reads them: Finance asks through
/// <c>IPaymentSource</c>.
/// </summary>
internal sealed class HrDbContext(DbContextOptions<HrDbContext> options) : DbContext(options)
{
    public const string Schema = "hr";

    /// <summary>The module keeps its own migration history, so each module's migrations stay independent.</summary>
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<EmployeeDocument> EmployeeDocuments => Set<EmployeeDocument>();
    public DbSet<SalaryStructure> SalaryStructures => Set<SalaryStructure>();
    public DbSet<PayrollInput> PayrollInputs => Set<PayrollInput>();
    public DbSet<SalarySheet> SalarySheets => Set<SalarySheet>();
    public DbSet<SalarySheetLine> SalarySheetLines => Set<SalarySheetLine>();

    /// <summary>Salaried employees on projects over time, and the cases other modules refer to HR.</summary>
    public DbSet<EmployeeAssignment> EmployeeAssignments => Set<EmployeeAssignment>();
    public DbSet<HrReferral> Referrals => Set<HrReferral>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(HrDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        StorageConventions.Apply(configurationBuilder);
}
