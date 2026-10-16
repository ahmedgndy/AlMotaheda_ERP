namespace AlMotaheda.Modules.Platform.Data;

/// <summary>
/// A message for one user about one record ("your expense was returned"). Stored first, so a user who was offline
/// sees it at the next sign-in; live delivery (SignalR) only pushes what is already stored.
/// </summary>
internal sealed class Notification
{
    public long Id { get; set; }

    public int RecipientUserId { get; set; }

    /// <summary>A <see cref="Notifications.NotificationTypes"/> value; the frontend may show its own text for it.</summary>
    public string Type { get; set; } = "";

    public string TitleAr { get; set; } = "";
    public string TitleEn { get; set; } = "";

    /// <summary>The record the notification is about: entity type, key and project, so the client can open it.</summary>
    public string EntityType { get; set; } = "";
    public string EntityKey { get; set; } = "";
    public int? ProjectId { get; set; }

    /// <summary>The outbox message it was made from.</summary>
    public Guid SourceMessageId { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? ReadAt { get; set; }
}
