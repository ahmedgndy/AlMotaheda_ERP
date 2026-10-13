using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Projects.Data.Configurations;

internal sealed class ProjectExpenseConfiguration : IEntityTypeConfiguration<ProjectExpense>
{
    public const int ItemNameMaxLength = 200;
    public const int DescriptionMaxLength = 500;
    public const int ReferenceMaxLength = 50;
    public const int ReviewNoteMaxLength = 500;
    public const int NotesMaxLength = 1000;

    public void Configure(EntityTypeBuilder<ProjectExpense> builder)
    {
        builder.ToTable(nameof(ProjectExpense), table =>
        {
            table.HasCheckConstraint("CK_ProjectExpense_Kind", $"[Kind] IN ({SqlList.Of(Enum.GetNames<ExpenseKind>())})");
            table.HasCheckConstraint("CK_ProjectExpense_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<ExpenseStatus>())})");
            table.HasCheckConstraint("CK_ProjectExpense_Channel", $"[Channel] IN ({SqlList.Of(Enum.GetNames<PaymentChannel>())})");
            table.HasCheckConstraint(
                "CK_ProjectExpense_Category",
                $"[Category] IS NULL OR [Category] IN ({SqlList.Of(Enum.GetNames<ExpenseCategory>())})");
            table.HasCheckConstraint("CK_ProjectExpense_Unit", $"[Unit] IS NULL OR [Unit] IN ({SqlList.Of(MeasurementUnits.All)})");
            table.HasCheckConstraint("CK_ProjectExpense_Amount", "[Amount] > 0");

            // A supply has a supplier, an item, a unit, a quantity and a price, and its amount is their product.
            // An expense has a category and a description, and none of the supply columns.
            // Every column is tested with IS (NOT) NULL first: a CHECK passes when its result is unknown.
            table.HasCheckConstraint(
                "CK_ProjectExpense_KindColumns",
                "([Kind] = 'Supply' AND [SupplierId] IS NOT NULL AND [ItemName] IS NOT NULL AND [Unit] IS NOT NULL "
                + "AND [Quantity] IS NOT NULL AND [UnitPrice] IS NOT NULL AND [Quantity] > 0 AND [UnitPrice] > 0 "
                + "AND [Category] IS NULL AND ABS([Amount] - ROUND([Quantity] * [UnitPrice], 2)) < 0.01) "
                + "OR ([Kind] = 'Expense' AND [Category] IS NOT NULL AND [Description] IS NOT NULL AND [ItemName] IS NULL "
                + "AND [Unit] IS NULL AND [Quantity] IS NULL AND [UnitPrice] IS NULL)");

            // Buying on credit means owing someone: the supplier must be known.
            table.HasCheckConstraint("CK_ProjectExpense_CreditSupplier", "[Channel] = 'Custody' OR [SupplierId] IS NOT NULL");

            // A review is recorded exactly when the row has left Recorded.
            table.HasCheckConstraint(
                "CK_ProjectExpense_Review",
                "([Status] = 'Recorded' AND [ReviewedAt] IS NULL AND [ReviewedBy] IS NULL) "
                + "OR ([Status] <> 'Recorded' AND [ReviewedAt] IS NOT NULL AND [ReviewedBy] IS NOT NULL)");

            table.HasCheckConstraint(
                "CK_ProjectExpense_ItemLink",
                "([ItemLinkedAt] IS NULL AND [ItemLinkedBy] IS NULL) OR ([ItemLinkedAt] IS NOT NULL AND [ItemLinkedBy] IS NOT NULL)");
        });

        builder.Property(expense => expense.Kind).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(expense => expense.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(expense => expense.Channel).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(expense => expense.Category).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(expense => expense.ItemName).HasMaxLength(ItemNameMaxLength).IsUnicode();
        builder.Property(expense => expense.Unit).HasMaxLength(20);
        builder.Property(expense => expense.Quantity).HasPrecision(18, 3);
        builder.Property(expense => expense.Description).HasMaxLength(DescriptionMaxLength).IsUnicode();
        builder.Property(expense => expense.InvoiceNo).HasMaxLength(ReferenceMaxLength).IsUnicode();
        builder.Property(expense => expense.DeliveryNoteNo).HasMaxLength(ReferenceMaxLength).IsUnicode();
        builder.Property(expense => expense.ReviewNote).HasMaxLength(ReviewNoteMaxLength).IsUnicode();
        builder.Property(expense => expense.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(expense => expense.RowVersion).IsRowVersion();

        // The lists of a project: by date, and by status for the review queue.
        builder.HasIndex(expense => new { expense.ProjectId, expense.ExpenseDate }, "IX_ProjectExpense_Project_Date");
        builder.HasIndex(expense => new { expense.ProjectId, expense.Status }, "IX_ProjectExpense_Project_Status");

        // "What is linked to this item?" and "what does this supplier have open?".
        builder.HasIndex(expense => expense.ProjectItemId, "IX_ProjectExpense_ProjectItemId");
        builder.HasIndex(expense => expense.SupplierId, "IX_ProjectExpense_SupplierId");
        builder.HasIndex(expense => expense.CatalogItemId, "IX_ProjectExpense_CatalogItemId");

        builder.HasOne(expense => expense.Project)
            .WithMany()
            .HasForeignKey(expense => expense.ProjectId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(expense => expense.Supplier)
            .WithMany()
            .HasForeignKey(expense => expense.SupplierId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(expense => expense.CatalogItem)
            .WithMany()
            .HasForeignKey(expense => expense.CatalogItemId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(expense => expense.ProjectItem)
            .WithMany()
            .HasForeignKey(expense => expense.ProjectItemId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
