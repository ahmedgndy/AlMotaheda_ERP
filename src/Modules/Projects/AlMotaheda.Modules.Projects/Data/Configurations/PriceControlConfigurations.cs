using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Projects.Data.Configurations;

internal sealed class PriceCategoryConfiguration : IEntityTypeConfiguration<PriceCategory>
{
    public const string CodeIndex = "UX_PriceCategory_Code";
    public const int CodeMaxLength = 40;
    public const int NameMaxLength = 100;

    public void Configure(EntityTypeBuilder<PriceCategory> builder)
    {
        builder.ToTable(nameof(PriceCategory));
        builder.Property(category => category.Code).HasMaxLength(CodeMaxLength).IsUnicode(false);
        builder.Property(category => category.NameAr).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(category => category.NameEn).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(category => category.RowVersion).IsRowVersion();
        builder.HasIndex(category => category.Code).IsUnique().HasDatabaseName(CodeIndex);
        builder.HasData(PriceControlSeed.Categories);
    }
}

internal sealed class PriceUnitConfiguration : IEntityTypeConfiguration<PriceUnit>
{
    public const string CodeIndex = "UX_PriceUnit_Code";
    public const int CodeMaxLength = 20;
    public const int NameMaxLength = 60;

    public void Configure(EntityTypeBuilder<PriceUnit> builder)
    {
        builder.ToTable(nameof(PriceUnit));
        builder.Property(unit => unit.Code).HasMaxLength(CodeMaxLength).IsUnicode(false);
        builder.Property(unit => unit.NameAr).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(unit => unit.NameEn).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(unit => unit.RowVersion).IsRowVersion();
        builder.HasIndex(unit => unit.Code).IsUnique().HasDatabaseName(CodeIndex);
        builder.HasData(PriceControlSeed.Units);
    }
}

internal sealed class PriceExceptionConfiguration : IEntityTypeConfiguration<PriceException>
{
    /// <summary>One open case per transaction. A lost race is answered with the case that won.</summary>
    public const string PendingIndex = "UX_PriceException_Source_Pending";

    public const int SourceTypeMaxLength = 40;
    public const int SummaryMaxLength = 300;
    public const int TextMaxLength = 500;

    public void Configure(EntityTypeBuilder<PriceException> builder)
    {
        builder.ToTable(nameof(PriceException), table =>
        {
            table.HasCheckConstraint("CK_PriceException_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<PriceExceptionStatus>())})");
            table.HasCheckConstraint("CK_PriceException_SourceType", $"[SourceType] IN ({SqlList.Of(PriceExceptionSources.All)})");
            table.HasCheckConstraint("CK_PriceException_Amounts", "[Quantity] > 0 AND [UnitPrice] > 0 AND [VariancePerUnit] >= 0 AND [VarianceTotal] >= 0");
            table.HasCheckConstraint(
                "CK_PriceException_Decision",
                "([Status] = 'Pending' AND [DecidedAt] IS NULL AND [DecidedBy] IS NULL) "
                + "OR ([Status] <> 'Pending' AND [DecidedAt] IS NOT NULL AND [DecidedBy] IS NOT NULL)");
            table.HasCheckConstraint("CK_PriceException_NewRange", "[NewRangeId] IS NULL OR [Status] = 'ApprovedWithPriceChange'");
        });

        builder.Property(exception => exception.SourceType).HasMaxLength(SourceTypeMaxLength).IsUnicode(false);
        builder.Property(exception => exception.Unit).HasMaxLength(20);
        builder.Property(exception => exception.Quantity).HasPrecision(18, 3);
        builder.Property(exception => exception.UnitPrice).HasPrecision(18, 2);
        builder.Property(exception => exception.ReferenceMinPrice).HasPrecision(18, 2);
        builder.Property(exception => exception.ReferenceMaxPrice).HasPrecision(18, 2);
        builder.Property(exception => exception.VariancePerUnit).HasPrecision(18, 2);
        builder.Property(exception => exception.VarianceTotal).HasPrecision(18, 2);
        builder.Property(exception => exception.VariancePercent).HasPrecision(9, 2);
        builder.Property(exception => exception.Reason).HasMaxLength(TextMaxLength).IsUnicode();
        builder.Property(exception => exception.Summary).HasMaxLength(SummaryMaxLength).IsUnicode();
        builder.Property(exception => exception.Status).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(exception => exception.DecisionNote).HasMaxLength(TextMaxLength).IsUnicode();
        builder.Property(exception => exception.RowVersion).IsRowVersion();

        builder.HasIndex(exception => new { exception.SourceType, exception.SourceId }, PendingIndex)
            .IsUnique()
            .HasFilter($"[Status] = '{nameof(PriceExceptionStatus.Pending)}'");
        builder.HasIndex(exception => new { exception.Status, exception.SubmittedAt }, "IX_PriceException_Status_SubmittedAt");
        builder.HasIndex(exception => new { exception.ProjectId, exception.Status }, "IX_PriceException_Project_Status");
        builder.HasIndex(exception => new { exception.PriceItemId, exception.Status }, "IX_PriceException_PriceItem_Status");
        builder.HasIndex(exception => exception.SubmittedBy, "IX_PriceException_SubmittedBy");

        builder.HasOne(exception => exception.Project).WithMany().HasForeignKey(exception => exception.ProjectId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne(exception => exception.PriceItem).WithMany().HasForeignKey(exception => exception.PriceItemId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<PriceRange>().WithMany().HasForeignKey(exception => exception.PriceRangeId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<PriceRange>().WithMany().HasForeignKey(exception => exception.NewRangeId).OnDelete(DeleteBehavior.NoAction)
            .HasConstraintName("FK_PriceException_PriceRange_NewRangeId");
    }
}

internal sealed class PriceExceptionAlertConfiguration : IEntityTypeConfiguration<PriceExceptionAlert>
{
    public const string OnceIndex = "UX_PriceExceptionAlert_Exception_Recipient";
    public const string RecipientForeignKey = "FK_PriceExceptionAlert_User_RecipientUserId";

    public void Configure(EntityTypeBuilder<PriceExceptionAlert> builder)
    {
        builder.ToTable(nameof(PriceExceptionAlert));
        builder.HasIndex(alert => new { alert.ExceptionId, alert.RecipientUserId }).IsUnique().HasDatabaseName(OnceIndex);
        builder.HasIndex(alert => new { alert.ProjectId, alert.CreatedAt }, "IX_PriceExceptionAlert_Project_CreatedAt");
        builder.HasOne(alert => alert.Exception).WithMany().HasForeignKey(alert => alert.ExceptionId).OnDelete(DeleteBehavior.NoAction);
    }
}
