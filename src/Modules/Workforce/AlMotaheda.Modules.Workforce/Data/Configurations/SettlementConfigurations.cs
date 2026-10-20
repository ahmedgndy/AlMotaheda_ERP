using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Workforce.Data.Configurations;

internal sealed class PayrollSettlementConfiguration : IEntityTypeConfiguration<PayrollSettlement>
{
    public const string IdempotencyKeyIndex = "UX_PayrollSettlement_IdempotencyKey";
    public const string PaidByForeignKey = "FK_PayrollSettlement_User_PaidByUserId";
    public const string ProjectForeignKey = "FK_PayrollSettlement_Project_ProjectId";

    public const int ReferenceMaxLength = 100;
    public const int NotesMaxLength = 500;
    public const int ReasonMaxLength = 300;

    public void Configure(EntityTypeBuilder<PayrollSettlement> builder)
    {
        builder.ToTable(nameof(PayrollSettlement), table =>
        {
            table.HasCheckConstraint("CK_PayrollSettlement_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<SettlementStatus>())})");
            table.HasCheckConstraint("CK_PayrollSettlement_Method", $"[Method] IN ({SqlList.Of(Enum.GetNames<WorkerPaymentMethod>())})");
            table.HasCheckConstraint("CK_PayrollSettlement_Amount", "[Amount] > 0");
            table.HasCheckConstraint("CK_PayrollSettlement_Period", "[PeriodTo] >= [PeriodFrom]");
            table.HasCheckConstraint(
                "CK_PayrollSettlement_Reversed",
                "([ReversedAt] IS NULL AND [ReversedBy] IS NULL AND [ReverseReason] IS NULL) "
                + "OR ([ReversedAt] IS NOT NULL AND [ReversedBy] IS NOT NULL AND [ReverseReason] IS NOT NULL)");
        });

        builder.Property(settlement => settlement.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(settlement => settlement.Method).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(settlement => settlement.Amount).HasPrecision(18, 2);
        builder.Property(settlement => settlement.Reference).HasMaxLength(ReferenceMaxLength).IsUnicode();
        builder.Property(settlement => settlement.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(settlement => settlement.ReviewNote).HasMaxLength(ReasonMaxLength).IsUnicode();
        builder.Property(settlement => settlement.ReverseReason).HasMaxLength(ReasonMaxLength).IsUnicode();
        builder.Property(settlement => settlement.RowVersion).IsRowVersion();

        builder.HasIndex(settlement => settlement.IdempotencyKey).IsUnique().HasDatabaseName(IdempotencyKeyIndex);
        builder.HasIndex(settlement => new { settlement.ProjectId, settlement.Status, settlement.PaidOn }, "IX_PayrollSettlement_Project_Status_PaidOn");
        builder.HasIndex(settlement => settlement.PaidByUserId, "IX_PayrollSettlement_PaidByUserId");

        builder.HasMany(settlement => settlement.Lines)
            .WithOne(line => line.Settlement)
            .HasForeignKey(line => line.SettlementId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class PayrollSettlementLineConfiguration : IEntityTypeConfiguration<PayrollSettlementLine>
{
    /// <summary>One line per worker in a settlement.</summary>
    public const string WorkerIndex = "UX_PayrollSettlementLine_Settlement_Worker";

    public void Configure(EntityTypeBuilder<PayrollSettlementLine> builder)
    {
        builder.ToTable(nameof(PayrollSettlementLine), table => table.HasCheckConstraint("CK_PayrollSettlementLine_Amount", "[Amount] > 0"));

        builder.Property(line => line.Amount).HasPrecision(18, 2);
        builder.HasIndex(line => new { line.SettlementId, line.WorkerId }).IsUnique().HasDatabaseName(WorkerIndex);
        builder.HasIndex(line => line.WorkerId, "IX_PayrollSettlementLine_WorkerId");
        builder.HasIndex(line => line.PaymentId, "IX_PayrollSettlementLine_PaymentId");

        builder.HasOne(line => line.Worker).WithMany().HasForeignKey(line => line.WorkerId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(line => line.Payment).WithMany().HasForeignKey(line => line.PaymentId).OnDelete(DeleteBehavior.NoAction);

        builder.HasMany(line => line.Allocations)
            .WithOne(allocation => allocation.SettlementLine)
            .HasForeignKey(allocation => allocation.SettlementLineId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class PayrollSettlementAllocationConfiguration : IEntityTypeConfiguration<PayrollSettlementAllocation>
{
    public const string LineIndex = "UX_PayrollSettlementAllocation_SettlementLine_Line";

    public void Configure(EntityTypeBuilder<PayrollSettlementAllocation> builder)
    {
        builder.ToTable(nameof(PayrollSettlementAllocation), table => table.HasCheckConstraint("CK_PayrollSettlementAllocation_Amount", "[Amount] > 0"));

        builder.Property(allocation => allocation.Amount).HasPrecision(18, 2);
        builder.HasIndex(allocation => new { allocation.SettlementLineId, allocation.LineId }).IsUnique().HasDatabaseName(LineIndex);
        builder.HasIndex(allocation => allocation.LineId, "IX_PayrollSettlementAllocation_LineId");

        builder.HasOne(allocation => allocation.Line).WithMany().HasForeignKey(allocation => allocation.LineId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class SettlementAlertConfiguration : IEntityTypeConfiguration<SettlementAlert>
{
    public const string OnceIndex = "UX_SettlementAlert_Settlement_Recipient";
    public const string RecipientForeignKey = "FK_SettlementAlert_User_RecipientUserId";

    public void Configure(EntityTypeBuilder<SettlementAlert> builder)
    {
        builder.ToTable(nameof(SettlementAlert));
        builder.HasIndex(alert => new { alert.SettlementId, alert.RecipientUserId }).IsUnique().HasDatabaseName(OnceIndex);
        builder.HasIndex(alert => new { alert.ProjectId, alert.CreatedAt }, "IX_SettlementAlert_Project_CreatedAt");
        builder.HasOne(alert => alert.Settlement).WithMany().HasForeignKey(alert => alert.SettlementId).OnDelete(DeleteBehavior.NoAction);
    }
}
