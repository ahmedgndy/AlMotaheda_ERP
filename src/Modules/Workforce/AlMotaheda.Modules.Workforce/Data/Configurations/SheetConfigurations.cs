using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Workforce.Data.Configurations;

internal sealed class DailySheetConfiguration : IEntityTypeConfiguration<DailySheet>
{
    /// <summary>One live sheet per engineer, project, day and shift (decision W1). A rejected sheet frees the slot.</summary>
    public const string RecorderDayIndex = "UX_DailySheet_Recorder_Day";

    /// <summary>The foreign key to <c>projects.Project</c>. Added by hand in the migration: Projects owns that table.</summary>
    public const string ProjectForeignKey = "FK_DailySheet_Project_ProjectId";

    public const int ReviewNoteMaxLength = 500;
    public const int NotesMaxLength = 1000;

    public void Configure(EntityTypeBuilder<DailySheet> builder)
    {
        builder.ToTable(nameof(DailySheet), table =>
        {
            table.HasCheckConstraint("CK_DailySheet_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<SheetStatus>())})");
            table.HasCheckConstraint("CK_DailySheet_Shift", $"[Shift] IN ({SqlList.Of(Enum.GetNames<Shift>())})");

            // A sheet that left Draft was submitted; a reviewed status has its review. Draft and Submitted may keep
            // the details of an earlier return, so the return note stays visible after a resubmit.
            table.HasCheckConstraint("CK_DailySheet_Submitted", "[Status] = 'Draft' OR [SubmittedAt] IS NOT NULL");
            table.HasCheckConstraint(
                "CK_DailySheet_Review",
                "[Status] IN ('Draft', 'Submitted') OR ([ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL)");
        });

        builder.Property(sheet => sheet.Shift).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(sheet => sheet.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(sheet => sheet.ReviewNote).HasMaxLength(ReviewNoteMaxLength).IsUnicode();
        builder.Property(sheet => sheet.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(sheet => sheet.RowVersion).IsRowVersion();

        builder.HasIndex(sheet => new { sheet.ProjectId, sheet.WorkDate, sheet.Shift, sheet.RecordedBy })
            .IsUnique()
            .HasFilter("[Status] <> 'Rejected'")
            .HasDatabaseName(RecorderDayIndex);

        // The sheets list of a project (by date) and the review queue (by status).
        builder.HasIndex(sheet => new { sheet.ProjectId, sheet.WorkDate }, "IX_DailySheet_Project_Date");
        builder.HasIndex(sheet => new { sheet.ProjectId, sheet.Status }, "IX_DailySheet_Project_Status");

        builder.HasMany(sheet => sheet.Lines)
            .WithOne(line => line.Sheet)
            .HasForeignKey(line => line.SheetId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class AttendanceLineConfiguration : IEntityTypeConfiguration<AttendanceLine>
{
    /// <summary>
    /// A worker is recorded once per day and shift, whatever the project or the engineer. Void lines (of rejected
    /// sheets) don't count, so the worker-day is free again.
    /// </summary>
    public const string WorkerShiftIndex = "UX_AttendanceLine_Worker_Shift";

    public const int NotesMaxLength = 300;

    public void Configure(EntityTypeBuilder<AttendanceLine> builder)
    {
        builder.ToTable(nameof(AttendanceLine), table =>
        {
            table.HasCheckConstraint("CK_AttendanceLine_Shift", $"[Shift] IN ({SqlList.Of(Enum.GetNames<Shift>())})");
            table.HasCheckConstraint("CK_AttendanceLine_WorkType", $"[WorkType] IN ({SqlList.Of(WorkTypes.All)})");
            table.HasCheckConstraint("CK_AttendanceLine_Days", "[Days] IN (0.5, 1)");
            table.HasCheckConstraint("CK_AttendanceLine_OvertimeHours", "[OvertimeHours] >= 0 AND [OvertimeHours] <= 12");

            // The pay is copied onto the line whole, at approval, or not at all. Every column is tested with
            // IS (NOT) NULL first: a CHECK passes when its result is unknown.
            table.HasCheckConstraint(
                "CK_AttendanceLine_Pay",
                "([AppliedDailyRate] IS NULL AND [BaseAmount] IS NULL AND [OvertimeAmount] IS NULL AND [AllowancesAmount] IS NULL "
                + "AND [DeductionsAmount] IS NULL AND [NetAmount] IS NULL) "
                + "OR ([AppliedDailyRate] IS NOT NULL AND [BaseAmount] IS NOT NULL AND [OvertimeAmount] IS NOT NULL "
                + "AND [AllowancesAmount] IS NOT NULL AND [DeductionsAmount] IS NOT NULL AND [NetAmount] IS NOT NULL "
                + "AND [AppliedDailyRate] > 0 AND [BaseAmount] >= 0 AND [OvertimeAmount] >= 0 AND [AllowancesAmount] >= 0 "
                + "AND [DeductionsAmount] >= 0 AND [NetAmount] >= 0 "
                + "AND [NetAmount] = [BaseAmount] + [OvertimeAmount] + [AllowancesAmount] - [DeductionsAmount])");
        });

        builder.Property(line => line.Shift).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(line => line.WorkType).HasMaxLength(30);
        builder.Property(line => line.Days).HasPrecision(2, 1);
        builder.Property(line => line.OvertimeHours).HasPrecision(4, 1);
        builder.Property(line => line.Notes).HasMaxLength(NotesMaxLength).IsUnicode();

        builder.HasIndex(line => new { line.WorkerId, line.WorkDate, line.Shift })
            .IsUnique()
            .HasFilter("[IsVoid] = 0")
            .HasDatabaseName(WorkerShiftIndex);

        // A worker's days in date order (payments settle the oldest first), void or not.
        builder.HasIndex(line => new { line.WorkerId, line.WorkDate }, "IX_AttendanceLine_Worker_Date");
        builder.HasIndex(line => line.SheetId, "IX_AttendanceLine_SheetId");
        builder.HasIndex(line => line.TradeId, "IX_AttendanceLine_TradeId");

        builder.HasOne(line => line.Worker)
            .WithMany()
            .HasForeignKey(line => line.WorkerId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(line => line.Trade)
            .WithMany()
            .HasForeignKey(line => line.TradeId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasMany(line => line.Adjustments)
            .WithOne(adjustment => adjustment.Line)
            .HasForeignKey(adjustment => adjustment.LineId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasMany(line => line.Allocations)
            .WithOne(allocation => allocation.Line)
            .HasForeignKey(allocation => allocation.LineId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class LineAdjustmentConfiguration : IEntityTypeConfiguration<LineAdjustment>
{
    public const int ReasonMaxLength = 300;

    public void Configure(EntityTypeBuilder<LineAdjustment> builder)
    {
        builder.ToTable(nameof(LineAdjustment), table =>
        {
            table.HasCheckConstraint("CK_LineAdjustment_Kind", $"[Kind] IN ({SqlList.Of(Enum.GetNames<AdjustmentKind>())})");
            table.HasCheckConstraint("CK_LineAdjustment_Type", $"[Type] IN ({SqlList.Of(Enum.GetNames<AdjustmentType>())})");
            table.HasCheckConstraint("CK_LineAdjustment_Amount", "[Amount] > 0");

            // Every deduction says why. Tested with IS NOT NULL: a CHECK passes when its result is unknown.
            table.HasCheckConstraint("CK_LineAdjustment_DeductionReason", "[Kind] = 'Allowance' OR [Reason] IS NOT NULL");
        });

        builder.Property(adjustment => adjustment.Kind).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(adjustment => adjustment.Type).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(adjustment => adjustment.Reason).HasMaxLength(ReasonMaxLength).IsUnicode();

        builder.HasIndex(adjustment => adjustment.LineId, "IX_LineAdjustment_LineId");
    }
}

internal sealed class LaborAllocationConfiguration : IEntityTypeConfiguration<LaborAllocation>
{
    /// <summary>An item appears once per line.</summary>
    public const string LineItemIndex = "UX_LaborAllocation_Line_Item";

    /// <summary>The foreign key to <c>projects.ProjectItem</c>. Added by hand in the migration: Projects owns that table.</summary>
    public const string ProjectItemForeignKey = "FK_LaborAllocation_ProjectItem_ProjectItemId";

    public void Configure(EntityTypeBuilder<LaborAllocation> builder)
    {
        builder.ToTable(nameof(LaborAllocation), table =>
        {
            table.HasCheckConstraint("CK_LaborAllocation_Portion", "[Portion] > 0 AND [Portion] <= 1");
            table.HasCheckConstraint("CK_LaborAllocation_Amount", "[Amount] IS NULL OR [Amount] >= 0");
        });

        builder.Property(allocation => allocation.Portion).HasPrecision(5, 4);

        builder.HasIndex(allocation => new { allocation.LineId, allocation.ProjectItemId }).IsUnique().HasDatabaseName(LineItemIndex);

        // "What labor is on this item?" and "may this item be deleted?".
        builder.HasIndex(allocation => allocation.ProjectItemId, "IX_LaborAllocation_ProjectItemId");
    }
}

internal sealed class WorkerPaymentConfiguration : IEntityTypeConfiguration<WorkerPayment>
{
    /// <summary>One payment per client key. A lost race on it is answered by returning the first payment.</summary>
    public const string IdempotencyKeyIndex = "UX_WorkerPayment_IdempotencyKey";

    public const int ReferenceMaxLength = 100;
    public const int NotesMaxLength = 500;
    public const int VoidReasonMaxLength = 300;

    public void Configure(EntityTypeBuilder<WorkerPayment> builder)
    {
        builder.ToTable(nameof(WorkerPayment), table =>
        {
            table.HasCheckConstraint("CK_WorkerPayment_Method", $"[Method] IN ({SqlList.Of(Enum.GetNames<WorkerPaymentMethod>())})");
            table.HasCheckConstraint("CK_WorkerPayment_Amount", "[Amount] > 0");

            // A void is recorded whole: who, when and why, or nothing at all.
            table.HasCheckConstraint(
                "CK_WorkerPayment_Void",
                "([VoidedAt] IS NULL AND [VoidedBy] IS NULL AND [VoidReason] IS NULL) "
                + "OR ([VoidedAt] IS NOT NULL AND [VoidedBy] IS NOT NULL AND [VoidReason] IS NOT NULL)");
        });

        builder.Property(payment => payment.Method).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(payment => payment.Reference).HasMaxLength(ReferenceMaxLength).IsUnicode();
        builder.Property(payment => payment.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(payment => payment.VoidReason).HasMaxLength(VoidReasonMaxLength).IsUnicode();
        builder.Property(payment => payment.RowVersion).IsRowVersion();

        builder.HasIndex(payment => payment.IdempotencyKey).IsUnique().HasDatabaseName(IdempotencyKeyIndex);

        // A worker's payments, newest first.
        builder.HasIndex(payment => new { payment.WorkerId, payment.PaymentDate }, "IX_WorkerPayment_Worker_Date");

        builder.HasOne(payment => payment.Worker)
            .WithMany()
            .HasForeignKey(payment => payment.WorkerId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasMany(payment => payment.Allocations)
            .WithOne(allocation => allocation.Payment)
            .HasForeignKey(allocation => allocation.PaymentId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class WorkerPaymentAllocationConfiguration : IEntityTypeConfiguration<WorkerPaymentAllocation>
{
    /// <summary>One allocation per payment and line.</summary>
    public const string PaymentLineIndex = "UX_WorkerPaymentAllocation_Payment_Line";

    public void Configure(EntityTypeBuilder<WorkerPaymentAllocation> builder)
    {
        builder.ToTable(nameof(WorkerPaymentAllocation), table =>
            table.HasCheckConstraint("CK_WorkerPaymentAllocation_Amount", "[Amount] > 0"));

        builder.HasIndex(allocation => new { allocation.PaymentId, allocation.LineId }).IsUnique().HasDatabaseName(PaymentLineIndex);

        // "How much of this day is paid?" runs for every line of every account and items screen.
        builder.HasIndex(allocation => allocation.LineId, "IX_WorkerPaymentAllocation_LineId");

        builder.HasOne(allocation => allocation.Line)
            .WithMany()
            .HasForeignKey(allocation => allocation.LineId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
