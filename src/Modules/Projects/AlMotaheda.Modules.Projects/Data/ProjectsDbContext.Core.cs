using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Data;

/// <summary>The tables of the first part of the workspace: supplier payments and their allocations.</summary>
internal sealed partial class ProjectsDbContext
{
    public DbSet<SupplierPayment> SupplierPayments => Set<SupplierPayment>();
    public DbSet<SupplierPaymentAllocation> SupplierPaymentAllocations => Set<SupplierPaymentAllocation>();
}
