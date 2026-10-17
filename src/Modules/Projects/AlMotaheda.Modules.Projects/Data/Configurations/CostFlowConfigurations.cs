using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Projects.Data.Configurations;

/// <summary>The master catalog (docs/cost-flow §2.2).</summary>
internal sealed class MasterItemConfiguration : IEntityTypeConfiguration<MasterItem>
{
    public const string NameEnIndex = "UX_MasterItem_NameEn";
    public const string NameArIndex = "UX_MasterItem_NameAr";

    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 1000;

    public void Configure(EntityTypeBuilder<MasterItem> builder)
    {
        builder.ToTable(nameof(MasterItem), table =>
        {
            table.HasCheckConstraint("CK_MasterItem_Kind", $"[Kind] IN ({SqlList.Of(Enum.GetNames<MasterItemKind>())})");
            table.HasCheckConstraint("CK_MasterItem_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<MasterItemStatus>())})");
            table.HasCheckConstraint("CK_MasterItem_Unit", $"[Unit] IN ({SqlList.Of(MeasurementUnits.All)})");

            // Work and composite items take a work type; the others a catalog category (they go to different tables).
            table.HasCheckConstraint(
                "CK_MasterItem_Category",
                $"([Kind] IN ('{nameof(MasterItemKind.Work)}', '{nameof(MasterItemKind.Composite)}') AND [Category] IN ({SqlList.Of(WorkTypes.All)})) "
                + $"OR ([Kind] NOT IN ('{nameof(MasterItemKind.Work)}', '{nameof(MasterItemKind.Composite)}') AND [Category] IN ({SqlList.Of(Enum.GetNames<CatalogCategory>())}))");
        });

        builder.Property(item => item.NameAr).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(item => item.NameEn).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(item => item.Kind).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(item => item.Category).HasMaxLength(30).IsUnicode(false);
        builder.Property(item => item.Unit).HasMaxLength(20);
        builder.Property(item => item.Description).HasMaxLength(DescriptionMaxLength).IsUnicode();
        builder.Property(item => item.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(item => item.RowVersion).IsRowVersion();

        builder.HasIndex(item => item.NameEn).IsUnique().HasDatabaseName(NameEnIndex);
        builder.HasIndex(item => item.NameAr).IsUnique().HasDatabaseName(NameArIndex);

        builder.HasMany(item => item.Prices)
            .WithOne(price => price.MasterItem)
            .HasForeignKey(price => price.MasterItemId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class MasterItemPriceConfiguration : IEntityTypeConfiguration<MasterItemPrice>
{
    /// <summary>One pending request per master item. A lost race is answered with 409 <c>price_change_pending</c>.</summary>
    public const string PendingIndex = "UX_MasterItemPrice_Pending";

    /// <summary>One approved price per master item and start date.</summary>
    public const string ApprovedIndex = "UX_MasterItemPrice_ApprovedFrom";

    public void Configure(EntityTypeBuilder<MasterItemPrice> builder)
    {
        builder.ToTable(nameof(MasterItemPrice), table =>
        {
            table.HasCheckConstraint("CK_MasterItemPrice_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<ItemPriceStatus>())})");
            table.HasCheckConstraint("CK_MasterItemPrice_Price", "[Price] > 0 AND ([PreviousPrice] IS NULL OR [PreviousPrice] > 0)");
            table.HasCheckConstraint(
                "CK_MasterItemPrice_Decision",
                "([Status] = 'Pending' AND [DecidedAt] IS NULL AND [DecidedBy] IS NULL) "
                + "OR ([Status] <> 'Pending' AND [DecidedAt] IS NOT NULL AND [DecidedBy] IS NOT NULL)");
        });

        builder.Property(price => price.Price).HasPrecision(18, 2);
        builder.Property(price => price.PreviousPrice).HasPrecision(18, 2);
        // A concurrency token: two people deciding the same request at once can't both win.
        builder.Property(price => price.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false).IsConcurrencyToken();
        builder.Property(price => price.Reason).HasMaxLength(ItemPriceConfiguration.ReasonMaxLength).IsUnicode();
        builder.Property(price => price.DecisionNote).HasMaxLength(ItemPriceConfiguration.ReasonMaxLength).IsUnicode();

        builder.HasIndex(price => price.MasterItemId, PendingIndex)
            .IsUnique()
            .HasFilter($"[Status] = '{nameof(ItemPriceStatus.Pending)}'");

        builder.HasIndex(price => new { price.MasterItemId, price.EffectiveFrom }, ApprovedIndex)
            .IsUnique()
            .HasFilter($"[Status] = '{nameof(ItemPriceStatus.Approved)}'");

        builder.HasIndex(price => price.Status).HasDatabaseName("IX_MasterItemPrice_Status");
    }
}

internal sealed class PricingPolicyConfiguration : IEntityTypeConfiguration<PricingPolicy>
{
    public void Configure(EntityTypeBuilder<PricingPolicy> builder)
    {
        builder.ToTable(nameof(PricingPolicy), table =>
        {
            table.HasCheckConstraint("CK_PricingPolicy_Singleton", $"[Id] = {PricingPolicy.SingletonId}");
            table.HasCheckConstraint("CK_PricingPolicy_Tolerance", "[VarianceTolerancePercent] >= 0 AND [VarianceTolerancePercent] <= 100");
            table.HasCheckConstraint("CK_PricingPolicy_Rounding", "[RoundingAllowance] >= 0");
        });

        builder.Property(policy => policy.Id).ValueGeneratedNever();
        builder.Property(policy => policy.VarianceTolerancePercent).HasPrecision(5, 2);
        builder.Property(policy => policy.RoundingAllowance).HasPrecision(18, 2);
        builder.Property(policy => policy.RowVersion).IsRowVersion();

        // Seeded: no tolerance, one piastre per unit for rounding [Confirm the values with the owner].
        builder.HasData(new PricingPolicy { Id = PricingPolicy.SingletonId, VarianceTolerancePercent = 0m, RoundingAllowance = 0.01m });
    }
}

internal sealed class PriceVarianceConfiguration : IEntityTypeConfiguration<PriceVariance>
{
    /// <summary>At most one variance waiting or rejected per supply. A lost race is answered with 409.</summary>
    public const string ActiveIndex = "UX_PriceVariance_Active";

    public const int NoteMaxLength = 500;

    public void Configure(EntityTypeBuilder<PriceVariance> builder)
    {
        builder.ToTable(nameof(PriceVariance), table =>
        {
            table.HasCheckConstraint("CK_PriceVariance_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<PriceVarianceStatus>())})");
            table.HasCheckConstraint("CK_PriceVariance_Direction", $"[Direction] IN ({SqlList.Of(Enum.GetNames<VarianceDirection>())})");
            table.HasCheckConstraint("CK_PriceVariance_Prices", "[ReferencePrice] > 0 AND [ActualPrice] > 0 AND [Quantity] > 0");
            table.HasCheckConstraint(
                "CK_PriceVariance_Decision",
                "([Status] = 'Open' AND [DecidedAt] IS NULL AND [DecidedBy] IS NULL) "
                + "OR ([Status] <> 'Open' AND [DecidedAt] IS NOT NULL AND [DecidedBy] IS NOT NULL)");
        });

        builder.Property(variance => variance.ReferencePrice).HasPrecision(18, 2);
        builder.Property(variance => variance.ActualPrice).HasPrecision(18, 2);
        builder.Property(variance => variance.Quantity).HasPrecision(18, 3);
        builder.Property(variance => variance.UnitVariance).HasPrecision(18, 2);
        builder.Property(variance => variance.TotalVariance).HasPrecision(18, 2);
        builder.Property(variance => variance.VariancePercent).HasPrecision(9, 2);
        builder.Property(variance => variance.TolerancePercent).HasPrecision(5, 2);
        builder.Property(variance => variance.RoundingAllowance).HasPrecision(18, 2);
        builder.Property(variance => variance.Direction).HasConversion<string>().HasMaxLength(10).IsUnicode(false);
        builder.Property(variance => variance.Status).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        builder.Property(variance => variance.DecisionNote).HasMaxLength(NoteMaxLength).IsUnicode();
        builder.Property(variance => variance.RowVersion).IsRowVersion();

        builder.HasIndex(variance => variance.ExpenseId, ActiveIndex)
            .IsUnique()
            .HasFilter($"[Status] IN ('{nameof(PriceVarianceStatus.Open)}', '{nameof(PriceVarianceStatus.Rejected)}')");
        builder.HasIndex(variance => new { variance.ProjectId, variance.Status }).HasDatabaseName("IX_PriceVariance_Project_Status");

        builder.HasOne(variance => variance.Expense)
            .WithMany()
            .HasForeignKey(variance => variance.ExpenseId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<Project>()
            .WithMany()
            .HasForeignKey(variance => variance.ProjectId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne<CatalogItem>()
            .WithMany()
            .HasForeignKey(variance => variance.CatalogItemId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class ProjectItemComponentConfiguration : IEntityTypeConfiguration<ProjectItemComponent>
{
    /// <summary>A material appears once per item.</summary>
    public const string MaterialIndex = "UX_ProjectItemComponent_Material";

    /// <summary>Labor, equipment and subcontract work appear once per item.</summary>
    public const string KindIndex = "UX_ProjectItemComponent_Kind";

    public const int NotesMaxLength = 500;

    public void Configure(EntityTypeBuilder<ProjectItemComponent> builder)
    {
        builder.ToTable(nameof(ProjectItemComponent), table =>
        {
            table.HasCheckConstraint("CK_ProjectItemComponent_Kind", $"[Kind] IN ({SqlList.Of(Enum.GetNames<ComponentKind>())})");
            table.HasCheckConstraint(
                "CK_ProjectItemComponent_Material",
                $"([Kind] = '{nameof(ComponentKind.Material)}' AND [CatalogItemId] IS NOT NULL) OR ([Kind] <> '{nameof(ComponentKind.Material)}' AND [CatalogItemId] IS NULL)");
            table.HasCheckConstraint(
                "CK_ProjectItemComponent_Quantities",
                "([PlannedQuantity] IS NULL OR [PlannedQuantity] > 0) AND ([QuantityPerUnit] IS NULL OR [QuantityPerUnit] > 0)");
        });

        builder.Property(component => component.Kind).HasConversion<string>().HasMaxLength(20).IsUnicode(false);
        builder.Property(component => component.PlannedQuantity).HasPrecision(18, 3);
        builder.Property(component => component.QuantityPerUnit).HasPrecision(18, 4);
        builder.Property(component => component.Notes).HasMaxLength(NotesMaxLength).IsUnicode();

        builder.HasIndex(component => new { component.ProjectItemId, component.CatalogItemId }, MaterialIndex)
            .IsUnique()
            .HasFilter("[CatalogItemId] IS NOT NULL");
        builder.HasIndex(component => new { component.ProjectItemId, component.Kind }, KindIndex)
            .IsUnique()
            .HasFilter($"[Kind] <> '{nameof(ComponentKind.Material)}'");

        builder.HasOne(component => component.ProjectItem)
            .WithMany(item => item.Components)
            .HasForeignKey(component => component.ProjectItemId)
            .OnDelete(DeleteBehavior.NoAction);

        builder.HasOne(component => component.CatalogItem)
            .WithMany()
            .HasForeignKey(component => component.CatalogItemId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}
