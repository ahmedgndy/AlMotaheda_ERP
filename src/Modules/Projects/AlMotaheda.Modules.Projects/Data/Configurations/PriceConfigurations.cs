using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Projects.Data.Configurations;

internal sealed class PriceItemConfiguration : IEntityTypeConfiguration<PriceItem>
{
    /// <summary>One price item per name in either language. A lost race is answered with 409 <c>price_item_exists</c>.</summary>
    public const string NameEnIndex = "UX_PriceItem_NameEn";
    public const string NameArIndex = "UX_PriceItem_NameAr";

    public const int NameMaxLength = 200;
    public const int NotesMaxLength = 1000;

    public void Configure(EntityTypeBuilder<PriceItem> builder)
    {
        builder.ToTable(nameof(PriceItem), table =>
        {
            table.HasCheckConstraint("CK_PriceItem_Classification", $"[Classification] IN ({SqlList.Of(Enum.GetNames<CatalogCategory>())})");
            table.HasCheckConstraint("CK_PriceItem_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<PriceItemStatus>())})");
            table.HasCheckConstraint("CK_PriceItem_Unit", $"[Unit] IN ({SqlList.Of(MeasurementUnits.All)})");
        });

        builder.Property(item => item.NameAr).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(item => item.NameEn).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(item => item.Classification).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(item => item.Unit).HasMaxLength(20);
        builder.Property(item => item.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(item => item.RowVersion).IsRowVersion();

        builder.HasIndex(item => item.NameEn).IsUnique().HasDatabaseName(NameEnIndex);
        builder.HasIndex(item => item.NameAr).IsUnique().HasDatabaseName(NameArIndex);

        builder.HasMany(item => item.Ranges)
            .WithOne(range => range.PriceItem)
            .HasForeignKey(range => range.PriceItemId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class PriceRangeConfiguration : IEntityTypeConfiguration<PriceRange>
{
    /// <summary>One pending request per item and supplier (or general). A lost race is answered with 409 <c>price_change_pending</c>.</summary>
    public const string PendingIndex = "UX_PriceRange_Pending";

    /// <summary>One approved range per item, supplier (or general) and start date.</summary>
    public const string ApprovedIndex = "UX_PriceRange_ApprovedFrom";

    public const int TextMaxLength = 500;

    public void Configure(EntityTypeBuilder<PriceRange> builder)
    {
        builder.ToTable(nameof(PriceRange), table =>
        {
            table.HasCheckConstraint("CK_PriceRange_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<PriceRangeStatus>())})");
            table.HasCheckConstraint("CK_PriceRange_Prices", "[MinPrice] > 0 AND [MaxPrice] >= [MinPrice]");
            table.HasCheckConstraint("CK_PriceRange_Currency", $"[Currency] = '{PriceCurrencies.Egp}'");
            table.HasCheckConstraint("CK_PriceRange_Dates", "[EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]");
            table.HasCheckConstraint(
                "CK_PriceRange_Decision",
                "([Status] = 'Pending' AND [DecidedAt] IS NULL AND [DecidedBy] IS NULL) "
                + "OR ([Status] <> 'Pending' AND [DecidedAt] IS NOT NULL AND [DecidedBy] IS NOT NULL)");
        });

        builder.Property(range => range.MinPrice).HasPrecision(18, 2);
        builder.Property(range => range.MaxPrice).HasPrecision(18, 2);
        builder.Property(range => range.Currency).HasMaxLength(3).IsUnicode(false);
        builder.Property(range => range.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(range => range.Conditions).HasMaxLength(TextMaxLength).IsUnicode();
        builder.Property(range => range.Reason).HasMaxLength(TextMaxLength).IsUnicode();
        builder.Property(range => range.DecisionNote).HasMaxLength(TextMaxLength).IsUnicode();

        // HasFilter replaces EF's "SupplierId IS NOT NULL" filter: SQL Server treats NULL suppliers as equal, so the
        // general range is unique too.
        builder.HasIndex(range => new { range.PriceItemId, range.SupplierId }, PendingIndex)
            .IsUnique()
            .HasFilter($"[Status] = '{nameof(PriceRangeStatus.Pending)}'");

        builder.HasIndex(range => new { range.PriceItemId, range.SupplierId, range.EffectiveFrom }, ApprovedIndex)
            .IsUnique()
            .HasFilter($"[Status] = '{nameof(PriceRangeStatus.Approved)}'");

        builder.HasIndex(range => range.Status, "IX_PriceRange_Status");

        builder.HasOne(range => range.Supplier)
            .WithMany()
            .HasForeignKey(range => range.SupplierId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class CostAlertConfiguration : IEntityTypeConfiguration<CostAlert>
{
    public const string RecipientForeignKey = "FK_CostAlert_User_RecipientUserId";
    public const string OnceIndex = "UX_CostAlert_Expense_Kind_Recipient";

    public const string KindColumn = "Kind";
    public const string PriceAboveRange = "PriceAboveRange";
    public const string CostRecorded = "CostRecorded";
    private static readonly string[] Kinds = [PriceAboveRange, CostRecorded];

    public void Configure(EntityTypeBuilder<CostAlert> builder)
    {
        builder.ToTable(nameof(CostAlert), table =>
            table.HasCheckConstraint("CK_CostAlert_Kind", $"[Kind] IN ({SqlList.Of(Kinds)})"));

        builder.HasDiscriminator<string>(KindColumn)
            .HasValue<PriceAboveRangeAlert>(PriceAboveRange)
            .HasValue<CostRecordedAlert>(CostRecorded);
        builder.Property<string>(KindColumn).HasMaxLength(30).IsUnicode(false);

        // One alert per record, kind and person: a resubmitted record doesn't notify the same person twice.
        builder.HasIndex([nameof(CostAlert.ExpenseId), KindColumn, nameof(CostAlert.RecipientUserId)], OnceIndex).IsUnique();
        builder.HasIndex(alert => new { alert.ProjectId, alert.CreatedAt }, "IX_CostAlert_Project_CreatedAt");

        builder.HasOne(alert => alert.Project)
            .WithMany()
            .HasForeignKey(alert => alert.ProjectId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(alert => alert.Expense)
            .WithMany()
            .HasForeignKey(alert => alert.ExpenseId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
