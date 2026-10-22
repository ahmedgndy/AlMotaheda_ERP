using AlMotaheda.Modules.Fleet.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlMotaheda.Modules.Fleet.Tests.Unit;

/// <summary>
/// Checks the SQL the model produces, with no database: the approved table design, and that the committed
/// migration matches the model (a forgotten migration fails here).
/// </summary>
public sealed class DatabaseSchemaTests
{
    private static FleetDbContext CreateContext() => new FleetDbContextDesignTimeFactory().CreateDbContext([]);

    [Fact]
    public void Committed_migrations_match_the_model()
    {
        using var db = CreateContext();

        Assert.False(db.Database.HasPendingModelChanges(), "The model changed without a migration. Run dotnet ef migrations add.");
        Assert.Equal(["InitialFleet", "EquipmentCostAdjustments", "EquipmentCostWorkItem", "VehiclesAndCustody", "ClaimOdometerOptional"], db.Database.GetMigrations().Select(id => id[(id.IndexOf('_') + 1)..]));
    }

    [Fact]
    public void Tables_live_in_the_fleet_schema()
    {
        using var db = CreateContext();
        var script = db.Database.GenerateCreateScript();

        Assert.Contains("CREATE TABLE [fleet].[Equipment]", script);
        Assert.Contains("CREATE TABLE [fleet].[EquipmentRental]", script);
        Assert.Contains("CREATE TABLE [fleet].[EquipmentAssignment]", script);
        Assert.Contains("CREATE TABLE [fleet].[EquipmentCost]", script);
        Assert.Contains("CREATE TABLE [fleet].[VoidedEquipmentCostPayment]", script);
        Assert.Contains("CREATE TABLE [fleet].[Vehicle]", script);
        Assert.Contains("CREATE TABLE [fleet].[VehicleDriverAssignment]", script);
        Assert.Contains("CREATE TABLE [fleet].[VehicleCustodyEntry]", script);
        Assert.Contains("CREATE TABLE [fleet].[VehicleExpense]", script);
        Assert.Contains("CREATE TABLE [fleet].[VehicleExpenseFlag]", script);
        Assert.Contains("CREATE TABLE [fleet].[VehicleExpenseAlert]", script);
        Assert.Contains("CREATE TABLE [fleet].[FleetAttachment]", script);
        Assert.Contains("CREATE TABLE [fleet].[VehicleViolation]", script);
    }

