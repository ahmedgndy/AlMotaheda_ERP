using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Projects.Data.Configurations;

internal sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    /// <summary>Unique index on the English name. A lost race on it is answered with 409 <c>supplier_exists</c>.</summary>
    public const string NameIndex = "UX_Supplier_NameEn";
    public const string NameArIndex = "UX_Supplier_NameAr";
    public const string TaxNumberIndex = "UX_Supplier_TaxNumber";

    public const int NameMaxLength = 200;
    public const int PhoneMaxLength = 30;
    public const int TaxNumberMaxLength = 30;
    public const int NotesMaxLength = 1000;

    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.ToTable(nameof(Supplier), table =>
        {
            table.HasCheckConstraint("CK_Supplier_Kind", $"[Kind] IN ({SqlList.Of(Enum.GetNames<SupplierKind>())})");
            table.HasCheckConstraint("CK_Supplier_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<SupplierStatus>())})");
        });

        builder.Property(supplier => supplier.NameAr).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(supplier => supplier.NameEn).HasMaxLength(NameMaxLength).IsUnicode();
        // One supplier per name in either language, and per tax number when it is known: the same supplier entered
        // twice would split its balance (refactor plan M9).
        builder.HasIndex(supplier => supplier.NameEn).IsUnique().HasDatabaseName(NameIndex);
        builder.HasIndex(supplier => supplier.NameAr).IsUnique().HasDatabaseName(NameArIndex);
        builder.HasIndex(supplier => supplier.TaxNumber).IsUnique().HasFilter("[TaxNumber] IS NOT NULL").HasDatabaseName(TaxNumberIndex);

        builder.Property(supplier => supplier.Kind).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(supplier => supplier.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(supplier => supplier.Phone).HasMaxLength(PhoneMaxLength);
        builder.Property(supplier => supplier.TaxNumber).HasMaxLength(TaxNumberMaxLength);
        builder.Property(supplier => supplier.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(supplier => supplier.RowVersion).IsRowVersion();
    }
}

internal sealed class ProjectItemConfiguration : IEntityTypeConfiguration<ProjectItem>
{
    /// <summary>
    /// Item names are unique among siblings: among the main items of a project, and among the sub-items of one main
    /// item (two main items may each have a "Cement" sub-item). A lost race is answered with 409 <c>item_name_taken</c>.
    /// </summary>
    public const string NameArIndex = "UX_ProjectItem_Parent_NameAr";
    public const string NameEnIndex = "UX_ProjectItem_Parent_NameEn";

    /// <summary>The key the sub-item foreign key points to, so a parent is always on the same project.</summary>
    public const string ProjectAndIdKey = "AK_ProjectItem_Project_Id";
    public const string ParentForeignKey = "FK_ProjectItem_ProjectItem_Parent";

    public const int NameMaxLength = 200;
    public const int TextMaxLength = 1000;

