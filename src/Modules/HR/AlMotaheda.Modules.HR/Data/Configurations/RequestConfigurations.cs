using AlMotaheda.BuildingBlocks.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.HR.Data.Configurations;

internal sealed class HrRequestConfiguration : IEntityTypeConfiguration<HrRequest>
{
    public const string IdempotencyIndex = "UX_HrRequest_IdempotencyKey";

    public const int SubjectMaxLength = 200;
    public const int DetailsMaxLength = 2000;
    public const int NoteMaxLength = 1000;

    public void Configure(EntityTypeBuilder<HrRequest> builder)
    {
        builder.ToTable(nameof(HrRequest), table =>
        {
            table.HasCheckConstraint("CK_HrRequest_Type", $"[Type] IN ({SqlList.Of(Enum.GetNames<HrRequestType>())})");
            table.HasCheckConstraint("CK_HrRequest_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<HrRequestStatus>())})");
            table.HasCheckConstraint("CK_HrRequest_Dates", "[FromDate] IS NULL OR [ToDate] IS NULL OR [ToDate] >= [FromDate]");

            // A decided request has its decision; an open one has none.
            table.HasCheckConstraint(
                "CK_HrRequest_Decided",
                "([Status] IN ('Submitted', 'UnderReview', 'NeedsInfo') AND [DecidedAt] IS NULL AND [DecidedBy] IS NULL AND [DecisionNote] IS NULL) "
                + "OR ([Status] IN ('Approved', 'Rejected', 'Closed') AND [DecidedAt] IS NOT NULL AND [DecidedBy] IS NOT NULL AND [DecisionNote] IS NOT NULL)");
            table.HasCheckConstraint("CK_HrRequest_CaseResponse", "([Type] = 'CaseResponse' AND [ReferralId] IS NOT NULL) OR ([Type] <> 'CaseResponse' AND [ReferralId] IS NULL)");
        });

        builder.Property(request => request.Number)
            .HasMaxLength(20)
            .HasComputedColumnSql("CONCAT('HRQ-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true);
        builder.HasIndex(request => request.Number).IsUnique().HasDatabaseName("UX_HrRequest_Number");

        builder.Property(request => request.Type).HasConversion<string>().HasMaxLength(30);
        builder.Property(request => request.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(request => request.Subject).HasMaxLength(SubjectMaxLength).IsUnicode();
        builder.Property(request => request.Details).HasMaxLength(DetailsMaxLength).IsUnicode();
        builder.Property(request => request.DecisionNote).HasMaxLength(NoteMaxLength).IsUnicode();
        builder.Property(request => request.RowVersion).IsRowVersion();

        builder.HasOne(request => request.Employee).WithMany().HasForeignKey(request => request.EmployeeId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<HrReferral>().WithMany().HasForeignKey(request => request.ReferralId).OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(request => request.Messages).WithOne(message => message.Request).HasForeignKey(message => message.RequestId).OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(request => request.Attachments).WithOne(attachment => attachment.Request).HasForeignKey(attachment => attachment.RequestId).OnDelete(DeleteBehavior.NoAction);

        builder.HasIndex(request => request.IdempotencyKey).IsUnique().HasDatabaseName(IdempotencyIndex);
        builder.HasIndex(request => new { request.EmployeeId, request.Status }, "IX_HrRequest_Employee_Status");
        builder.HasIndex(request => new { request.Status, request.SubmittedAt }, "IX_HrRequest_Status_SubmittedAt");
    }
}

internal sealed class HrRequestMessageConfiguration : IEntityTypeConfiguration<HrRequestMessage>
{
    public const int TextMaxLength = 2000;

    public void Configure(EntityTypeBuilder<HrRequestMessage> builder)
    {
        builder.ToTable(nameof(HrRequestMessage));
        builder.Property(message => message.Text).HasMaxLength(TextMaxLength).IsUnicode();
        builder.HasIndex(message => new { message.RequestId, message.Id }, "IX_HrRequestMessage_Request");
    }
}

internal sealed class HrRequestAttachmentConfiguration : IEntityTypeConfiguration<HrRequestAttachment>
{
    public const string IdempotencyIndex = "UX_HrRequestAttachment_IdempotencyKey";
    public const string StorageKeyIndex = "UX_HrRequestAttachment_StorageKey";
    public const int FileNameMaxLength = 200;

    public void Configure(EntityTypeBuilder<HrRequestAttachment> builder)
    {
        builder.ToTable(nameof(HrRequestAttachment), table =>
        {
            table.HasCheckConstraint("CK_HrRequestAttachment_Size", "[SizeBytes] > 0");
            table.HasCheckConstraint("CK_HrRequestAttachment_Sha256", "LEN([Sha256]) = 64 AND [Sha256] COLLATE Latin1_General_BIN NOT LIKE '%[^0-9a-f]%'");
        });

        builder.Property(attachment => attachment.FileName).HasMaxLength(FileNameMaxLength).IsUnicode();
        builder.Property(attachment => attachment.ContentType).HasMaxLength(100);
        builder.Property(attachment => attachment.Sha256).HasMaxLength(64).IsUnicode(false);
        builder.Property(attachment => attachment.StorageKey).HasMaxLength(60).IsUnicode(false);

        builder.HasIndex(attachment => attachment.IdempotencyKey).IsUnique().HasDatabaseName(IdempotencyIndex);
        builder.HasIndex(attachment => attachment.StorageKey).IsUnique().HasDatabaseName(StorageKeyIndex);
        builder.HasIndex(attachment => attachment.RequestId, "IX_HrRequestAttachment_Request");
    }
}

internal sealed class HrRequestAlertConfiguration : IEntityTypeConfiguration<HrRequestAlert>
{
    /// <summary>Written by hand in the migration: the recipient is an <c>access.User</c>.</summary>
    public const string RecipientForeignKey = "FK_HrRequestAlert_User_RecipientUserId";

    public void Configure(EntityTypeBuilder<HrRequestAlert> builder)
    {
        builder.ToTable(nameof(HrRequestAlert));
        builder.HasOne(alert => alert.Request).WithMany().HasForeignKey(alert => alert.RequestId).OnDelete(DeleteBehavior.NoAction);
        builder.HasIndex(alert => new { alert.RequestId, alert.RecipientUserId }).IsUnique().HasDatabaseName("UX_HrRequestAlert_Request_Recipient");
    }
}
