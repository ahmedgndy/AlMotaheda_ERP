using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Subcontractors.Data.Configurations;

internal sealed class SubcontractorConfiguration : IEntityTypeConfiguration<Subcontractor>
{
    /// <summary>Unique index on the English name. A lost race on it is answered with 409 <c>subcontractor_exists</c>.</summary>
    public const string NameIndex = "UX_Subcontractor_NameEn";

    public const int NameMaxLength = 200;
    public const int ContactMaxLength = 200;
    public const int PhoneMaxLength = 30;
    public const int EmailMaxLength = 200;
    public const int AddressMaxLength = 400;
    public const int NotesMaxLength = 1000;

    public void Configure(EntityTypeBuilder<Subcontractor> builder)
    {
        builder.ToTable(nameof(Subcontractor), table =>
        {
            table.HasCheckConstraint("CK_Subcontractor_Specialty", $"[Specialty] IN ({SqlList.Of(Enum.GetNames<SubcontractorSpecialty>())})");
            table.HasCheckConstraint("CK_Subcontractor_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<SubcontractorStatus>())})");
        });

        builder.Property(company => company.NameAr).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(company => company.NameEn).HasMaxLength(NameMaxLength).IsUnicode();
        builder.HasIndex(company => company.NameEn).IsUnique().HasDatabaseName(NameIndex);

        builder.Property(company => company.Specialty).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(company => company.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(company => company.ContactName).HasMaxLength(ContactMaxLength).IsUnicode();
        builder.Property(company => company.Phone).HasMaxLength(PhoneMaxLength);
        builder.Property(company => company.Email).HasMaxLength(EmailMaxLength);
        builder.Property(company => company.Address).HasMaxLength(AddressMaxLength).IsUnicode();
        builder.Property(company => company.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(company => company.RowVersion).IsRowVersion();
    }
}

internal sealed class SubContractConfiguration : IEntityTypeConfiguration<SubContract>
{
    /// <summary>One contract per company per project. A lost race is answered with 409 <c>already_contracted</c>.</summary>
    public const string ProjectCompanyIndex = "UX_SubContract_Project_Subcontractor";

    /// <summary>A contract number is used once. A lost race is answered with 409 <c>contract_no_taken</c>.</summary>
    public const string ContractNoIndex = "UX_SubContract_ContractNo";

    /// <summary>The foreign key to <c>projects.Project</c>. Added by hand in the migration: Projects owns that table.</summary>
    public const string ProjectForeignKey = "FK_SubContract_Project_ProjectId";

    public const int ContractNoMaxLength = 50;
    public const int ScopeMaxLength = 1000;

    public void Configure(EntityTypeBuilder<SubContract> builder)
    {
        builder.ToTable(nameof(SubContract), table =>
        {
            table.HasCheckConstraint("CK_SubContract_Model", $"[Model] IN ({SqlList.Of(Enum.GetNames<ContractModel>())})");
            table.HasCheckConstraint("CK_SubContract_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<ContractStatus>())})");
            table.HasCheckConstraint("CK_SubContract_Value", "[Value] > 0");
            table.HasCheckConstraint("CK_SubContract_Retention", "[RetentionPercent] >= 0 AND [RetentionPercent] <= 100");
            table.HasCheckConstraint("CK_SubContract_Dates", "[EndDate] IS NULL OR [EndDate] >= [StartDate]");

            // A daily rate exists exactly on a daily-rate contract. IS (NOT) NULL first: a CHECK passes when unknown.
            table.HasCheckConstraint(
                "CK_SubContract_DailyRate",
                "([Model] = 'DailyRate' AND [DailyRate] IS NOT NULL AND [DailyRate] > 0) "
                + "OR ([Model] <> 'DailyRate' AND [DailyRate] IS NULL)");
        });

        builder.Property(contract => contract.Model).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(contract => contract.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(contract => contract.ContractNo).HasMaxLength(ContractNoMaxLength).IsUnicode();
        builder.Property(contract => contract.RetentionPercent).HasPrecision(5, 2);
        builder.Property(contract => contract.Scope).HasMaxLength(ScopeMaxLength).IsUnicode();
        builder.Property(contract => contract.RowVersion).IsRowVersion();

        builder.HasIndex(contract => new { contract.ProjectId, contract.SubcontractorId }).IsUnique().HasDatabaseName(ProjectCompanyIndex);
        builder.HasIndex(contract => contract.ContractNo).IsUnique().HasFilter("[ContractNo] IS NOT NULL").HasDatabaseName(ContractNoIndex);

        // A company's contracts across projects.
        builder.HasIndex(contract => contract.SubcontractorId, "IX_SubContract_SubcontractorId");

        builder.HasOne(contract => contract.Subcontractor)
            .WithMany()
            .HasForeignKey(contract => contract.SubcontractorId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasMany(contract => contract.Items)
            .WithOne(item => item.Contract)
            .HasForeignKey(item => item.ContractId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class ContractItemConfiguration : IEntityTypeConfiguration<ContractItem>
{
    /// <summary>The foreign key to <c>projects.ProjectItem</c>. Added by hand in the migration: Projects owns that table.</summary>
    public const string ProjectItemForeignKey = "FK_ContractItem_ProjectItem_ProjectItemId";

    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 500;
    public const int NotesMaxLength = 1000;

    public void Configure(EntityTypeBuilder<ContractItem> builder)
    {
        builder.ToTable(nameof(ContractItem), table =>
        {
            table.HasCheckConstraint("CK_ContractItem_WorkType", $"[WorkType] IN ({SqlList.Of(WorkTypes.All)})");
            table.HasCheckConstraint("CK_ContractItem_Unit", $"[Unit] IN ({SqlList.Of(MeasurementUnits.All)})");
            table.HasCheckConstraint("CK_ContractItem_Quantity", "[Quantity] > 0");
            table.HasCheckConstraint("CK_ContractItem_UnitPrice", "[UnitPrice] > 0");
        });

        builder.Property(item => item.Name).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(item => item.WorkType).HasMaxLength(30);
        builder.Property(item => item.Unit).HasMaxLength(20);
        builder.Property(item => item.Quantity).HasPrecision(18, 3);
        builder.Property(item => item.Description).HasMaxLength(DescriptionMaxLength).IsUnicode();
        builder.Property(item => item.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(item => item.RowVersion).IsRowVersion();

        builder.HasIndex(item => item.ContractId, "IX_ContractItem_ContractId");

        // "What is linked to this project item?" (the items screens and the item delete check).
        builder.HasIndex(item => item.ProjectItemId, "IX_ContractItem_ProjectItemId");
    }
}

/// <summary>The review columns shared by measurements and manpower sheets.</summary>
internal static class WorkReview
{
    public const int NoteMaxLength = 500;
    public const int ReviewNoteMaxLength = 500;

    /// <summary>
    /// A review is recorded exactly when the row has left Recorded, and a rejection always says why. Every column
    /// is tested with IS (NOT) NULL: a CHECK passes when its result is unknown.
    /// </summary>
    public static string ReviewCheck =>
        "([Status] = 'Recorded' AND [ReviewedAt] IS NULL AND [ReviewedBy] IS NULL AND [ReviewNote] IS NULL) "
        + "OR ([Status] = 'Approved' AND [ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL) "
        + "OR ([Status] = 'Rejected' AND [ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL AND [ReviewNote] IS NOT NULL)";
}

internal sealed class ProgressMeasurementConfiguration : IEntityTypeConfiguration<ProgressMeasurement>
{
    public void Configure(EntityTypeBuilder<ProgressMeasurement> builder)
    {
        builder.ToTable(nameof(ProgressMeasurement), table =>
        {
            table.HasCheckConstraint("CK_ProgressMeasurement_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<WorkStatus>())})");
            table.HasCheckConstraint("CK_ProgressMeasurement_Quantity", "[Quantity] > 0");
            table.HasCheckConstraint("CK_ProgressMeasurement_UnitPrice", "[UnitPrice] > 0");

            // The amount is the server's product of quantity and price, never a typed number.
            table.HasCheckConstraint(
                "CK_ProgressMeasurement_Amount",
                "[Amount] > 0 AND ABS([Amount] - ROUND([Quantity] * [UnitPrice], 2)) < 0.01");
            table.HasCheckConstraint("CK_ProgressMeasurement_Review", WorkReview.ReviewCheck);
        });

        builder.Property(measurement => measurement.Quantity).HasPrecision(18, 3);
        builder.Property(measurement => measurement.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(measurement => measurement.Note).HasMaxLength(WorkReview.NoteMaxLength).IsUnicode();
        builder.Property(measurement => measurement.ReviewNote).HasMaxLength(WorkReview.ReviewNoteMaxLength).IsUnicode();
        builder.Property(measurement => measurement.RowVersion).IsRowVersion();

        builder.HasIndex(measurement => new { measurement.ContractItemId, measurement.Status }, "IX_ProgressMeasurement_Item_Status");

        builder.HasOne(measurement => measurement.ContractItem)
            .WithMany()
            .HasForeignKey(measurement => measurement.ContractItemId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class ManpowerSheetConfiguration : IEntityTypeConfiguration<ManpowerSheet>
{
    /// <summary>The foreign key to <c>projects.ProjectItem</c>. Added by hand in the migration: Projects owns that table.</summary>
    public const string ProjectItemForeignKey = "FK_ManpowerSheet_ProjectItem_ProjectItemId";

    public void Configure(EntityTypeBuilder<ManpowerSheet> builder)
    {
        builder.ToTable(nameof(ManpowerSheet), table =>
        {
            table.HasCheckConstraint("CK_ManpowerSheet_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<WorkStatus>())})");
            table.HasCheckConstraint("CK_ManpowerSheet_WorkType", $"[WorkType] IN ({SqlList.Of(WorkTypes.All)})");
            table.HasCheckConstraint("CK_ManpowerSheet_Dates", "[ToDate] >= [FromDate]");
            table.HasCheckConstraint("CK_ManpowerSheet_Crew", "[Workers] > 0 AND [Days] > 0 AND [DailyRate] > 0");
            table.HasCheckConstraint(
                "CK_ManpowerSheet_Amount",
                "[Amount] > 0 AND ABS([Amount] - ROUND([Workers] * [Days] * [DailyRate], 2)) < 0.01");
            table.HasCheckConstraint("CK_ManpowerSheet_Review", WorkReview.ReviewCheck);
        });

        builder.Property(sheet => sheet.Days).HasPrecision(18, 3);
        builder.Property(sheet => sheet.WorkType).HasMaxLength(30);
        builder.Property(sheet => sheet.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(sheet => sheet.Note).HasMaxLength(WorkReview.NoteMaxLength).IsUnicode();
        builder.Property(sheet => sheet.ReviewNote).HasMaxLength(WorkReview.ReviewNoteMaxLength).IsUnicode();
        builder.Property(sheet => sheet.RowVersion).IsRowVersion();

        builder.HasIndex(sheet => new { sheet.ContractId, sheet.Status }, "IX_ManpowerSheet_Contract_Status");
        builder.HasIndex(sheet => sheet.ProjectItemId, "IX_ManpowerSheet_ProjectItemId");

        builder.HasOne(sheet => sheet.Contract)
            .WithMany()
            .HasForeignKey(sheet => sheet.ContractId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class SubPaymentConfiguration : IEntityTypeConfiguration<SubPayment>
{
    /// <summary>One payment per client key. A lost race on it is answered by returning the first payment.</summary>
    public const string IdempotencyKeyIndex = "UX_SubPayment_IdempotencyKey";

    public const int ReferenceMaxLength = 100;
    public const int NotesMaxLength = 500;
    public const int VoidReasonMaxLength = 300;

    public void Configure(EntityTypeBuilder<SubPayment> builder)
    {
        builder.ToTable(nameof(SubPayment), table =>
        {
            table.HasCheckConstraint("CK_SubPayment_Method", $"[Method] IN ({SqlList.Of(Enum.GetNames<SubPaymentMethod>())})");
            table.HasCheckConstraint("CK_SubPayment_Purpose", $"[Purpose] IN ({SqlList.Of(Enum.GetNames<SubPaymentPurpose>())})");
            table.HasCheckConstraint("CK_SubPayment_Amount", "[Amount] > 0");

            // A void is recorded whole: who, when and why, or nothing at all.
            table.HasCheckConstraint(
                "CK_SubPayment_Void",
                "([VoidedAt] IS NULL AND [VoidedBy] IS NULL AND [VoidReason] IS NULL) "
                + "OR ([VoidedAt] IS NOT NULL AND [VoidedBy] IS NOT NULL AND [VoidReason] IS NOT NULL)");
        });

        builder.Property(payment => payment.Method).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(payment => payment.Purpose).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(payment => payment.Reference).HasMaxLength(ReferenceMaxLength).IsUnicode();
        builder.Property(payment => payment.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(payment => payment.VoidReason).HasMaxLength(VoidReasonMaxLength).IsUnicode();
        builder.Property(payment => payment.RowVersion).IsRowVersion();

        builder.HasIndex(payment => payment.IdempotencyKey).IsUnique().HasDatabaseName(IdempotencyKeyIndex);
        builder.HasIndex(payment => new { payment.ContractId, payment.PaymentDate }, "IX_SubPayment_Contract_Date");

        builder.HasOne(payment => payment.Contract)
            .WithMany()
            .HasForeignKey(payment => payment.ContractId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasMany(payment => payment.Allocations)
            .WithOne(allocation => allocation.Payment)
            .HasForeignKey(allocation => allocation.PaymentId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class SubPaymentAllocationConfiguration : IEntityTypeConfiguration<SubPaymentAllocation>
{
    /// <summary>One allocation per payment and measurement.</summary>
    public const string PaymentMeasurementIndex = "UX_SubPaymentAllocation_Payment_Measurement";

    /// <summary>One allocation per payment and manpower sheet.</summary>
    public const string PaymentManpowerIndex = "UX_SubPaymentAllocation_Payment_Manpower";

    public void Configure(EntityTypeBuilder<SubPaymentAllocation> builder)
    {
        builder.ToTable(nameof(SubPaymentAllocation), table =>
        {
            table.HasCheckConstraint("CK_SubPaymentAllocation_Amount", "[Amount] > 0");

            // Exactly one piece of work. IS (NOT) NULL on both: a CHECK passes when its result is unknown.
            table.HasCheckConstraint(
                "CK_SubPaymentAllocation_Work",
                "([MeasurementId] IS NOT NULL AND [ManpowerSheetId] IS NULL) "
                + "OR ([MeasurementId] IS NULL AND [ManpowerSheetId] IS NOT NULL)");
        });

        builder.HasIndex(allocation => new { allocation.PaymentId, allocation.MeasurementId })
            .IsUnique()
            .HasFilter("[MeasurementId] IS NOT NULL")
            .HasDatabaseName(PaymentMeasurementIndex);

        builder.HasIndex(allocation => new { allocation.PaymentId, allocation.ManpowerSheetId })
            .IsUnique()
            .HasFilter("[ManpowerSheetId] IS NOT NULL")
            .HasDatabaseName(PaymentManpowerIndex);

        // "How much of this work is paid?" runs for every piece of work on the lists.
        builder.HasIndex(allocation => allocation.MeasurementId, "IX_SubPaymentAllocation_MeasurementId");
        builder.HasIndex(allocation => allocation.ManpowerSheetId, "IX_SubPaymentAllocation_ManpowerSheetId");

        builder.HasOne(allocation => allocation.Measurement)
            .WithMany()
            .HasForeignKey(allocation => allocation.MeasurementId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(allocation => allocation.ManpowerSheet)
            .WithMany()
            .HasForeignKey(allocation => allocation.ManpowerSheetId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
