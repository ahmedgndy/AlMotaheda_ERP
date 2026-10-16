using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Projects.Data.Configurations;

internal sealed class ExpenseAdjustmentConfiguration : IEntityTypeConfiguration<ExpenseAdjustment>
{
    public const string OneOpenIndex = "UX_ExpenseAdjustment_OneOpen";
    public const int ReasonMaxLength = 500;

    public void Configure(EntityTypeBuilder<ExpenseAdjustment> builder)
    {
        builder.ToTable(nameof(ExpenseAdjustment), table =>
        {
            table.HasCheckConstraint("CK_ExpenseAdjustment_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<AdjustmentStatus>())})");
            table.HasCheckConstraint("CK_ExpenseAdjustment_Amount", "[Amount] <> 0");
        });

        builder.Property(adjustment => adjustment.Reason).HasMaxLength(ReasonMaxLength).IsUnicode();
        builder.Property(adjustment => adjustment.DecisionNote).HasMaxLength(ProjectExpenseConfiguration.ReviewNoteMaxLength).IsUnicode();
        builder.Property(adjustment => adjustment.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(adjustment => adjustment.RowVersion).IsRowVersion();

        builder.HasOne<ProjectExpense>()
            .WithMany(expense => expense.Adjustments)
            .HasForeignKey(adjustment => adjustment.ExpenseId)
            .OnDelete(DeleteBehavior.NoAction);

        // One open adjustment per record at a time, so two corrections can't be decided against the same figure.
        builder.HasIndex(adjustment => adjustment.ExpenseId)
            .IsUnique()
            .HasFilter("[Status] = 'Requested'")
            .HasDatabaseName(OneOpenIndex);
        builder.HasIndex(adjustment => new { adjustment.ExpenseId, adjustment.Status }, "IX_ExpenseAdjustment_Expense_Status");
    }
}
