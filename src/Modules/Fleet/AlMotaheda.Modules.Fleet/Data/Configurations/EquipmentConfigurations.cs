using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Fleet.Data.Configurations;

internal sealed class EquipmentConfiguration : IEntityTypeConfiguration<Equipment>
{
    /// <summary>One machine per asset number. A lost race on it answers 409 <c>asset_no_taken</c>.</summary>
    public const string AssetNoIndex = "UX_Equipment_AssetNo";

    public const int AssetNoMaxLength = 30;
    public const int NameMaxLength = 200;
    public const int TypeNameMaxLength = 100;
    public const int SerialNoMaxLength = 60;
    public const int NotesMaxLength = 1000;

    public void Configure(EntityTypeBuilder<Equipment> builder)
    {
        builder.ToTable(nameof(Equipment), table =>
        {
            table.HasCheckConstraint("CK_Equipment_Category", $"[Category] IN ({SqlList.Of(Enum.GetNames<EquipmentCategory>())})");
            table.HasCheckConstraint("CK_Equipment_Ownership", $"[Ownership] IN ({SqlList.Of(Enum.GetNames<EquipmentOwnership>())})");
            table.HasCheckConstraint("CK_Equipment_Condition", $"[Condition] IN ({SqlList.Of(Enum.GetNames<EquipmentCondition>())})");
            table.HasCheckConstraint("CK_Equipment_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<EquipmentStatus>())})");
        });

        builder.Property(equipment => equipment.AssetNo).HasMaxLength(AssetNoMaxLength);
        builder.Property(equipment => equipment.NameAr).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(equipment => equipment.NameEn).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(equipment => equipment.TypeName).HasMaxLength(TypeNameMaxLength).IsUnicode();
        builder.Property(equipment => equipment.SerialNo).HasMaxLength(SerialNoMaxLength).IsUnicode();
        builder.Property(equipment => equipment.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(equipment => equipment.Category).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(equipment => equipment.Ownership).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(equipment => equipment.Condition).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(equipment => equipment.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(equipment => equipment.RowVersion).IsRowVersion();

        builder.HasIndex(equipment => equipment.AssetNo).IsUnique().HasDatabaseName(AssetNoIndex);

        // The register's filters.
        builder.HasIndex(equipment => new { equipment.Status, equipment.Category }, "IX_Equipment_Status_Category");
    }
}

internal sealed class EquipmentRentalConfiguration : IEntityTypeConfiguration<EquipmentRental>
{
    /// <summary>At most one set of current terms per machine.</summary>
    public const string OpenTermsIndex = "UX_EquipmentRental_Open";

    /// <summary>The lessor is a <c>projects.Supplier</c>. Written by hand in the migration (another module's table).</summary>
    public const string SupplierForeignKey = "FK_EquipmentRental_Supplier_SupplierId";

    public const int NotesMaxLength = 500;

    public void Configure(EntityTypeBuilder<EquipmentRental> builder)
    {
        builder.ToTable(nameof(EquipmentRental), table =>
        {
            table.HasCheckConstraint("CK_EquipmentRental_RateBasis", $"[RateBasis] IN ({SqlList.Of(Enum.GetNames<RateBasis>())})");
            table.HasCheckConstraint("CK_EquipmentRental_Rate", "[Rate] > 0");
            table.HasCheckConstraint("CK_EquipmentRental_Dates", "[EndDate] IS NULL OR [EndDate] >= [StartDate]");
        });

        builder.Property(rental => rental.RateBasis).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(rental => rental.Notes).HasMaxLength(NotesMaxLength).IsUnicode();

        builder.HasIndex(rental => rental.EquipmentId)
            .IsUnique()
            .HasFilter("[EndDate] IS NULL")
            .HasDatabaseName(OpenTermsIndex);

        builder.HasIndex(rental => rental.EquipmentId, "IX_EquipmentRental_EquipmentId");
        builder.HasIndex(rental => rental.SupplierId, "IX_EquipmentRental_SupplierId");

        builder.HasOne(rental => rental.Equipment)
            .WithMany()
            .HasForeignKey(rental => rental.EquipmentId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class EquipmentAssignmentConfiguration : IEntityTypeConfiguration<EquipmentAssignment>
{
    /// <summary>A machine is on one project at a time: one open assignment per machine.</summary>
    public const string OpenAssignmentIndex = "UX_EquipmentAssignment_Open";

    /// <summary>Written by hand in the migration: the project is a <c>projects.Project</c>.</summary>
    public const string ProjectForeignKey = "FK_EquipmentAssignment_Project_ProjectId";

    /// <summary>Written by hand in the migration: the responsible person is an <c>access.User</c>.</summary>
    public const string ResponsibleUserForeignKey = "FK_EquipmentAssignment_User_ResponsibleUserId";

    public const int OperatorNameMaxLength = 200;
    public const int NotesMaxLength = 500;

    public void Configure(EntityTypeBuilder<EquipmentAssignment> builder)
    {
        builder.ToTable(nameof(EquipmentAssignment), table =>
        {
            table.HasCheckConstraint("CK_EquipmentAssignment_Dates", "[EndDate] IS NULL OR [EndDate] >= [StartDate]");

            // An end is recorded whole: the date, when and by whom, or nothing at all.
            table.HasCheckConstraint(
                "CK_EquipmentAssignment_End",
                "([EndDate] IS NULL AND [EndedAt] IS NULL AND [EndedBy] IS NULL) "
                + "OR ([EndDate] IS NOT NULL AND [EndedAt] IS NOT NULL AND [EndedBy] IS NOT NULL)");
        });

        builder.Property(assignment => assignment.OperatorName).HasMaxLength(OperatorNameMaxLength).IsUnicode();
        builder.Property(assignment => assignment.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(assignment => assignment.RowVersion).IsRowVersion();

        builder.HasIndex(assignment => assignment.EquipmentId)
            .IsUnique()
            .HasFilter("[EndDate] IS NULL")
            .HasDatabaseName(OpenAssignmentIndex);

        // A machine's history, and a project's machines (now and before).
        builder.HasIndex(assignment => new { assignment.EquipmentId, assignment.StartDate }, "IX_EquipmentAssignment_Equipment_Start");
        builder.HasIndex(assignment => new { assignment.ProjectId, assignment.EndDate }, "IX_EquipmentAssignment_Project_End");
        builder.HasIndex(assignment => assignment.ResponsibleUserId, "IX_EquipmentAssignment_ResponsibleUserId");

        builder.HasOne(assignment => assignment.Equipment)
            .WithMany()
            .HasForeignKey(assignment => assignment.EquipmentId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class EquipmentCostConfiguration : IEntityTypeConfiguration<EquipmentCost>
{
    /// <summary>Written by hand in the migration: the project is a <c>projects.Project</c>.</summary>
    public const string ProjectForeignKey = "FK_EquipmentCost_Project_ProjectId";

    /// <summary>Written by hand in the migration: the supplier is a <c>projects.Supplier</c>.</summary>
    public const string SupplierForeignKey = "FK_EquipmentCost_Supplier_SupplierId";

    public const int DescriptionMaxLength = 500;
    public const int InvoiceNoMaxLength = 50;
    public const int ReviewNoteMaxLength = 500;
    public const int PaymentReferenceMaxLength = 100;
    public const int VoidReasonMaxLength = 300;

    public void Configure(EntityTypeBuilder<EquipmentCost> builder)
    {
        builder.ToTable(nameof(EquipmentCost), table =>
        {
            table.HasCheckConstraint("CK_EquipmentCost_Type", $"[Type] IN ({SqlList.Of(Enum.GetNames<EquipmentCostType>())})");
            table.HasCheckConstraint("CK_EquipmentCost_Basis", $"[Basis] IS NULL OR [Basis] IN ({SqlList.Of(Enum.GetNames<CostBasis>())})");
            table.HasCheckConstraint("CK_EquipmentCost_Channel", $"[Channel] IN ({SqlList.Of(Enum.GetNames<CostChannel>())})");
            table.HasCheckConstraint("CK_EquipmentCost_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<EquipmentCostStatus>())})");
            table.HasCheckConstraint(
                "CK_EquipmentCost_PaymentMethod",
                $"[PaymentMethod] IS NULL OR [PaymentMethod] IN ({SqlList.Of(Enum.GetNames<PaymentMethod>())})");
            table.HasCheckConstraint("CK_EquipmentCost_Amount", "[Amount] > 0");

            // Fuel has litres and a price per litre, and its amount is their product. Every other type has a basis:
            // a lump sum has no quantity or rate; a day, month or hour basis has both, and the amount is their
            // product. Every column is tested with IS (NOT) NULL first: a CHECK passes when its result is unknown.
            table.HasCheckConstraint(
                "CK_EquipmentCost_TypeColumns",
                "([Type] = 'Fuel' AND [Liters] IS NOT NULL AND [PricePerLiter] IS NOT NULL AND [Liters] > 0 AND [PricePerLiter] > 0 "
                + "AND [Basis] IS NULL AND [Quantity] IS NULL AND [Rate] IS NULL "
                + "AND ABS([Amount] - ROUND([Liters] * [PricePerLiter], 2)) < 0.01) "
                + "OR ([Type] <> 'Fuel' AND [Liters] IS NULL AND [PricePerLiter] IS NULL AND [Basis] IS NOT NULL "
                + "AND (([Basis] = 'Lump' AND [Quantity] IS NULL AND [Rate] IS NULL) "
                + "OR ([Basis] <> 'Lump' AND [Quantity] IS NOT NULL AND [Rate] IS NOT NULL AND [Quantity] > 0 AND [Rate] > 0 "
                + "AND ABS([Amount] - ROUND([Quantity] * [Rate], 2)) < 0.01)))");

            // Renting and buying on credit both mean owing someone: the supplier must be known.
            table.HasCheckConstraint("CK_EquipmentCost_RentalSupplier", "[Type] <> 'Rental' OR [SupplierId] IS NOT NULL");
            table.HasCheckConstraint("CK_EquipmentCost_CreditSupplier", "[Channel] = 'Custody' OR [SupplierId] IS NOT NULL");

            // A review is recorded exactly when the row has left Recorded.
            table.HasCheckConstraint(
                "CK_EquipmentCost_Review",
                "([Status] = 'Recorded' AND [ReviewedAt] IS NULL AND [ReviewedBy] IS NULL) "
                + "OR ([Status] <> 'Recorded' AND [ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL)");

            // A payment is recorded whole, and only on an approved credit cost.
            table.HasCheckConstraint(
                "CK_EquipmentCost_Payment",
                "([PaidOn] IS NULL AND [PaymentMethod] IS NULL AND [PaymentReference] IS NULL AND [PaymentRecordedAt] IS NULL AND [PaymentRecordedBy] IS NULL) "
                + "OR ([PaidOn] IS NOT NULL AND [PaymentMethod] IS NOT NULL AND [PaymentRecordedAt] IS NOT NULL AND [PaymentRecordedBy] IS NOT NULL "
                + "AND [Status] = 'Approved' AND [Channel] = 'Credit')");

            table.HasCheckConstraint(
                "CK_EquipmentCost_PaymentVoid",
                "([PaymentVoidedAt] IS NULL AND [PaymentVoidedBy] IS NULL AND [PaymentVoidReason] IS NULL) "
                + "OR ([PaymentVoidedAt] IS NOT NULL AND [PaymentVoidedBy] IS NOT NULL AND [PaymentVoidReason] IS NOT NULL)");
        });

        builder.Property(cost => cost.Type).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(cost => cost.Basis).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(cost => cost.Channel).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(cost => cost.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(cost => cost.PaymentMethod).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(cost => cost.Quantity).HasPrecision(18, 3);
        builder.Property(cost => cost.Liters).HasPrecision(18, 3);
        builder.Property(cost => cost.Description).HasMaxLength(DescriptionMaxLength).IsUnicode();
        builder.Property(cost => cost.InvoiceNo).HasMaxLength(InvoiceNoMaxLength).IsUnicode();
        builder.Property(cost => cost.ReviewNote).HasMaxLength(ReviewNoteMaxLength).IsUnicode();
        builder.Property(cost => cost.PaymentReference).HasMaxLength(PaymentReferenceMaxLength).IsUnicode();
        builder.Property(cost => cost.PaymentVoidReason).HasMaxLength(VoidReasonMaxLength).IsUnicode();
        builder.Property(cost => cost.RowVersion).IsRowVersion();

        // The lists of a project: by date, and by status for the review queue.
        builder.HasIndex(cost => new { cost.ProjectId, cost.CostDate }, "IX_EquipmentCost_Project_Date");
        builder.HasIndex(cost => new { cost.ProjectId, cost.Status }, "IX_EquipmentCost_Project_Status");
        builder.HasIndex(cost => cost.EquipmentId, "IX_EquipmentCost_EquipmentId");
        builder.HasIndex(cost => cost.SupplierId, "IX_EquipmentCost_SupplierId");

        builder.HasOne(cost => cost.Equipment)
            .WithMany()
            .HasForeignKey(cost => cost.EquipmentId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class VoidedEquipmentCostPaymentConfiguration : IEntityTypeConfiguration<VoidedEquipmentCostPayment>
{
    public void Configure(EntityTypeBuilder<VoidedEquipmentCostPayment> builder)
    {
        builder.ToTable(nameof(VoidedEquipmentCostPayment), table =>
            table.HasCheckConstraint(
                "CK_VoidedEquipmentCostPayment_PaymentMethod",
                $"[PaymentMethod] IN ({SqlList.Of(Enum.GetNames<PaymentMethod>())})"));

        builder.Property(payment => payment.PaymentMethod).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(payment => payment.PaymentReference).HasMaxLength(EquipmentCostConfiguration.PaymentReferenceMaxLength).IsUnicode();
        builder.Property(payment => payment.VoidReason).HasMaxLength(EquipmentCostConfiguration.VoidReasonMaxLength).IsUnicode();

        builder.HasIndex(payment => payment.CostId, "IX_VoidedEquipmentCostPayment_CostId");

        builder.HasOne(payment => payment.Cost)
            .WithMany()
            .HasForeignKey(payment => payment.CostId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
