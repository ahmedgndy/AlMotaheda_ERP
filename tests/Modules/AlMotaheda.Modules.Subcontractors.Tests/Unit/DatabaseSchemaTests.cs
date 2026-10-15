using AlMotaheda.Modules.Subcontractors.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlMotaheda.Modules.Subcontractors.Tests.Unit;

/// <summary>
/// Checks the SQL the model produces, with no database: the approved table design, and that the committed
/// migration matches the model (a forgotten migration fails here).
/// </summary>
public sealed class DatabaseSchemaTests
{
    private static SubcontractorsDbContext CreateContext() => new SubcontractorsDbContextDesignTimeFactory().CreateDbContext([]);

    [Fact]
    public void Committed_migrations_match_the_model()
    {
        using var db = CreateContext();

        Assert.False(db.Database.HasPendingModelChanges(), "The model changed without a migration. Create the migration again.");
        Assert.Equal(["InitialSubcontractors"], db.Database.GetMigrations().Select(id => id[(id.IndexOf('_') + 1)..]));
    }

    [Theory]
    [InlineData("Subcontractor")]
    [InlineData("SubContract")]
    [InlineData("ContractItem")]
    [InlineData("ProgressMeasurement")]
    [InlineData("ManpowerSheet")]
    [InlineData("SubPayment")]
    [InlineData("SubPaymentAllocation")]
    public void Tables_live_in_the_sub_schema(string table)
    {
        using var db = CreateContext();

        Assert.Contains($"CREATE TABLE [sub].[{table}]", db.Database.GenerateCreateScript());
    }

