using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Procurement.Data;

/// <summary>
/// The Procurement module's tables, all in the <c>proc</c> schema. Suppliers, catalog items, cost items and supply
/// records stay in Projects and are reached through contracts.
/// </summary>
internal sealed class ProcurementDbContext(DbContextOptions<ProcurementDbContext> options) : DbContext(options)
{
    public const string Schema = "proc";

    /// <summary>The module keeps its own migration history, so each module's migrations stay independent.</summary>
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    public DbSet<ProcurementPolicy> Policies => Set<ProcurementPolicy>();
    public DbSet<Requisition> Requisitions => Set<Requisition>();
    public DbSet<RequisitionLine> RequisitionLines => Set<RequisitionLine>();
    public DbSet<QuotationRequest> QuotationRequests => Set<QuotationRequest>();
    public DbSet<Quotation> Quotations => Set<Quotation>();
    public DbSet<PurchaseOrder> PurchaseOrders => Set<PurchaseOrder>();
    public DbSet<PurchaseOrderLine> PurchaseOrderLines => Set<PurchaseOrderLine>();
    public DbSet<GoodsReceipt> GoodsReceipts => Set<GoodsReceipt>();
    public DbSet<GoodsReceiptLine> GoodsReceiptLines => Set<GoodsReceiptLine>();
    public DbSet<SupplierInvoice> SupplierInvoices => Set<SupplierInvoice>();
    public DbSet<SupplierInvoiceLine> SupplierInvoiceLines => Set<SupplierInvoiceLine>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ProcurementDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        StorageConventions.Apply(configurationBuilder);
}