    [Theory]
    [InlineData("CREATE UNIQUE INDEX [UX_Equipment_AssetNo] ON [fleet].[Equipment] ([AssetNo])")]
    [InlineData("CREATE UNIQUE INDEX [UX_EquipmentAssignment_Open] ON [fleet].[EquipmentAssignment] ([EquipmentId]) WHERE [EndDate] IS NULL")]
    [InlineData("CREATE UNIQUE INDEX [UX_EquipmentRental_Open] ON [fleet].[EquipmentRental] ([EquipmentId]) WHERE [EndDate] IS NULL")]
    [InlineData("CREATE INDEX [IX_EquipmentAssignment_Project_End] ON [fleet].[EquipmentAssignment] ([ProjectId], [EndDate])")]
    [InlineData("CREATE INDEX [IX_EquipmentCost_Project_Date] ON [fleet].[EquipmentCost] ([ProjectId], [CostDate])")]
    [InlineData("CREATE INDEX [IX_EquipmentCost_Project_Status] ON [fleet].[EquipmentCost] ([ProjectId], [Status])")]
    [InlineData("CREATE INDEX [IX_EquipmentCost_Project_Item] ON [fleet].[EquipmentCost] ([ProjectId], [ProjectItemId])")]
    [InlineData("[ProjectItemId] int NULL")]
    [InlineData("CONSTRAINT [CK_Equipment_Category] CHECK ([Category] IN ('Tools', 'Medium', 'Heavy', 'Survey', 'Safety', 'Vehicle'))")]
    [InlineData("CONSTRAINT [CK_Equipment_Ownership] CHECK ([Ownership] IN ('Owned', 'Rented'))")]
    [InlineData("CONSTRAINT [CK_Equipment_Status] CHECK ([Status] IN ('InService', 'UnderMaintenance', 'Lost', 'Retired'))")]
    [InlineData("CONSTRAINT [CK_EquipmentRental_Rate] CHECK ([Rate] > 0)")]
    [InlineData("CONSTRAINT [CK_EquipmentRental_Dates] CHECK ([EndDate] IS NULL OR [EndDate] >= [StartDate])")]
    [InlineData("CONSTRAINT [CK_EquipmentAssignment_Dates] CHECK ([EndDate] IS NULL OR [EndDate] >= [StartDate])")]
    [InlineData("CONSTRAINT [CK_EquipmentCost_Amount] CHECK ([Amount] > 0)")]
    [InlineData("CONSTRAINT [CK_EquipmentCost_Status] CHECK ([Status] IN ('Recorded', 'Approved', 'Returned', 'Rejected'))")]
    [InlineData("CONSTRAINT [CK_EquipmentCost_Type] CHECK ([Type] IN ('Rental', 'Fuel', 'Maintenance', 'Repair', 'Transport', 'Other'))")]
    [InlineData("CONSTRAINT [CK_EquipmentCost_RentalSupplier] CHECK ([Type] <> 'Rental' OR [SupplierId] IS NOT NULL)")]
    [InlineData("CONSTRAINT [CK_EquipmentCost_CreditSupplier] CHECK ([Channel] = 'Custody' OR [SupplierId] IS NOT NULL)")]
    [InlineData("CONSTRAINT [FK_EquipmentCost_Equipment_EquipmentId] FOREIGN KEY ([EquipmentId]) REFERENCES [fleet].[Equipment] ([Id])")]
    [InlineData("CONSTRAINT [FK_EquipmentAssignment_Equipment_EquipmentId] FOREIGN KEY ([EquipmentId]) REFERENCES [fleet].[Equipment] ([Id])")]
    [InlineData("CREATE INDEX [IX_VoidedEquipmentCostPayment_CostId] ON [fleet].[VoidedEquipmentCostPayment] ([CostId])")]
    [InlineData("CONSTRAINT [CK_VoidedEquipmentCostPayment_PaymentMethod] CHECK ([PaymentMethod] IN ('BankTransfer', 'Cheque', 'Cash'))")]
    [InlineData("CONSTRAINT [FK_VoidedEquipmentCostPayment_EquipmentCost_CostId] FOREIGN KEY ([CostId]) REFERENCES [fleet].[EquipmentCost] ([Id])")]
    [InlineData("[AssetNo] varchar(30) NOT NULL")]
    [InlineData("[NameAr] nvarchar(200) NOT NULL")]
    [InlineData("[Amount] decimal(18,2) NOT NULL")]
    [InlineData("[Quantity] decimal(18,3) NULL")]
    [InlineData("[Liters] decimal(18,3) NULL")]
    [InlineData("[CostDate] date NOT NULL")]
    [InlineData("[RowVersion] rowversion NOT NULL")]
    // Vehicles: one register row extended (same id), one driver at a time, append-only custody, claims posted once.
    [InlineData("CONSTRAINT [FK_Vehicle_Equipment_Id] FOREIGN KEY ([Id]) REFERENCES [fleet].[Equipment] ([Id])")]
    [InlineData("CREATE UNIQUE INDEX [UX_Vehicle_Plate] ON [fleet].[Vehicle] ([Plate])")]
    [InlineData("CONSTRAINT [CK_Vehicle_Odometer] CHECK ([OdometerKm] >= 0)")]
    [InlineData("CONSTRAINT [CK_Vehicle_Gps] CHECK ([GpsStatus] = 'NotConfigured' OR ([GpsProvider] IS NOT NULL AND [GpsDeviceId] IS NOT NULL))")]
    [InlineData("CREATE UNIQUE INDEX [UX_VehicleDriverAssignment_Open] ON [fleet].[VehicleDriverAssignment] ([VehicleId]) WHERE [EndDate] IS NULL")]
    [InlineData("CONSTRAINT [CK_VehicleCustodyEntry_Amount] CHECK ([Amount] > 0)")]
    [InlineData("CONSTRAINT [CK_VehicleCustodyEntry_Pending] CHECK ([Status] = 'Posted' OR [Kind] = 'Expense')")]
    [InlineData("CREATE UNIQUE INDEX [UX_VehicleCustodyEntry_Source] ON [fleet].[VehicleCustodyEntry] ([SourceType], [SourceId])")]
    [InlineData("CONSTRAINT [CK_VehicleExpense_Status] CHECK ([Status] IN ('Submitted', 'Approved', 'Rejected', 'EvidenceRequested', 'Escalated'))")]
    [InlineData("CONSTRAINT [CK_VehicleExpense_Approved] CHECK ([Status] <> 'Approved' OR [PostingStatus] <> 'NotPosted')")]
    [InlineData("CONSTRAINT [CK_VehicleExpense_Referral] CHECK ([HrReferralId] IS NULL OR [Status] = 'Rejected')")]
    [InlineData("CREATE UNIQUE INDEX [UX_VehicleExpense_IdempotencyKey] ON [fleet].[VehicleExpense] ([IdempotencyKey])")]
    [InlineData("CREATE UNIQUE INDEX [UX_VehicleExpense_CustodyEntry] ON [fleet].[VehicleExpense] ([CustodyEntryId]) WHERE [CustodyEntryId] IS NOT NULL")]
    [InlineData("[Number] AS CONCAT('VX-', REPLICATE('0', 6 - LEN([Id])), [Id]) PERSISTED")]
    [InlineData("CONSTRAINT [CK_FleetAttachment_Sha256] CHECK (LEN([Sha256]) = 64 AND [Sha256] COLLATE Latin1_General_BIN NOT LIKE '%[^0-9a-f]%')")]
    [InlineData("CONSTRAINT [CK_VehicleViolation_Status] CHECK ([Status] IN ('Recorded', 'UnderReview', 'Resolved', 'Dismissed'))")]
    [InlineData("[OdometerKm] decimal(18,1) NOT NULL")]
    [InlineData("[FuelQuantity] decimal(18,3) NULL")]
    public void Script_contains_the_approved_constraints_and_indexes(string expected)
    {
        using var db = CreateContext();

        Assert.Contains(expected, db.Database.GenerateCreateScript());
    }

