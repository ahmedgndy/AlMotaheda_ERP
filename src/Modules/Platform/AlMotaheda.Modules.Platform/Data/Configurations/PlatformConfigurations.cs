using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Platform.Data.Configurations;

/// <remarks>Column sizes match the <c>OPENJSON</c> columns in <c>AuditTrailInterceptor</c>.</remarks>
internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public const int ModuleMaxLength = 30;
    public const int EntityTypeMaxLength = 100;
    public const int EntityKeyMaxLength = 100;

    public static readonly string[] Actions = ["Added", "Modified", "Deleted"];

    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable(PlatformTables.AuditEntry, table =>
            table.HasCheckConstraint("CK_AuditEntry_Action", $"[Action] IN ({SqlList.Of(Actions)})"));

        builder.Property(entry => entry.Module).HasMaxLength(ModuleMaxLength);
        builder.Property(entry => entry.EntityType).HasMaxLength(EntityTypeMaxLength);
        builder.Property(entry => entry.EntityKey).HasMaxLength(EntityKeyMaxLength);
        builder.Property(entry => entry.Action).HasMaxLength(10);
        builder.Property(entry => entry.Changes).IsUnicode();
        builder.Property(entry => entry.TraceId).HasMaxLength(32);

        // The audit screen asks: the history of one record, of one project, of one user, or of a period.
        builder.HasIndex(entry => new { entry.EntityType, entry.EntityKey, entry.Id }, "IX_AuditEntry_Entity");
        builder.HasIndex(entry => new { entry.ProjectId, entry.OccurredAt }, "IX_AuditEntry_Project_OccurredAt")
            .HasFilter("[ProjectId] IS NOT NULL");
        builder.HasIndex(entry => new { entry.UserId, entry.OccurredAt }, "IX_AuditEntry_User_OccurredAt");
        builder.HasIndex(entry => entry.OccurredAt, "IX_AuditEntry_OccurredAt");
    }
}

internal sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable(PlatformTables.OutboxMessage, table =>
            table.HasCheckConstraint("CK_OutboxMessage_Attempts", "[Attempts] >= 0"));

        builder.Property(message => message.Type).HasMaxLength(100);
        builder.Property(message => message.Payload).IsUnicode();
        builder.Property(message => message.LastError).HasMaxLength(1000).IsUnicode();

        builder.HasIndex(message => message.MessageId).IsUnique().HasDatabaseName("UX_OutboxMessage_MessageId");

        // The dispatcher reads the unhandled messages, oldest first; handled ones are most of the table.
        builder.HasIndex(message => message.Id, "IX_OutboxMessage_Pending")
            .HasFilter("[ProcessedAt] IS NULL")
            .IncludeProperties(message => message.Attempts);
    }
}

internal sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public const string SourceIndex = "UX_Notification_Source";

    public const int TypeMaxLength = 60;
    public const int TitleMaxLength = 300;

    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable(nameof(Notification));

        builder.Property(notification => notification.Type).HasMaxLength(TypeMaxLength);
        builder.Property(notification => notification.TitleAr).HasMaxLength(TitleMaxLength).IsUnicode();
        builder.Property(notification => notification.TitleEn).HasMaxLength(TitleMaxLength).IsUnicode();
        builder.Property(notification => notification.EntityType).HasMaxLength(AuditEntryConfiguration.EntityTypeMaxLength);
        builder.Property(notification => notification.EntityKey).HasMaxLength(AuditEntryConfiguration.EntityKeyMaxLength);

        // Handling an outbox message twice must not tell anyone twice.
        builder.HasIndex(notification => new
            {
                notification.SourceMessageId,
                notification.RecipientUserId,
                notification.EntityType,
                notification.EntityKey,
                notification.Type,
            })
            .IsUnique()
            .HasDatabaseName(SourceIndex);

        // A user's list, newest first, and the unread count.
        builder.HasIndex(notification => new { notification.RecipientUserId, notification.Id }, "IX_Notification_Recipient")
            .IncludeProperties(notification => notification.ReadAt);
    }
}

internal sealed class DeviceTokenConfiguration : IEntityTypeConfiguration<DeviceToken>
{
    public const string TokenIndex = "UX_DeviceToken_Token";

    /// <summary>Firebase registration tokens are about 160 characters today; room to spare.</summary>
    public const int TokenMaxLength = 400;

    public void Configure(EntityTypeBuilder<DeviceToken> builder)
    {
        builder.ToTable(nameof(DeviceToken), table =>
            table.HasCheckConstraint("CK_DeviceToken_Platform", $"[Platform] IN ({SqlList.Of(DevicePlatforms.All)})"));

        builder.Property(device => device.Platform).HasMaxLength(20);
        builder.Property(device => device.Token).HasMaxLength(TokenMaxLength);

        // One row per phone: registering the same token again updates it (and moves it to the signed-in user).
        builder.HasIndex(device => device.Token).IsUnique().HasDatabaseName(TokenIndex);

        // Sending: the active tokens of one recipient.
        builder.HasIndex(device => device.UserId, "IX_DeviceToken_User").HasFilter("[DisabledAt] IS NULL");
    }
}
