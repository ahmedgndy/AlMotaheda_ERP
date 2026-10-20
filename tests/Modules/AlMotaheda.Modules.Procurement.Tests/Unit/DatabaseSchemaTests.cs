using AlMotaheda.Modules.Procurement.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AlMotaheda.Modules.Procurement.Tests.Unit;

/// <summary>The table design, with no database.</summary>
public sealed class DatabaseSchemaTests
{
    private static ProcurementDbContext CreateContext() => new ProcurementDbContextDesignTimeFactory().CreateDbContext([]);

    [Fact]
    public void Committed_migrations_match_the_model()
    {
        using var db = CreateContext();

        Assert.False(db.Database.HasPendingModelChanges(), "The model changed without a migration. Run dotnet ef migrations add.");
        Assert.Equal(["InitialProcurement", "PriceControlLinks"], db.Database.GetMigrations().Select(id => id[(id.IndexOf('_') + 1)..]));
    }

    [Theory]
    [InlineData("[Number] AS CONCAT('PR-', REPLICATE('0', 6 - LEN([Id])), [Id]) PERSISTED")]
    [InlineData("[Number] AS CONCAT('RFQ-', REPLICATE('0', 6 - LEN([Id])), [Id]) PERSISTED")]
    [InlineData("[Number] AS CONCAT('PO-', REPLICATE('0', 6 - LEN([Id])), [Id]) PERSISTED")]
    [InlineData("[Number] AS CONCAT('GRN-', REPLICATE('0', 6 - LEN([Id])), [Id]) PERSISTED")]
    [InlineData("[Number] AS CONCAT('INV-', REPLICATE('0', 6 - LEN([Id])), [Id]) PERSISTED")]
    [InlineData("CREATE UNIQUE INDEX [UX_SupplierInvoice_Supplier_Number] ON [proc].[SupplierInvoice] ([SupplierId], [InvoiceNo]) WHERE [Status] <> 'Returned'")]
    [InlineData("CREATE UNIQUE INDEX [UX_QuotationRequest_Requisition] ON [proc].[QuotationRequest] ([RequisitionId])")]
    [InlineData("CONSTRAINT [CK_ProcurementPolicy_Single] CHECK ([Id] = 1)")]
    [InlineData("CONSTRAINT [CK_RequisitionEvent_Actor] CHECK ([ActorUserId] IS NOT NULL OR ([ActorLabel] IS NOT NULL AND [PolicyName] IS NOT NULL))")]
    [InlineData("CONSTRAINT [CK_GoodsReceiptLine_Quantity] CHECK ([Quantity] > 0)")]
    [InlineData("[Quantity] decimal(18,3) NOT NULL")]
    [InlineData("CONSTRAINT [CK_SupplierInvoice_Status] CHECK ([Status] IN ('Matched', 'Mismatch', 'Posted', 'Returned', 'PriceHold'))")]
    [InlineData("CREATE INDEX [IX_PurchaseOrderLine_PriceItemId] ON [proc].[PurchaseOrderLine] ([PriceItemId])")]
    public void The_create_script_contains(string expected)
    {
        using var db = CreateContext();

        Assert.Contains(expected, db.Database.GenerateCreateScript());
    }

    [Fact]
    public void The_policy_starts_with_auto_approval_on_and_no_tolerance()
    {
        using var db = CreateContext();

        var seed = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(ProcurementPolicy))!.GetSeedData().Single();
        Assert.Equal((1, true, 0m), ((int)seed["Id"]!, (bool)seed["AutoApproveRequisitions"]!, (decimal)seed["PriceTolerancePercent"]!));
    }

    [Fact]
    public void Nothing_is_deleted_by_the_database()
    {
        using var db = CreateContext();

        Assert.DoesNotContain("ON DELETE CASCADE", db.Database.GenerateCreateScript());
    }
}