    public void Configure(EntityTypeBuilder<ProjectItem> builder)
    {
        builder.ToTable(nameof(ProjectItem), table =>
        {
            table.HasCheckConstraint("CK_ProjectItem_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<ProjectItemStatus>())})");
            table.HasCheckConstraint("CK_ProjectItem_Category", $"[Category] IN ({SqlList.Of(WorkTypes.All)})");
            table.HasCheckConstraint("CK_ProjectItem_Unit", $"[Unit] IN ({SqlList.Of(MeasurementUnits.All)})");

            // Deactivation details exist exactly while the item is inactive.
            table.HasCheckConstraint(
                "CK_ProjectItem_Deactivation",
                "([Status] = 'Active' AND [DeactivatedAt] IS NULL AND [DeactivatedBy] IS NULL) "
                + "OR ([Status] = 'Inactive' AND [DeactivatedAt] IS NOT NULL AND [DeactivatedBy] IS NOT NULL)");

            // An item is never its own parent. Two levels only is a rule of the slices (it needs the parent's row).
            table.HasCheckConstraint("CK_ProjectItem_Parent", "[ParentItemId] IS NULL OR [ParentItemId] <> [Id]");
        });

        builder.Property(item => item.NameAr).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(item => item.NameEn).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(item => item.Category).HasMaxLength(30);
        builder.Property(item => item.Unit).HasMaxLength(20);
        builder.Property(item => item.Description).HasMaxLength(TextMaxLength).IsUnicode();
        builder.Property(item => item.Notes).HasMaxLength(TextMaxLength).IsUnicode();
        builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(item => item.RowVersion).IsRowVersion();

        // HasFilter(null): SQL Server treats NULL parents as equal in a unique index, which is exactly "unique among
        // the main items". EF would otherwise add "WHERE [ParentItemId] IS NOT NULL" and leave main items unchecked.
        builder.HasIndex(item => new { item.ProjectId, item.ParentItemId, item.NameAr }).IsUnique().HasFilter(null).HasDatabaseName(NameArIndex);
        builder.HasIndex(item => new { item.ProjectId, item.ParentItemId, item.NameEn }).IsUnique().HasFilter(null).HasDatabaseName(NameEnIndex);

        builder.HasOne(item => item.Project)
            .WithMany()
            .HasForeignKey(item => item.ProjectId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasAlternateKey(item => new { item.ProjectId, item.Id }).HasName(ProjectAndIdKey);

        builder.HasOne(item => item.ParentItem)
            .WithMany()
            .HasForeignKey(item => new { item.ProjectId, item.ParentItemId })
            .HasPrincipalKey(parent => new { parent.ProjectId, parent.Id })
            .HasConstraintName(ParentForeignKey)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class CatalogItemConfiguration : IEntityTypeConfiguration<CatalogItem>
{
    /// <summary>Catalog item names are unique within a project. A lost race is answered with 409 <c>catalog_item_exists</c>.</summary>
    public const string NameIndex = "UX_CatalogItem_NameEn";
    public const string NameArIndex = "UX_CatalogItem_NameAr";

    public const int NameMaxLength = 200;

    public void Configure(EntityTypeBuilder<CatalogItem> builder)
    {
        builder.ToTable(nameof(CatalogItem), table =>
        {
            table.HasCheckConstraint("CK_CatalogItem_Category", $"[Category] IN ({SqlList.Of(Enum.GetNames<CatalogCategory>())})");
            table.HasCheckConstraint("CK_CatalogItem_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<CatalogItemStatus>())})");
            table.HasCheckConstraint("CK_CatalogItem_Unit", $"[Unit] IN ({SqlList.Of(MeasurementUnits.All)})");
            table.HasCheckConstraint("CK_CatalogItem_EstimatedQuantity", "[EstimatedQuantity] >= 0");
        });

        builder.Property(item => item.NameAr).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(item => item.NameEn).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(item => item.Category).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(item => item.Unit).HasMaxLength(20);
        builder.Property(item => item.EstimatedQuantity).HasPrecision(18, 3);
        builder.Property(item => item.RowVersion).IsRowVersion();

        builder.HasIndex(item => new { item.ProjectId, item.NameEn }).IsUnique().HasDatabaseName(NameIndex);
        builder.HasIndex(item => new { item.ProjectId, item.NameAr }).IsUnique().HasDatabaseName(NameArIndex);

        builder.HasOne(item => item.Project)
            .WithMany()
            .HasForeignKey(item => item.ProjectId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(item => item.Supplier)
            .WithMany()
            .HasForeignKey(item => item.SupplierId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasMany(item => item.Prices)
            .WithOne(price => price.CatalogItem)
            .HasForeignKey(price => price.CatalogItemId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class ItemPriceConfiguration : IEntityTypeConfiguration<ItemPrice>
{
    /// <summary>One pending request per item. A lost race is answered with 409 <c>price_change_pending</c>.</summary>
    public const string PendingIndex = "UX_ItemPrice_Pending";

    /// <summary>One approved price per item and start date.</summary>
    public const string ApprovedIndex = "UX_ItemPrice_ApprovedFrom";

    public const int ReasonMaxLength = 500;

    public void Configure(EntityTypeBuilder<ItemPrice> builder)
    {
        builder.ToTable(nameof(ItemPrice), table =>
        {
            table.HasCheckConstraint("CK_ItemPrice_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<ItemPriceStatus>())})");
            table.HasCheckConstraint("CK_ItemPrice_Price", "[Price] > 0 AND ([PreviousPrice] IS NULL OR [PreviousPrice] > 0)");

            // A decision is recorded exactly when the request is no longer pending.
            table.HasCheckConstraint(
                "CK_ItemPrice_Decision",
                "([Status] = 'Pending' AND [DecidedAt] IS NULL AND [DecidedBy] IS NULL) "
                + "OR ([Status] <> 'Pending' AND [DecidedAt] IS NOT NULL AND [DecidedBy] IS NOT NULL)");
        });

        builder.Property(price => price.Price).HasPrecision(18, 2);
        builder.Property(price => price.PreviousPrice).HasPrecision(18, 2);
        builder.Property(price => price.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(price => price.Reason).HasMaxLength(ReasonMaxLength).IsUnicode();
        builder.Property(price => price.DecisionNote).HasMaxLength(ReasonMaxLength).IsUnicode();

        builder.HasIndex(price => price.CatalogItemId, PendingIndex)
            .IsUnique()
            .HasFilter($"[Status] = '{nameof(ItemPriceStatus.Pending)}'");

        builder.HasIndex(price => new { price.CatalogItemId, price.EffectiveFrom }, ApprovedIndex)
            .IsUnique()
            .HasFilter($"[Status] = '{nameof(ItemPriceStatus.Approved)}'");
    }
}