    [Theory]
    [InlineData("CREATE UNIQUE INDEX [UX_Subcontractor_NameEn] ON [sub].[Subcontractor] ([NameEn])")]
    [InlineData("CREATE UNIQUE INDEX [UX_SubContract_Project_Subcontractor] ON [sub].[SubContract] ([ProjectId], [SubcontractorId])")]
    [InlineData("CREATE UNIQUE INDEX [UX_SubContract_ContractNo] ON [sub].[SubContract] ([ContractNo]) WHERE [ContractNo] IS NOT NULL")]
    [InlineData("CREATE INDEX [IX_ContractItem_ProjectItemId] ON [sub].[ContractItem] ([ProjectItemId])")]
    [InlineData("CREATE INDEX [IX_ManpowerSheet_ProjectItemId] ON [sub].[ManpowerSheet] ([ProjectItemId])")]
    [InlineData("CREATE UNIQUE INDEX [UX_SubPayment_IdempotencyKey] ON [sub].[SubPayment] ([IdempotencyKey])")]
    [InlineData("CREATE UNIQUE INDEX [UX_SubPaymentAllocation_Payment_Measurement] ON [sub].[SubPaymentAllocation] ([PaymentId], [MeasurementId]) WHERE [MeasurementId] IS NOT NULL")]
    [InlineData("CREATE UNIQUE INDEX [UX_SubPaymentAllocation_Payment_Manpower] ON [sub].[SubPaymentAllocation] ([PaymentId], [ManpowerSheetId]) WHERE [ManpowerSheetId] IS NOT NULL")]
    [InlineData("CONSTRAINT [CK_Subcontractor_Specialty] CHECK ([Specialty] IN ('Civil', 'Masonry', 'Plumbing', 'Electrical', 'Mep', 'Finishing', 'Earthworks', 'Steel', 'Facade', 'Landscape', 'Other'))")]
    [InlineData("CONSTRAINT [CK_Subcontractor_Status] CHECK ([Status] IN ('Active', 'Inactive'))")]
    [InlineData("CONSTRAINT [CK_SubContract_Model] CHECK ([Model] IN ('UnitRate', 'LumpSum', 'DailyRate'))")]
    [InlineData("CONSTRAINT [CK_SubContract_Status] CHECK ([Status] IN ('Pending', 'Active', 'Completed'))")]
    [InlineData("CONSTRAINT [CK_SubContract_Value] CHECK ([Value] > 0)")]
    [InlineData("CONSTRAINT [CK_SubContract_Retention] CHECK ([RetentionPercent] >= 0 AND [RetentionPercent] <= 100)")]
    [InlineData("CONSTRAINT [CK_SubContract_Dates] CHECK ([EndDate] IS NULL OR [EndDate] >= [StartDate])")]
    [InlineData("CONSTRAINT [CK_ContractItem_Quantity] CHECK ([Quantity] > 0)")]
    [InlineData("CONSTRAINT [CK_ContractItem_UnitPrice] CHECK ([UnitPrice] > 0)")]
    [InlineData("CONSTRAINT [CK_ProgressMeasurement_Status] CHECK ([Status] IN ('Recorded', 'Approved', 'Rejected'))")]
    [InlineData("CONSTRAINT [CK_ProgressMeasurement_Amount] CHECK ([Amount] > 0 AND ABS([Amount] - ROUND([Quantity] * [UnitPrice], 2)) < 0.01)")]
    [InlineData("CONSTRAINT [CK_ManpowerSheet_Amount] CHECK ([Amount] > 0 AND ABS([Amount] - ROUND([Workers] * [Days] * [DailyRate], 2)) < 0.01)")]
    [InlineData("CONSTRAINT [CK_ManpowerSheet_Dates] CHECK ([ToDate] >= [FromDate])")]
    [InlineData("CONSTRAINT [CK_SubPayment_Method] CHECK ([Method] IN ('BankTransfer', 'Cheque', 'Cash'))")]
    [InlineData("CONSTRAINT [CK_SubPayment_Purpose] CHECK ([Purpose] IN ('Advance', 'Progress', 'Final', 'Retention'))")]
    [InlineData("CONSTRAINT [CK_SubPayment_Amount] CHECK ([Amount] > 0)")]
    [InlineData("CONSTRAINT [CK_SubPaymentAllocation_Amount] CHECK ([Amount] > 0)")]
    [InlineData("CONSTRAINT [FK_SubContract_Subcontractor_SubcontractorId] FOREIGN KEY ([SubcontractorId]) REFERENCES [sub].[Subcontractor] ([Id])")]
    [InlineData("CONSTRAINT [FK_ContractItem_SubContract_ContractId] FOREIGN KEY ([ContractId]) REFERENCES [sub].[SubContract] ([Id])")]
    [InlineData("CONSTRAINT [FK_ProgressMeasurement_ContractItem_ContractItemId] FOREIGN KEY ([ContractItemId]) REFERENCES [sub].[ContractItem] ([Id])")]
    [InlineData("CONSTRAINT [FK_SubPaymentAllocation_ProgressMeasurement_MeasurementId] FOREIGN KEY ([MeasurementId]) REFERENCES [sub].[ProgressMeasurement] ([Id])")]
    [InlineData("CONSTRAINT [FK_SubPaymentAllocation_ManpowerSheet_ManpowerSheetId] FOREIGN KEY ([ManpowerSheetId]) REFERENCES [sub].[ManpowerSheet] ([Id])")]
    [InlineData("[Value] decimal(18,2) NOT NULL")]
    [InlineData("[DailyRate] decimal(18,2) NULL")]
    [InlineData("[RetentionPercent] decimal(5,2) NOT NULL")]
    [InlineData("[Quantity] decimal(18,3) NOT NULL")]
    [InlineData("[Days] decimal(18,3) NOT NULL")]
    [InlineData("[MeasuredOn] date NOT NULL")]
    [InlineData("[ContractNo] nvarchar(50) NULL")]
    [InlineData("[NameEn] nvarchar(200) NOT NULL")]
    [InlineData("[Phone] varchar(30) NULL")]
    [InlineData("[Email] varchar(200) NULL")]
    [InlineData("[Address] nvarchar(400) NULL")]
    [InlineData("[Scope] nvarchar(1000) NULL")]
    [InlineData("[Model] varchar(30) NOT NULL")]
    [InlineData("[IdempotencyKey] uniqueidentifier NOT NULL")]
    [InlineData("[RowVersion] rowversion NOT NULL")]
    public void Script_contains_the_approved_constraints_and_indexes(string expected)
    {
        using var db = CreateContext();

        Assert.Contains(expected, db.Database.GenerateCreateScript());
    }

