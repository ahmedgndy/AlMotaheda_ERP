using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Fleet.Data.Configurations;

internal sealed class VehicleConfiguration : IEntityTypeConfiguration<Vehicle>
{
    /// <summary>One vehicle per plate (409 <c>plate_taken</c>).</summary>
    public const string PlateIndex = "UX_Vehicle_Plate";

    public const int PlateMaxLength = 20;
    public const int ProviderMaxLength = 50;
    public const int DeviceMaxLength = 100;
    public const int NotesMaxLength = 1000;

    public void Configure(EntityTypeBuilder<Vehicle> builder)
    {
        builder.ToTable(nameof(Vehicle), table =>
        {
            table.HasCheckConstraint("CK_Vehicle_Type", $"[Type] IN ({SqlList.Of(Enum.GetNames<VehicleType>())})");
            table.HasCheckConstraint("CK_Vehicle_FuelType", $"[FuelType] IN ({SqlList.Of(Enum.GetNames<VehicleFuelType>())})");
            table.HasCheckConstraint("CK_Vehicle_GpsStatus", $"[GpsStatus] IN ({SqlList.Of(Enum.GetNames<VehicleGpsStatus>())})");
            table.HasCheckConstraint("CK_Vehicle_Odometer", "[OdometerKm] >= 0");
            table.HasCheckConstraint("CK_Vehicle_ModelYear", "[ModelYear] IS NULL OR ([ModelYear] >= 1950 AND [ModelYear] <= 2100)");
            table.HasCheckConstraint("CK_Vehicle_Gps", "[GpsStatus] = 'NotConfigured' OR ([GpsProvider] IS NOT NULL AND [GpsDeviceId] IS NOT NULL)");
        });

        // The vehicle's key is the equipment's: one register row, extended.
        builder.Property(vehicle => vehicle.Id).ValueGeneratedNever();
        builder.HasOne(vehicle => vehicle.Equipment).WithOne().HasForeignKey<Vehicle>(vehicle => vehicle.Id).OnDelete(DeleteBehavior.NoAction);

        builder.Property(vehicle => vehicle.Plate).HasMaxLength(PlateMaxLength).IsUnicode();
        builder.Property(vehicle => vehicle.Type).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(vehicle => vehicle.FuelType).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(vehicle => vehicle.GpsStatus).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(vehicle => vehicle.OdometerKm).HasPrecision(18, 1);
        builder.Property(vehicle => vehicle.GpsProvider).HasMaxLength(ProviderMaxLength);
        builder.Property(vehicle => vehicle.GpsDeviceId).HasMaxLength(DeviceMaxLength);
        builder.Property(vehicle => vehicle.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(vehicle => vehicle.RowVersion).IsRowVersion();

        builder.HasIndex(vehicle => vehicle.Plate).IsUnique().HasDatabaseName(PlateIndex);
    }
}

internal sealed class VehicleDriverAssignmentConfiguration : IEntityTypeConfiguration<VehicleDriverAssignment>
{
    /// <summary>A vehicle has one driver at a time.</summary>
    public const string OpenIndex = "UX_VehicleDriverAssignment_Open";

    /// <summary>Written by hand in the migration: the driver is an <c>hr.Employee</c>.</summary>
    public const string DriverForeignKey = "FK_VehicleDriverAssignment_Employee_DriverEmployeeId";

    public const int NotesMaxLength = 500;