    [Fact]
    public void Checks_on_nullable_columns_test_is_null_first()
    {
        using var db = CreateContext();
        var script = db.Database.GenerateCreateScript();

        // A CHECK passes when its result is unknown (NULL), so each branch tests IS NULL / IS NOT NULL itself.
        Assert.Contains(
            "CONSTRAINT [CK_EquipmentCost_TypeColumns] CHECK (([Type] = 'Fuel' AND [Liters] IS NOT NULL AND [PricePerLiter] IS NOT NULL AND [Liters] > 0",
            script);
        Assert.Contains("[Basis] <> 'Lump' AND [Quantity] IS NOT NULL AND [Rate] IS NOT NULL AND [Quantity] > 0 AND [Rate] > 0", script);
        Assert.Contains(
            "CONSTRAINT [CK_EquipmentCost_Review] CHECK (([Status] = 'Recorded' AND [ReviewedAt] IS NULL AND [ReviewedBy] IS NULL)",
            script);
        Assert.Contains("AND [Status] = 'Approved' AND [Channel] = 'Credit')", script);
        Assert.Contains(
            "CONSTRAINT [CK_EquipmentAssignment_End] CHECK (([EndDate] IS NULL AND [EndedAt] IS NULL AND [EndedBy] IS NULL)",
            script);
    }

