using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Tests.Unit.Core;

/// <summary>The supplier payment tables, as the model produces them, with no database.</summary>
public sealed class CoreSchemaTests
{
    private static ProjectsDbContext CreateContext() => new ProjectsDbContextDesignTimeFactory().CreateDbContext([]);

    [Fact]
    public void Payment_tables_live_in_the_projects_schema()
    {
        using var db = CreateContext();
        var script = db.Database.GenerateCreateScript();

        Assert.Contains("CREATE TABLE [projects].[SupplierPayment]", script);
        Assert.Contains("CREATE TABLE [projects].[SupplierPaymentAllocation]", script);
    }

    [Theory]
    [InlineData("CREATE UNIQUE INDEX [UX_SupplierPayment_IdempotencyKey] ON [projects].[SupplierPayment] ([IdempotencyKey])")]
    [InlineData("CREATE INDEX [IX_SupplierPayment_Project_Date] ON [projects].[SupplierPayment] ([ProjectId], [PaymentDate])")]
    [InlineData("CREATE INDEX [IX_SupplierPayment_Project_Supplier] ON [projects].[SupplierPayment] ([ProjectId], [SupplierId])")]
    [InlineData("CREATE UNIQUE INDEX [UX_SupplierPaymentAllocation_Payment_Expense] ON [projects].[SupplierPaymentAllocation] ([PaymentId], [ExpenseId])")]
    [InlineData("CREATE INDEX [IX_SupplierPaymentAllocation_ExpenseId] ON [projects].[SupplierPaymentAllocation] ([ExpenseId])")]
    [InlineData("CONSTRAINT [CK_SupplierPayment_Method] CHECK ([Method] IN ('BankTransfer', 'Cheque', 'Cash'))")]
    [InlineData("CONSTRAINT [CK_SupplierPayment_Amount] CHECK ([Amount] > 0)")]
    [InlineData("CONSTRAINT [CK_SupplierPaymentAllocation_Amount] CHECK ([Amount] > 0)")]
    [InlineData("CONSTRAINT [FK_SupplierPayment_Project_ProjectId] FOREIGN KEY ([ProjectId]) REFERENCES [projects].[Project] ([Id])")]
    [InlineData("CONSTRAINT [FK_SupplierPayment_Supplier_SupplierId] FOREIGN KEY ([SupplierId]) REFERENCES [projects].[Supplier] ([Id])")]
    [InlineData("CONSTRAINT [FK_SupplierPaymentAllocation_SupplierPayment_PaymentId] FOREIGN KEY ([PaymentId]) REFERENCES [projects].[SupplierPayment] ([Id])")]
    [InlineData("CONSTRAINT [FK_SupplierPaymentAllocation_ProjectExpense_ExpenseId] FOREIGN KEY ([ExpenseId]) REFERENCES [projects].[ProjectExpense] ([Id])")]
    [InlineData("[PaymentDate] date NOT NULL")]
    [InlineData("[Amount] decimal(18,2) NOT NULL")]
    [InlineData("[IdempotencyKey] uniqueidentifier NOT NULL")]
    [InlineData("[Reference] nvarchar(100) NULL")]
    [InlineData("[VoidReason] nvarchar(300) NULL")]
    [InlineData("[Method] varchar(30) NOT NULL")]
    public void Script_contains_the_approved_constraints_and_indexes(string expected)
    {
        using var db = CreateContext();

        Assert.Contains(expected, db.Database.GenerateCreateScript());
    }

    [Fact]
    public void A_void_is_recorded_whole_or_not_at_all()
    {
        using var db = CreateContext();

        // Each column is tested with IS (NOT) NULL: a CHECK passes when its result is unknown.
        Assert.Contains(
            "CONSTRAINT [CK_SupplierPayment_Void] CHECK (([VoidedAt] IS NULL AND [VoidedBy] IS NULL AND [VoidReason] IS NULL) "
            + "OR ([VoidedAt] IS NOT NULL AND [VoidedBy] IS NOT NULL AND [VoidReason] IS NOT NULL))",
            db.Database.GenerateCreateScript());
    }

    [Fact]
    public void Payment_foreign_keys_never_cascade()
    {
        using var db = CreateContext();

        var cascading = db.Model.GetEntityTypes()
            .Where(entity => entity.ClrType == typeof(SupplierPayment) || entity.ClrType == typeof(SupplierPaymentAllocation))
            .SelectMany(entity => entity.GetForeignKeys())
            .Where(foreignKey => foreignKey.DeleteBehavior != DeleteBehavior.NoAction)
            .Select(foreignKey => foreignKey.ToString());

        Assert.Empty(cascading);
    }
}