    [Fact]
    public void Checks_on_nullable_columns_test_null_explicitly()
    {
        using var db = CreateContext();
        var script = db.Database.GenerateCreateScript();

        // A CHECK passes when its result is unknown (NULL), so each branch tests IS NULL / IS NOT NULL itself.
        Assert.Contains(
            "CONSTRAINT [CK_SubContract_DailyRate] CHECK (([Model] = 'DailyRate' AND [DailyRate] IS NOT NULL AND [DailyRate] > 0) "
            + "OR ([Model] <> 'DailyRate' AND [DailyRate] IS NULL))",
            script);
        Assert.Contains(
            "CONSTRAINT [CK_SubPaymentAllocation_Work] CHECK (([MeasurementId] IS NOT NULL AND [ManpowerSheetId] IS NULL) "
            + "OR ([MeasurementId] IS NULL AND [ManpowerSheetId] IS NOT NULL))",
            script);
        Assert.Contains(
            "CONSTRAINT [CK_SubPayment_Void] CHECK (([VoidedAt] IS NULL AND [VoidedBy] IS NULL AND [VoidReason] IS NULL) "
            + "OR ([VoidedAt] IS NOT NULL AND [VoidedBy] IS NOT NULL AND [VoidReason] IS NOT NULL))",
            script);
        Assert.Contains(
            "CONSTRAINT [CK_ProgressMeasurement_Review] CHECK (([Status] = 'Recorded' AND [ReviewedAt] IS NULL AND [ReviewedBy] IS NULL AND [ReviewNote] IS NULL) "
            + "OR ([Status] = 'Approved' AND [ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL) "
            + "OR ([Status] = 'Rejected' AND [ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL AND [ReviewNote] IS NOT NULL))",
            script);
        Assert.Contains("CONSTRAINT [CK_ManpowerSheet_Review] CHECK (([Status] = 'Recorded'", script);
    }

    [Fact]
    public void Migration_adds_the_foreign_keys_to_the_projects_tables()
    {
        using var db = CreateContext();

        var script = db.GetService<IMigrator>().GenerateScript();

        Assert.Contains(
            "ALTER TABLE [sub].[SubContract] ADD CONSTRAINT [FK_SubContract_Project_ProjectId] FOREIGN KEY ([ProjectId]) REFERENCES [projects].[Project] ([Id])",
            script);
        Assert.Contains(
            "ALTER TABLE [sub].[ContractItem] ADD CONSTRAINT [FK_ContractItem_ProjectItem_ProjectItemId] FOREIGN KEY ([ProjectItemId]) REFERENCES [projects].[ProjectItem] ([Id])",
            script);
        Assert.Contains(
            "ALTER TABLE [sub].[ManpowerSheet] ADD CONSTRAINT [FK_ManpowerSheet_ProjectItem_ProjectItemId] FOREIGN KEY ([ProjectItemId]) REFERENCES [projects].[ProjectItem] ([Id])",
            script);
        Assert.Contains("[sub].[__EFMigrationsHistory]", script);
        Assert.DoesNotContain("ON DELETE CASCADE", script);
    }

    [Fact]
    public void Foreign_keys_never_cascade()
    {
        using var db = CreateContext();

        var cascading = db.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetForeignKeys())
            .Where(foreignKey => foreignKey.DeleteBehavior != DeleteBehavior.NoAction)
            .Select(foreignKey => foreignKey.ToString());

        Assert.Empty(cascading);
    }
}