    [Theory]
    [InlineData("ALTER TABLE [fleet].[EquipmentAssignment] ADD CONSTRAINT [FK_EquipmentAssignment_Project_ProjectId] FOREIGN KEY ([ProjectId]) REFERENCES [projects].[Project] ([Id])")]
    [InlineData("ALTER TABLE [fleet].[EquipmentAssignment] ADD CONSTRAINT [FK_EquipmentAssignment_User_ResponsibleUserId] FOREIGN KEY ([ResponsibleUserId]) REFERENCES [access].[User] ([Id])")]
    [InlineData("ALTER TABLE [fleet].[EquipmentCost] ADD CONSTRAINT [FK_EquipmentCost_Project_ProjectId] FOREIGN KEY ([ProjectId]) REFERENCES [projects].[Project] ([Id])")]
    [InlineData("ALTER TABLE [fleet].[EquipmentCost] ADD CONSTRAINT [FK_EquipmentCost_Supplier_SupplierId] FOREIGN KEY ([SupplierId]) REFERENCES [projects].[Supplier] ([Id])")]
    [InlineData("ALTER TABLE [fleet].[EquipmentCost] ADD CONSTRAINT [FK_EquipmentCost_ProjectItem_Project_Item] FOREIGN KEY ([ProjectId], [ProjectItemId]) REFERENCES [projects].[ProjectItem] ([ProjectId], [Id])")]
    [InlineData("ALTER TABLE [fleet].[EquipmentRental] ADD CONSTRAINT [FK_EquipmentRental_Supplier_SupplierId] FOREIGN KEY ([SupplierId]) REFERENCES [projects].[Supplier] ([Id])")]
    [InlineData("[fleet].[__EFMigrationsHistory]")]
    [InlineData("ALTER TABLE [fleet].[VehicleDriverAssignment] ADD CONSTRAINT [FK_VehicleDriverAssignment_Employee_DriverEmployeeId] FOREIGN KEY ([DriverEmployeeId]) REFERENCES [hr].[Employee] ([Id])")]
    [InlineData("ALTER TABLE [fleet].[VehicleCustodyEntry] ADD CONSTRAINT [FK_VehicleCustodyEntry_Employee_DriverEmployeeId] FOREIGN KEY ([DriverEmployeeId]) REFERENCES [hr].[Employee] ([Id])")]
    [InlineData("ALTER TABLE [fleet].[VehicleExpense] ADD CONSTRAINT [FK_VehicleExpense_Employee_DriverEmployeeId] FOREIGN KEY ([DriverEmployeeId]) REFERENCES [hr].[Employee] ([Id])")]
    [InlineData("ALTER TABLE [fleet].[VehicleExpense] ADD CONSTRAINT [FK_VehicleExpense_Project_ProjectId] FOREIGN KEY ([ProjectId]) REFERENCES [projects].[Project] ([Id])")]
    [InlineData("ALTER TABLE [fleet].[VehicleExpense] ADD CONSTRAINT [FK_VehicleExpense_Supplier_SupplierId] FOREIGN KEY ([SupplierId]) REFERENCES [projects].[Supplier] ([Id])")]
    [InlineData("ALTER TABLE [fleet].[VehicleViolation] ADD CONSTRAINT [FK_VehicleViolation_Employee_DriverEmployeeId] FOREIGN KEY ([DriverEmployeeId]) REFERENCES [hr].[Employee] ([Id])")]
    [InlineData("ALTER TABLE [fleet].[VehicleExpenseAlert] ADD CONSTRAINT [FK_VehicleExpenseAlert_User_RecipientUserId] FOREIGN KEY ([RecipientUserId]) REFERENCES [access].[User] ([Id])")]
    public void Migration_adds_the_foreign_keys_to_other_modules(string expected)
    {
        using var db = CreateContext();

        Assert.Contains(expected, db.GetService<IMigrator>().GenerateScript());
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
        Assert.DoesNotContain("CASCADE", db.GetService<IMigrator>().GenerateScript());
    }
}
