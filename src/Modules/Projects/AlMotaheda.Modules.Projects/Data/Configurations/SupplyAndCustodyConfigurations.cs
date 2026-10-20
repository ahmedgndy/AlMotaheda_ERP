using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Projects.Data.Configurations;

internal sealed class SupplyAgreementConfiguration : IEntityTypeConfiguration<SupplyAgreement>
{
    /// <summary>One agreement per supplier, project, work item and material (409 <c>supply_agreement_exists</c>).</summary>
    public const string UniqueIndex = "UX_SupplyAgreement_Project_Supplier_Item_Material";

    public const int MaterialMaxLength = 200;
    public const int NotesMaxLength = 1000;

    public void Configure(EntityTypeBuilder<SupplyAgreement> builder)
    {
        builder.ToTable(nameof(SupplyAgreement), table =>
        {
            table.HasCheckConstraint("CK_SupplyAgreement_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<SupplyAgreementStatus>())})");
            table.HasCheckConstraint("CK_SupplyAgreement_Unit", $"[Unit] IN ({SqlList.Of(MeasurementUnits.All)})");
            table.HasCheckConstraint(
                "CK_SupplyAgreement_Agreed",
                "([AgreedQuantity] IS NULL OR [AgreedQuantity] > 0) AND ([AgreedUnitPrice] IS NULL OR [AgreedUnitPrice] > 0) "
                + "AND ([AgreedValue] IS NULL OR [AgreedValue] > 0)");
            table.HasCheckConstraint("CK_SupplyAgreement_Dates", "[EndDate] IS NULL OR [StartDate] IS NULL OR [EndDate] >= [StartDate]");
        });

        builder.Property(agreement => agreement.MaterialName).HasMaxLength(MaterialMaxLength).IsUnicode();
        builder.Property(agreement => agreement.Unit).HasMaxLength(20);
        builder.Property(agreement => agreement.AgreedQuantity).HasPrecision(18, 3);
        builder.Property(agreement => agreement.AgreedUnitPrice).HasPrecision(18, 2);
        builder.Property(agreement => agreement.AgreedValue).HasPrecision(18, 2);
        builder.Property(agreement => agreement.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(agreement => agreement.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(agreement => agreement.RowVersion).IsRowVersion();

        builder.HasIndex(agreement => new { agreement.ProjectId, agreement.SupplierId, agreement.ProjectItemId, agreement.MaterialName })
            .IsUnique()
            .HasDatabaseName(UniqueIndex);
        builder.HasIndex(agreement => agreement.SupplierId, "IX_SupplyAgreement_SupplierId");
        builder.HasIndex(agreement => agreement.ProjectItemId, "IX_SupplyAgreement_ProjectItemId");

        builder.HasOne(agreement => agreement.Project).WithMany().HasForeignKey(agreement => agreement.ProjectId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(agreement => agreement.Supplier).WithMany().HasForeignKey(agreement => agreement.SupplierId).OnDelete(DeleteBehavior.NoAction);

        // The work item is of the same project: the key is on (ProjectId, ProjectItemId), like a sub-item's parent.
        builder.HasOne(agreement => agreement.ProjectItem)
            .WithMany()
            .HasForeignKey(agreement => new { agreement.ProjectId, agreement.ProjectItemId })
            .HasPrincipalKey(item => new { item.ProjectId, item.Id })
            .HasConstraintName("FK_SupplyAgreement_ProjectItem_Project_Item")
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class CustodyEntryConfiguration : IEntityTypeConfiguration<CustodyEntry>
{
    public const string HolderForeignKey = "FK_CustodyEntry_User_HolderUserId";

    /// <summary>One movement per paying record (a payment request, a settlement): the same money never moves twice.</summary>
    public const string SourceIndex = "UX_CustodyEntry_Source";
    public const string IdempotencyIndex = "UX_CustodyEntry_IdempotencyKey";

    public const int TextMaxLength = 500;
    public const int ReferenceMaxLength = 100;

    public void Configure(EntityTypeBuilder<CustodyEntry> builder)
    {
        builder.ToTable(nameof(CustodyEntry), table =>
        {
            table.HasCheckConstraint("CK_CustodyEntry_Kind", $"[Kind] IN ({SqlList.Of(Enum.GetNames<CustodyEntryKind>())})");
            table.HasCheckConstraint("CK_CustodyEntry_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<CustodyEntryStatus>())})");
            table.HasCheckConstraint("CK_CustodyEntry_Amount", "[Amount] > 0");
            table.HasCheckConstraint("CK_CustodyEntry_Source", "([SourceType] IS NULL AND [SourceId] IS NULL) OR ([SourceType] IS NOT NULL AND [SourceId] IS NOT NULL)");

            // Fundings and returns are final when written; only a payroll outflow waits for its settlement's review.
            table.HasCheckConstraint("CK_CustodyEntry_Pending", "[Status] = 'Posted' OR [Kind] = 'Payroll'");
        });

        builder.Property(entry => entry.Kind).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(entry => entry.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(entry => entry.Method).HasMaxLength(30).IsUnicode(false);
        builder.Property(entry => entry.Reference).HasMaxLength(ReferenceMaxLength).IsUnicode();
        builder.Property(entry => entry.Notes).HasMaxLength(TextMaxLength).IsUnicode();
        builder.Property(entry => entry.SourceType).HasMaxLength(30).IsUnicode(false);
        builder.Property(entry => entry.RowVersion).IsRowVersion();

        builder.HasIndex(entry => new { entry.SourceType, entry.SourceId }).IsUnique().HasFilter("[SourceType] IS NOT NULL").HasDatabaseName(SourceIndex);
        builder.HasIndex(entry => entry.IdempotencyKey).IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL").HasDatabaseName(IdempotencyIndex);
        builder.HasIndex(entry => new { entry.ProjectId, entry.HolderUserId }, "IX_CustodyEntry_Project_Holder");

        builder.HasOne(entry => entry.Project).WithMany().HasForeignKey(entry => entry.ProjectId).OnDelete(DeleteBehavior.NoAction);
    }
}
