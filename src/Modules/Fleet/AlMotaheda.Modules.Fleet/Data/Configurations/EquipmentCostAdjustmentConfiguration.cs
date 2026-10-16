using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Fleet.Data.Configurations;

internal sealed class EquipmentCostAdjustmentConfiguration : IEntityTypeConfiguration<EquipmentCostAdjustment>
{
    public const string OneOpenIndex = "UX_EquipmentCostAdjustment_OneOpen";
    public const int ReasonMaxLength = 500;

    public void Configure(EntityTypeBuilder<EquipmentCostAdjustment> builder)
    {
        builder.ToTable(nameof(EquipmentCostAdjustment), table =>
        {
            table.HasCheckConstraint("CK_EquipmentCostAdjustment_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<AdjustmentStatus>())})");
            table.HasCheckConstraint("CK_EquipmentCostAdjustment_Amount", "[Amount] <> 0");
        });

        builder.Property(adjustment => adjustment.Reason).HasMaxLength(ReasonMaxLength).IsUnicode();
        builder.Property(adjustment => adjustment.DecisionNote).HasMaxLength(EquipmentCostConfiguration.ReviewNoteMaxLength).IsUnicode();
        builder.Property(adjustment => adjustment.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(adjustment => adjustment.RowVersion).IsRowVersion();

        builder.HasOne<EquipmentCost>()
            .WithMany(cost => cost.Adjustments)
            .HasForeignKey(adjustment => adjustment.CostId)
            .OnDelete(DeleteBehavior.NoAction);

        // One open adjustment per cost at a time, so two corrections can't be decided against the same figure.
        builder.HasIndex(adjustment => adjustment.CostId)
            .IsUnique()
            .HasFilter("[Status] = 'Requested'")
            .HasDatabaseName(OneOpenIndex);
        builder.HasIndex(adjustment => new { adjustment.CostId, adjustment.Status }, "IX_EquipmentCostAdjustment_Cost_Status");
    }
}
