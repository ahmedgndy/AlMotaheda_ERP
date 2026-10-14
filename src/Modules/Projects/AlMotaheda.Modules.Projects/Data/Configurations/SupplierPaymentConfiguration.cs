using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Projects.Data.Configurations;

internal sealed class SupplierPaymentConfiguration : IEntityTypeConfiguration<SupplierPayment>
{
    /// <summary>One payment per client key. A lost race on it is answered by returning the first payment.</summary>
    public const string IdempotencyKeyIndex = "UX_SupplierPayment_IdempotencyKey";

    public const int ReferenceMaxLength = 100;
    public const int NotesMaxLength = 500;
    public const int VoidReasonMaxLength = 300;

    public void Configure(EntityTypeBuilder<SupplierPayment> builder)
    {
        builder.ToTable(nameof(SupplierPayment), table =>
        {
            table.HasCheckConstraint("CK_SupplierPayment_Method", $"[Method] IN ({SqlList.Of(Enum.GetNames<PaymentMethod>())})");
            table.HasCheckConstraint("CK_SupplierPayment_Amount", "[Amount] > 0");

            // A void is recorded whole: who, when and why, or nothing at all.
            table.HasCheckConstraint(
                "CK_SupplierPayment_Void",
                "([VoidedAt] IS NULL AND [VoidedBy] IS NULL AND [VoidReason] IS NULL) "
                + "OR ([VoidedAt] IS NOT NULL AND [VoidedBy] IS NOT NULL AND [VoidReason] IS NOT NULL)");
        });

        builder.Property(payment => payment.Method).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(payment => payment.Reference).HasMaxLength(ReferenceMaxLength).IsUnicode();
        builder.Property(payment => payment.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(payment => payment.VoidReason).HasMaxLength(VoidReasonMaxLength).IsUnicode();
        builder.Property(payment => payment.RowVersion).IsRowVersion();

        builder.HasIndex(payment => payment.IdempotencyKey).IsUnique().HasDatabaseName(IdempotencyKeyIndex);

        // The payment list of a project, and a supplier's statement on a project.
        builder.HasIndex(payment => new { payment.ProjectId, payment.PaymentDate }, "IX_SupplierPayment_Project_Date");
        builder.HasIndex(payment => new { payment.ProjectId, payment.SupplierId }, "IX_SupplierPayment_Project_Supplier");

        builder.HasOne(payment => payment.Project)
            .WithMany()
            .HasForeignKey(payment => payment.ProjectId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(payment => payment.Supplier)
            .WithMany()
            .HasForeignKey(payment => payment.SupplierId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasMany(payment => payment.Allocations)
            .WithOne(allocation => allocation.Payment)
            .HasForeignKey(allocation => allocation.PaymentId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class SupplierPaymentAllocationConfiguration : IEntityTypeConfiguration<SupplierPaymentAllocation>
{
    /// <summary>One allocation per payment and expense.</summary>
    public const string PaymentExpenseIndex = "UX_SupplierPaymentAllocation_Payment_Expense";

    public void Configure(EntityTypeBuilder<SupplierPaymentAllocation> builder)
    {
        builder.ToTable(nameof(SupplierPaymentAllocation), table =>
            table.HasCheckConstraint("CK_SupplierPaymentAllocation_Amount", "[Amount] > 0"));

        builder.HasIndex(allocation => new { allocation.PaymentId, allocation.ExpenseId }).IsUnique().HasDatabaseName(PaymentExpenseIndex);

        // "How much of this expense is paid?" runs for every expense on the lists.
        builder.HasIndex(allocation => allocation.ExpenseId, "IX_SupplierPaymentAllocation_ExpenseId");

        builder.HasOne(allocation => allocation.Expense)
            .WithMany()
            .HasForeignKey(allocation => allocation.ExpenseId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