    public void Configure(EntityTypeBuilder<VehicleDriverAssignment> builder)
    {
        builder.ToTable(nameof(VehicleDriverAssignment), table =>
        {
            table.HasCheckConstraint("CK_VehicleDriverAssignment_Dates", "[EndDate] IS NULL OR [EndDate] >= [StartDate]");
            table.HasCheckConstraint(
                "CK_VehicleDriverAssignment_End",
                "([EndDate] IS NULL AND [EndedAt] IS NULL AND [EndedBy] IS NULL) OR ([EndDate] IS NOT NULL AND [EndedAt] IS NOT NULL AND [EndedBy] IS NOT NULL)");
            table.HasCheckConstraint("CK_VehicleDriverAssignment_Odometer", "([StartOdometerKm] IS NULL OR [StartOdometerKm] >= 0) AND ([EndOdometerKm] IS NULL OR [EndOdometerKm] >= 0)");
        });

        builder.Property(assignment => assignment.StartOdometerKm).HasPrecision(18, 1);
        builder.Property(assignment => assignment.EndOdometerKm).HasPrecision(18, 1);
        builder.Property(assignment => assignment.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(assignment => assignment.HandoverNote).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(assignment => assignment.RowVersion).IsRowVersion();

        builder.HasIndex(assignment => assignment.VehicleId).IsUnique().HasFilter("[EndDate] IS NULL").HasDatabaseName(OpenIndex);
        builder.HasIndex(assignment => new { assignment.VehicleId, assignment.StartDate }, "IX_VehicleDriverAssignment_Vehicle_Start");
        builder.HasIndex(assignment => new { assignment.DriverEmployeeId, assignment.StartDate }, "IX_VehicleDriverAssignment_Driver_Start");

        builder.HasOne(assignment => assignment.Vehicle).WithMany().HasForeignKey(assignment => assignment.VehicleId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class VehicleCustodyEntryConfiguration : IEntityTypeConfiguration<VehicleCustodyEntry>
{
    /// <summary>One entry per paying record: the same claim or transfer never moves the money twice.</summary>
    public const string SourceIndex = "UX_VehicleCustodyEntry_Source";

    public const string DriverForeignKey = "FK_VehicleCustodyEntry_Employee_DriverEmployeeId";

    public const int ReferenceMaxLength = 100;
    public const int NotesMaxLength = 500;

    public void Configure(EntityTypeBuilder<VehicleCustodyEntry> builder)
    {
        builder.ToTable(nameof(VehicleCustodyEntry), table =>
        {
            table.HasCheckConstraint("CK_VehicleCustodyEntry_Kind", $"[Kind] IN ({SqlList.Of(Enum.GetNames<VehicleCustodyKind>())})");
            table.HasCheckConstraint("CK_VehicleCustodyEntry_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<VehicleCustodyStatus>())})");
            table.HasCheckConstraint("CK_VehicleCustodyEntry_SourceType", $"[SourceType] IN ({SqlList.Of(VehicleCustodySources.All)})");
            table.HasCheckConstraint("CK_VehicleCustodyEntry_Amount", "[Amount] > 0");

            // Only a claim waits for review; fundings, returns and adjustments are final when written.
            table.HasCheckConstraint("CK_VehicleCustodyEntry_Pending", "[Status] = 'Posted' OR [Kind] = 'Expense'");
        });

        builder.Property(entry => entry.Kind).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(entry => entry.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(entry => entry.SourceType).HasMaxLength(30).IsUnicode(false);
        builder.Property(entry => entry.Reference).HasMaxLength(ReferenceMaxLength).IsUnicode();
        builder.Property(entry => entry.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(entry => entry.RowVersion).IsRowVersion();

        builder.HasIndex(entry => new { entry.SourceType, entry.SourceId }).IsUnique().HasDatabaseName(SourceIndex);
        builder.HasIndex(entry => new { entry.VehicleId, entry.EntryDate }, "IX_VehicleCustodyEntry_Vehicle_Date");
        builder.HasIndex(entry => entry.DriverEmployeeId, "IX_VehicleCustodyEntry_DriverEmployeeId");

        builder.HasOne(entry => entry.Vehicle).WithMany().HasForeignKey(entry => entry.VehicleId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class VehicleExpenseConfiguration : IEntityTypeConfiguration<VehicleExpense>
{
    public const string IdempotencyKeyIndex = "UX_VehicleExpense_IdempotencyKey";
    public const string DriverForeignKey = "FK_VehicleExpense_Employee_DriverEmployeeId";
    public const string ProjectForeignKey = "FK_VehicleExpense_Project_ProjectId";
    public const string SupplierForeignKey = "FK_VehicleExpense_Supplier_SupplierId";

    public const int VendorMaxLength = 200;
    public const int InvoiceNoMaxLength = 50;
    public const int NotesMaxLength = 1000;
    public const int NoteMaxLength = 500;
    public const int JustificationMaxLength = 500;

    public void Configure(EntityTypeBuilder<VehicleExpense> builder)
    {
        builder.ToTable(nameof(VehicleExpense), table =>
        {
            table.HasCheckConstraint("CK_VehicleExpense_Type", $"[Type] IN ({SqlList.Of(Enum.GetNames<VehicleExpenseType>())})");
            table.HasCheckConstraint("CK_VehicleExpense_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<VehicleExpenseStatus>())})");
            table.HasCheckConstraint("CK_VehicleExpense_PostingStatus", $"[PostingStatus] IN ({SqlList.Of(Enum.GetNames<VehiclePostingStatus>())})");
            table.HasCheckConstraint("CK_VehicleExpense_Amount", "[Amount] > 0");
            table.HasCheckConstraint("CK_VehicleExpense_Odometer", "[OdometerKm] IS NULL OR [OdometerKm] >= 0");
            table.HasCheckConstraint("CK_VehicleExpense_Round", "[Round] >= 1");

            // Fuel has litres and a price per litre, and its amount is their product; every other type has neither.
            table.HasCheckConstraint(
                "CK_VehicleExpense_Fuel",
                "([Type] = 'Fuel' AND [FuelQuantity] IS NOT NULL AND [UnitPrice] IS NOT NULL AND [FuelQuantity] > 0 AND [UnitPrice] > 0 "
                + "AND ABS([Amount] - ROUND([FuelQuantity] * [UnitPrice], 2)) < 0.01) "
                + "OR ([Type] <> 'Fuel' AND [FuelQuantity] IS NULL AND [UnitPrice] IS NULL)");

            // Posted exactly with its approval and its custody entry; never posted unless approved.
            table.HasCheckConstraint(
                "CK_VehicleExpense_Posting",
                "([PostingStatus] = 'NotPosted' AND [CustodyEntryId] IS NULL AND [PostedAt] IS NULL) "
                + "OR ([PostingStatus] IN ('Posted', 'Reversed') AND [Status] = 'Approved' AND [CustodyEntryId] IS NOT NULL AND [PostedAt] IS NOT NULL)");
            table.HasCheckConstraint("CK_VehicleExpense_Approved", "[Status] <> 'Approved' OR [PostingStatus] <> 'NotPosted'");
            table.HasCheckConstraint(
                "CK_VehicleExpense_Reversal",
                "([PostingStatus] <> 'Reversed' AND [ReversedAt] IS NULL AND [ReversedBy] IS NULL AND [ReversalReason] IS NULL) "
                + "OR ([PostingStatus] = 'Reversed' AND [ReversedAt] IS NOT NULL AND [ReversedBy] IS NOT NULL AND [ReversalReason] IS NOT NULL)");
            table.HasCheckConstraint("CK_VehicleExpense_Referral", "[HrReferralId] IS NULL OR [Status] = 'Rejected'");
            table.HasCheckConstraint("CK_VehicleExpense_Gps", "([GpsLatitude] IS NULL AND [GpsLongitude] IS NULL) OR ([GpsLatitude] IS NOT NULL AND [GpsLongitude] IS NOT NULL)");
        });

        builder.Property(expense => expense.Number)
            .HasMaxLength(20)
            .HasComputedColumnSql("CONCAT('VX-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true);
        builder.HasIndex(expense => expense.Number).IsUnique().HasDatabaseName("UX_VehicleExpense_Number");

        builder.Property(expense => expense.Type).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(expense => expense.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(expense => expense.PostingStatus).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(expense => expense.OdometerKm).HasPrecision(18, 1);
        builder.Property(expense => expense.FuelQuantity).HasPrecision(18, 3);
        builder.Property(expense => expense.VendorName).HasMaxLength(VendorMaxLength).IsUnicode();
        builder.Property(expense => expense.InvoiceNo).HasMaxLength(InvoiceNoMaxLength).IsUnicode();
        builder.Property(expense => expense.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(expense => expense.PriceCheck).HasMaxLength(30).IsUnicode(false);
        builder.Property(expense => expense.PriceJustification).HasMaxLength(JustificationMaxLength).IsUnicode();
        builder.Property(expense => expense.GpsLatitude).HasPrecision(9, 6);
        builder.Property(expense => expense.GpsLongitude).HasPrecision(9, 6);
        builder.Property(expense => expense.ReviewNote).HasMaxLength(NoteMaxLength).IsUnicode();
        builder.Property(expense => expense.ReversalReason).HasMaxLength(NoteMaxLength).IsUnicode();
        builder.Property(expense => expense.RowVersion).IsRowVersion();

        builder.HasIndex(expense => expense.IdempotencyKey).IsUnique().HasDatabaseName(IdempotencyKeyIndex);
        builder.HasIndex(expense => new { expense.VehicleId, expense.ExpenseDate }, "IX_VehicleExpense_Vehicle_Date");
        builder.HasIndex(expense => new { expense.Status, expense.Id }, "IX_VehicleExpense_Status");
        builder.HasIndex(expense => new { expense.ProjectId, expense.ExpenseDate }, "IX_VehicleExpense_Project_Date");
        builder.HasIndex(expense => expense.DriverEmployeeId, "IX_VehicleExpense_DriverEmployeeId");
        builder.HasIndex(expense => expense.InvoiceNo, "IX_VehicleExpense_InvoiceNo").HasFilter("[InvoiceNo] IS NOT NULL");
        builder.HasIndex(expense => expense.CustodyEntryId).IsUnique().HasFilter("[CustodyEntryId] IS NOT NULL").HasDatabaseName("UX_VehicleExpense_CustodyEntry");

        builder.HasOne(expense => expense.Vehicle).WithMany().HasForeignKey(expense => expense.VehicleId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<VehicleDriverAssignment>().WithMany().HasForeignKey(expense => expense.DriverAssignmentId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<VehicleCustodyEntry>().WithMany().HasForeignKey(expense => expense.CustodyEntryId).OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(expense => expense.Flags).WithOne().HasForeignKey(flag => flag.ExpenseId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class VehicleExpenseFlagConfiguration : IEntityTypeConfiguration<VehicleExpenseFlag>
{
    public const int DetailMaxLength = 300;

    public void Configure(EntityTypeBuilder<VehicleExpenseFlag> builder)
    {
        builder.ToTable(nameof(VehicleExpenseFlag), table =>
            table.HasCheckConstraint("CK_VehicleExpenseFlag_Kind", $"[Kind] IN ({SqlList.Of(Enum.GetNames<VehicleExpenseFlagKind>())})"));

        builder.Property(flag => flag.Kind).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(flag => flag.Detail).HasMaxLength(DetailMaxLength).IsUnicode();

        builder.HasIndex(flag => flag.ExpenseId, "IX_VehicleExpenseFlag_ExpenseId");
    }
}

internal sealed class VehicleExpenseAlertConfiguration : IEntityTypeConfiguration<VehicleExpenseAlert>
{
    public const string OnceIndex = "UX_VehicleExpenseAlert_Expense_Recipient";
    public const string RecipientForeignKey = "FK_VehicleExpenseAlert_User_RecipientUserId";

    public void Configure(EntityTypeBuilder<VehicleExpenseAlert> builder)
    {
        builder.ToTable(nameof(VehicleExpenseAlert));
        builder.HasIndex(alert => new { alert.ExpenseId, alert.RecipientUserId }).IsUnique().HasDatabaseName(OnceIndex);
        builder.HasOne(alert => alert.Expense).WithMany().HasForeignKey(alert => alert.ExpenseId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class FleetAttachmentConfiguration : IEntityTypeConfiguration<FleetAttachment>
{
    public const string KeyIndex = "UX_FleetAttachment_IdempotencyKey";
    public const string StorageKeyIndex = "UX_FleetAttachment_StorageKey";

    public const int FileNameMaxLength = 255;
    public const int ContentTypeMaxLength = 100;
    public const int StorageKeyMaxLength = 200;

    public void Configure(EntityTypeBuilder<FleetAttachment> builder)
    {
        builder.ToTable(nameof(FleetAttachment), table =>
        {
            table.HasCheckConstraint("CK_FleetAttachment_Purpose", $"[Purpose] IN ({SqlList.Of(Enum.GetNames<FleetAttachmentPurpose>())})");
            table.HasCheckConstraint("CK_FleetAttachment_Owner", $"([OwnerType] IS NULL AND [OwnerId] IS NULL) OR ([OwnerType] IN ({SqlList.Of(FleetAttachmentOwners.All)}) AND [OwnerId] IS NOT NULL)");
            table.HasCheckConstraint("CK_FleetAttachment_Size", "[SizeBytes] > 0");
            table.HasCheckConstraint("CK_FleetAttachment_Sha256", "LEN([Sha256]) = 64 AND [Sha256] COLLATE Latin1_General_BIN NOT LIKE '%[^0-9a-f]%'");
        });

        builder.Property(attachment => attachment.Purpose).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(attachment => attachment.OwnerType).HasMaxLength(30).IsUnicode(false);
        builder.Property(attachment => attachment.StorageKey).HasMaxLength(StorageKeyMaxLength);
        builder.Property(attachment => attachment.FileName).HasMaxLength(FileNameMaxLength).IsUnicode();
        builder.Property(attachment => attachment.ContentType).HasMaxLength(ContentTypeMaxLength);
        builder.Property(attachment => attachment.Sha256).HasMaxLength(64).IsFixedLength();

        builder.HasIndex(attachment => attachment.IdempotencyKey).IsUnique().HasDatabaseName(KeyIndex);
        builder.HasIndex(attachment => attachment.StorageKey).IsUnique().HasDatabaseName(StorageKeyIndex);
        builder.HasIndex(attachment => new { attachment.OwnerType, attachment.OwnerId }, "IX_FleetAttachment_Owner");
        builder.HasIndex(attachment => attachment.Sha256, "IX_FleetAttachment_Sha256");
    }
}

internal sealed class VehicleViolationConfiguration : IEntityTypeConfiguration<VehicleViolation>
{
    public const string DriverForeignKey = "FK_VehicleViolation_Employee_DriverEmployeeId";

    public const int DescriptionMaxLength = 1000;
    public const int TicketMaxLength = 50;
    public const int TripMaxLength = 100;
    public const int NoteMaxLength = 500;

    public void Configure(EntityTypeBuilder<VehicleViolation> builder)
    {
        builder.ToTable(nameof(VehicleViolation), table =>
        {
            table.HasCheckConstraint("CK_VehicleViolation_Category", $"[Category] IN ({SqlList.Of(Enum.GetNames<VehicleViolationCategory>())})");
            table.HasCheckConstraint("CK_VehicleViolation_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<VehicleViolationStatus>())})");
            table.HasCheckConstraint("CK_VehicleViolation_Amount", "[Amount] IS NULL OR [Amount] > 0");
            table.HasCheckConstraint(
                "CK_VehicleViolation_Review",
                "([Status] IN ('Recorded', 'UnderReview') AND [Resolution] IS NULL) OR ([Status] IN ('Resolved', 'Dismissed') AND [ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL)");
            table.HasCheckConstraint("CK_VehicleViolation_Gps", "([GpsLatitude] IS NULL AND [GpsLongitude] IS NULL) OR ([GpsLatitude] IS NOT NULL AND [GpsLongitude] IS NOT NULL)");
        });

        builder.Property(violation => violation.Number)
            .HasMaxLength(20)
            .HasComputedColumnSql("CONCAT('VV-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true);
        builder.HasIndex(violation => violation.Number).IsUnique().HasDatabaseName("UX_VehicleViolation_Number");

        builder.Property(violation => violation.Category).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(violation => violation.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(violation => violation.Description).HasMaxLength(DescriptionMaxLength).IsUnicode();
        builder.Property(violation => violation.TicketNo).HasMaxLength(TicketMaxLength).IsUnicode();
        builder.Property(violation => violation.TripReference).HasMaxLength(TripMaxLength).IsUnicode();
        builder.Property(violation => violation.GpsLatitude).HasPrecision(9, 6);
        builder.Property(violation => violation.GpsLongitude).HasPrecision(9, 6);
        builder.Property(violation => violation.ReviewNote).HasMaxLength(NoteMaxLength).IsUnicode();
        builder.Property(violation => violation.Resolution).HasMaxLength(NoteMaxLength).IsUnicode();
        builder.Property(violation => violation.RowVersion).IsRowVersion();

        builder.HasIndex(violation => new { violation.VehicleId, violation.ViolationDate }, "IX_VehicleViolation_Vehicle_Date");
        builder.HasIndex(violation => new { violation.Status, violation.Id }, "IX_VehicleViolation_Status");
        builder.HasIndex(violation => violation.DriverEmployeeId, "IX_VehicleViolation_DriverEmployeeId");

        builder.HasOne(violation => violation.Vehicle).WithMany().HasForeignKey(violation => violation.VehicleId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<VehicleDriverAssignment>().WithMany().HasForeignKey(violation => violation.DriverAssignmentId).OnDelete(DeleteBehavior.NoAction);
    }
}
