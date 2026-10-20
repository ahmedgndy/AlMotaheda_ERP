using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Procurement.Data.Configurations;

/// <summary>Shared column sizes and the number series.</summary>
internal static class ProcurementColumns
{
    public const int DescriptionMaxLength = 200;
    public const int NoteMaxLength = 1000;
    public const int ReferenceMaxLength = 50;
    public const int TermsMaxLength = 200;

    /// <summary><c>PR-000123</c>: deterministic, so stored and indexed; past 999999 it simply gets more digits.</summary>
    public static void NumberSeries<T>(EntityTypeBuilder<T> builder, string prefix, string indexName)
        where T : class
    {
        builder.Property<string>("Number")
            .HasMaxLength(20)
            .HasComputedColumnSql($"CONCAT('{prefix}-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true);
        builder.HasIndex("Number").IsUnique().HasDatabaseName(indexName);
    }

    public static string Statuses<TEnum>(string column = "Status")
        where TEnum : struct, Enum => $"[{column}] IN ({SqlList.Of(Enum.GetNames<TEnum>())})";
}

internal sealed class ProcurementPolicyConfiguration : IEntityTypeConfiguration<ProcurementPolicy>
{
    public void Configure(EntityTypeBuilder<ProcurementPolicy> builder)
    {
        builder.ToTable(nameof(ProcurementPolicy), table =>
        {
            table.HasCheckConstraint("CK_ProcurementPolicy_Single", $"[Id] = {ProcurementPolicy.SingletonId}");
            table.HasCheckConstraint("CK_ProcurementPolicy_Tolerance", "[PriceTolerancePercent] >= 0 AND [PriceTolerancePercent] <= 100");
        });

        builder.Property(policy => policy.Id).ValueGeneratedNever();
        builder.Property(policy => policy.PriceTolerancePercent).HasPrecision(5, 2);
        builder.Property(policy => policy.RowVersion).IsRowVersion();

        // The brief's default: requisitions are approved by the policy until the company has a purchasing department.
        builder.HasData(new ProcurementPolicy { Id = ProcurementPolicy.SingletonId, AutoApproveRequisitions = true, PriceTolerancePercent = 0 });
    }
}

internal sealed class RequisitionConfiguration : IEntityTypeConfiguration<Requisition>
{
    public void Configure(EntityTypeBuilder<Requisition> builder)
    {
        builder.ToTable(nameof(Requisition), table => table.HasCheckConstraint("CK_Requisition_Status", ProcurementColumns.Statuses<RequisitionStatus>()));

        ProcurementColumns.NumberSeries(builder, "PR", "UX_Requisition_Number");
        builder.Property(requisition => requisition.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(requisition => requisition.Notes).HasMaxLength(ProcurementColumns.NoteMaxLength).IsUnicode();
        builder.Property(requisition => requisition.RowVersion).IsRowVersion();

        builder.HasIndex(requisition => new { requisition.ProjectId, requisition.Id }, "IX_Requisition_Project");
        builder.HasIndex(requisition => new { requisition.Status, requisition.Id }, "IX_Requisition_Status");

        builder.HasMany(requisition => requisition.Lines).WithOne().HasForeignKey(line => line.RequisitionId).OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(requisition => requisition.Events).WithOne().HasForeignKey(entry => entry.RequisitionId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class RequisitionLineConfiguration : IEntityTypeConfiguration<RequisitionLine>
{
    public void Configure(EntityTypeBuilder<RequisitionLine> builder)
    {
        builder.ToTable(nameof(RequisitionLine), table =>
        {
            table.HasCheckConstraint("CK_RequisitionLine_Quantity", "[Quantity] > 0");
            table.HasCheckConstraint("CK_RequisitionLine_Unit", $"[Unit] IN ({SqlList.Of(MeasurementUnits.All)})");
        });

        builder.Property(line => line.Description).HasMaxLength(ProcurementColumns.DescriptionMaxLength).IsUnicode();
        builder.Property(line => line.Unit).HasMaxLength(20);
        builder.Property(line => line.Quantity).HasPrecision(18, 3);
        builder.HasIndex(line => line.PriceItemId, "IX_RequisitionLine_PriceItemId");
    }
}

internal sealed class RequisitionEventConfiguration : IEntityTypeConfiguration<RequisitionEvent>
{
    public void Configure(EntityTypeBuilder<RequisitionEvent> builder)
    {
        builder.ToTable(nameof(RequisitionEvent), table =>
            // A step is made by a person, or by the system under a named policy.
            table.HasCheckConstraint("CK_RequisitionEvent_Actor", "[ActorUserId] IS NOT NULL OR ([ActorLabel] IS NOT NULL AND [PolicyName] IS NOT NULL)"));

        builder.Property(entry => entry.Action).HasMaxLength(30);
        builder.Property(entry => entry.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(entry => entry.ActorLabel).HasMaxLength(100);
        builder.Property(entry => entry.PolicyName).HasMaxLength(100);
        builder.Property(entry => entry.Note).HasMaxLength(ProcurementColumns.NoteMaxLength).IsUnicode();
        builder.HasIndex(entry => entry.RequisitionId, "IX_RequisitionEvent_RequisitionId");
    }
}

internal sealed class QuotationRequestConfiguration : IEntityTypeConfiguration<QuotationRequest>
{
    public const string RequisitionIndex = "UX_QuotationRequest_Requisition";

    public void Configure(EntityTypeBuilder<QuotationRequest> builder)
    {
        builder.ToTable(nameof(QuotationRequest), table =>
        {
            table.HasCheckConstraint("CK_QuotationRequest_Status", ProcurementColumns.Statuses<QuotationRequestStatus>());
            table.HasCheckConstraint("CK_QuotationRequest_Wanted", "[QuotationsWanted] IS NULL OR [QuotationsWanted] BETWEEN 1 AND 10");
            table.HasCheckConstraint("CK_QuotationRequest_Selected", "[Status] <> 'Selected' OR [SelectedQuotationId] IS NOT NULL");
        });

        ProcurementColumns.NumberSeries(builder, "RFQ", "UX_QuotationRequest_Number");
        builder.Property(request => request.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(request => request.RequestNote).HasMaxLength(ProcurementColumns.NoteMaxLength).IsUnicode();
        builder.Property(request => request.RecommendationReason).HasMaxLength(ProcurementColumns.NoteMaxLength).IsUnicode();
        builder.Property(request => request.DecisionNote).HasMaxLength(ProcurementColumns.NoteMaxLength).IsUnicode();
        builder.Property(request => request.RowVersion).IsRowVersion();

        // One quotation round per PR.
        builder.HasIndex(request => request.RequisitionId).IsUnique().HasDatabaseName(RequisitionIndex);
        builder.HasIndex(request => new { request.ProjectId, request.Id }, "IX_QuotationRequest_Project");

        builder.HasOne<Requisition>().WithMany().HasForeignKey(request => request.RequisitionId).OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(request => request.Quotations).WithOne().HasForeignKey(quotation => quotation.RequestId).OnDelete(DeleteBehavior.ClientCascade);
    }
}

internal sealed class QuotationConfiguration : IEntityTypeConfiguration<Quotation>
{
    public void Configure(EntityTypeBuilder<Quotation> builder)
    {
        builder.ToTable(nameof(Quotation), table =>
        {
            table.HasCheckConstraint("CK_Quotation_Total", "[Total] > 0");
            table.HasCheckConstraint("CK_Quotation_DeliveryDays", "[DeliveryDays] IS NULL OR [DeliveryDays] >= 0");
        });

        builder.Property(quotation => quotation.SupplierReference).HasMaxLength(ProcurementColumns.ReferenceMaxLength).IsUnicode();
        builder.Property(quotation => quotation.PaymentTerms).HasMaxLength(ProcurementColumns.TermsMaxLength).IsUnicode();
        builder.Property(quotation => quotation.Notes).HasMaxLength(ProcurementColumns.NoteMaxLength).IsUnicode();

        builder.HasMany(quotation => quotation.Lines).WithOne().HasForeignKey(line => line.QuotationId).OnDelete(DeleteBehavior.ClientCascade);
    }
}

internal sealed class QuotationLineConfiguration : IEntityTypeConfiguration<QuotationLine>
{
    public void Configure(EntityTypeBuilder<QuotationLine> builder)
    {
        builder.ToTable(nameof(QuotationLine), table => table.HasCheckConstraint("CK_QuotationLine_Price", "[UnitPrice] > 0"));
        builder.HasIndex(line => new { line.QuotationId, line.RequisitionLineId }).IsUnique().HasDatabaseName("UX_QuotationLine_Line");
        builder.HasOne<RequisitionLine>().WithMany().HasForeignKey(line => line.RequisitionLineId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> builder)
    {
        builder.ToTable(nameof(PurchaseOrder), table =>
        {
            table.HasCheckConstraint("CK_PurchaseOrder_Status", ProcurementColumns.Statuses<PurchaseOrderStatus>());
            table.HasCheckConstraint("CK_PurchaseOrder_Cancelled", "[Status] <> 'Cancelled' OR ([CancelledAt] IS NOT NULL AND [CancelReason] IS NOT NULL)");
        });

        ProcurementColumns.NumberSeries(builder, "PO", "UX_PurchaseOrder_Number");
        builder.Property(order => order.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(order => order.PaymentTerms).HasMaxLength(ProcurementColumns.TermsMaxLength).IsUnicode();
        builder.Property(order => order.Notes).HasMaxLength(ProcurementColumns.NoteMaxLength).IsUnicode();
        builder.Property(order => order.CancelReason).HasMaxLength(ProcurementColumns.NoteMaxLength).IsUnicode();
        builder.Property(order => order.RowVersion).IsRowVersion();

        builder.HasIndex(order => new { order.ProjectId, order.Id }, "IX_PurchaseOrder_Project");
        builder.HasIndex(order => order.RequisitionId, "IX_PurchaseOrder_RequisitionId");
        builder.HasIndex(order => new { order.SupplierId, order.Id }, "IX_PurchaseOrder_Supplier");

        builder.HasOne<Requisition>().WithMany().HasForeignKey(order => order.RequisitionId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<Quotation>().WithMany().HasForeignKey(order => order.QuotationId).OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(order => order.Lines).WithOne().HasForeignKey(line => line.OrderId).OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(order => order.Revisions).WithOne().HasForeignKey(revision => revision.OrderId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> builder)
    {
        builder.ToTable(nameof(PurchaseOrderLine), table =>
        {
            // Zero is allowed: a revision can take a line down to what was received (nothing).
            table.HasCheckConstraint("CK_PurchaseOrderLine_Quantity", "[Quantity] >= 0");
            table.HasCheckConstraint("CK_PurchaseOrderLine_Price", "[UnitPrice] > 0");
        });

        builder.Property(line => line.Description).HasMaxLength(ProcurementColumns.DescriptionMaxLength).IsUnicode();
        builder.Property(line => line.Unit).HasMaxLength(20);
        builder.Property(line => line.Quantity).HasPrecision(18, 3);
        builder.HasIndex(line => line.RequisitionLineId, "IX_PurchaseOrderLine_RequisitionLineId");
        builder.HasIndex(line => line.PriceItemId, "IX_PurchaseOrderLine_PriceItemId");
        builder.HasOne<RequisitionLine>().WithMany().HasForeignKey(line => line.RequisitionLineId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class PurchaseOrderRevisionConfiguration : IEntityTypeConfiguration<PurchaseOrderRevision>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderRevision> builder)
    {
        builder.ToTable(nameof(PurchaseOrderRevision));
        builder.Property(revision => revision.Reason).HasMaxLength(ProcurementColumns.NoteMaxLength).IsUnicode();
        builder.Property(revision => revision.PreviousLines).IsUnicode();
        builder.HasIndex(revision => new { revision.OrderId, revision.RevisionNo }).IsUnique().HasDatabaseName("UX_PurchaseOrderRevision_No");
    }
}

internal sealed class GoodsReceiptConfiguration : IEntityTypeConfiguration<GoodsReceipt>
{
    public void Configure(EntityTypeBuilder<GoodsReceipt> builder)
    {
        builder.ToTable(nameof(GoodsReceipt));

        ProcurementColumns.NumberSeries(builder, "GRN", "UX_GoodsReceipt_Number");
        builder.Property(receipt => receipt.DeliveryNoteNo).HasMaxLength(ProcurementColumns.ReferenceMaxLength).IsUnicode();
        builder.Property(receipt => receipt.Notes).HasMaxLength(ProcurementColumns.NoteMaxLength).IsUnicode();

        builder.HasIndex(receipt => receipt.OrderId, "IX_GoodsReceipt_OrderId");
        builder.HasIndex(receipt => new { receipt.ProjectId, receipt.Id }, "IX_GoodsReceipt_Project");

        builder.HasOne<PurchaseOrder>().WithMany().HasForeignKey(receipt => receipt.OrderId).OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(receipt => receipt.Lines).WithOne().HasForeignKey(line => line.ReceiptId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class GoodsReceiptLineConfiguration : IEntityTypeConfiguration<GoodsReceiptLine>
{
    public void Configure(EntityTypeBuilder<GoodsReceiptLine> builder)
    {
        builder.ToTable(nameof(GoodsReceiptLine), table => table.HasCheckConstraint("CK_GoodsReceiptLine_Quantity", "[Quantity] > 0"));
        builder.Property(line => line.Quantity).HasPrecision(18, 3);
        builder.HasIndex(line => line.OrderLineId, "IX_GoodsReceiptLine_OrderLineId").IncludeProperties(line => line.Quantity);
        builder.HasOne<PurchaseOrderLine>().WithMany().HasForeignKey(line => line.OrderLineId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class SupplierInvoiceConfiguration : IEntityTypeConfiguration<SupplierInvoice>
{
    public const string SupplierNumberIndex = "UX_SupplierInvoice_Supplier_Number";

    public void Configure(EntityTypeBuilder<SupplierInvoice> builder)
    {
        builder.ToTable(nameof(SupplierInvoice), table =>
        {
            table.HasCheckConstraint("CK_SupplierInvoice_Status", ProcurementColumns.Statuses<SupplierInvoiceStatus>());
            table.HasCheckConstraint("CK_SupplierInvoice_Resolution", $"[Resolution] IS NULL OR {ProcurementColumns.Statuses<InvoiceResolution>("Resolution")}");
            table.HasCheckConstraint("CK_SupplierInvoice_Total", "[Total] > 0");
            table.HasCheckConstraint("CK_SupplierInvoice_Posted", "[Status] <> 'Posted' OR [PostedAt] IS NOT NULL");
            table.HasCheckConstraint("CK_SupplierInvoice_CreditNote", "[Resolution] IS NULL OR [Resolution] <> 'CreditNote' OR [CreditNoteNo] IS NOT NULL");
        });

        ProcurementColumns.NumberSeries(builder, "INV", "UX_SupplierInvoice_Number");
        builder.Property(invoice => invoice.InvoiceNo).HasMaxLength(ProcurementColumns.ReferenceMaxLength).IsUnicode();
        builder.Property(invoice => invoice.DeliveryNoteNo).HasMaxLength(ProcurementColumns.ReferenceMaxLength).IsUnicode();
        builder.Property(invoice => invoice.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(invoice => invoice.Resolution).HasConversion<string>().HasMaxLength(30);
        builder.Property(invoice => invoice.ResolutionReason).HasMaxLength(ProcurementColumns.NoteMaxLength).IsUnicode();
        builder.Property(invoice => invoice.CreditNoteNo).HasMaxLength(ProcurementColumns.ReferenceMaxLength).IsUnicode();
        builder.Property(invoice => invoice.RowVersion).IsRowVersion();

        // The same supplier invoice can't be recorded twice (so it can't be paid twice); a returned one can come back.
        builder.HasIndex(invoice => new { invoice.SupplierId, invoice.InvoiceNo })
            .IsUnique()
            .HasFilter("[Status] <> 'Returned'")
            .HasDatabaseName(SupplierNumberIndex);
        builder.HasIndex(invoice => invoice.OrderId, "IX_SupplierInvoice_OrderId");
        builder.HasIndex(invoice => new { invoice.ProjectId, invoice.Id }, "IX_SupplierInvoice_Project");
        builder.HasIndex(invoice => new { invoice.Status, invoice.Id }, "IX_SupplierInvoice_Status");

        builder.HasOne<PurchaseOrder>().WithMany().HasForeignKey(invoice => invoice.OrderId).OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(invoice => invoice.Lines).WithOne().HasForeignKey(line => line.InvoiceId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class SupplierInvoiceLineConfiguration : IEntityTypeConfiguration<SupplierInvoiceLine>
{
    public void Configure(EntityTypeBuilder<SupplierInvoiceLine> builder)
    {
        builder.ToTable(nameof(SupplierInvoiceLine), table =>
        {
            table.HasCheckConstraint("CK_SupplierInvoiceLine_Quantity", "[Quantity] > 0");
            table.HasCheckConstraint("CK_SupplierInvoiceLine_Price", "[UnitPrice] > 0 AND [OrderUnitPrice] > 0");
        });

        builder.Property(line => line.Quantity).HasPrecision(18, 3);
        builder.Property(line => line.PriceCheck).HasMaxLength(20).IsUnicode(false);
        builder.HasIndex(line => line.OrderLineId, "IX_SupplierInvoiceLine_OrderLineId").IncludeProperties(line => new { line.InvoiceId, line.Quantity });
        builder.HasIndex(line => line.PriceExceptionId, "IX_SupplierInvoiceLine_PriceExceptionId");
        builder.HasOne<PurchaseOrderLine>().WithMany().HasForeignKey(line => line.OrderLineId).OnDelete(DeleteBehavior.NoAction);
    }
}
