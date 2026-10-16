namespace AlMotaheda.Modules.Platform.Data;

/// <summary>
/// "These records changed", written in the same transaction as the change and handled after the commit by
/// <c>OutboxDispatcher</c> (notifications today; live updates and cache invalidation later). Nothing is published
/// for a change that was rolled back, and nothing is lost if the process stops before it is handled.
/// </summary>
internal sealed class OutboxMessage
{
    public long Id { get; set; }

    /// <summary>Unique per message; notifications made from it remember it, so handling it twice can't duplicate them.</summary>
    public Guid MessageId { get; set; }

    public string Type { get; set; } = "";

    /// <summary>JSON, see <c>EntitiesChangedPayload</c>.</summary>
    public string Payload { get; set; } = "";

    public DateTime OccurredAt { get; set; }

    /// <summary>Who made the change; <c>null</c> for the system.</summary>
    public int? UserId { get; set; }

    /// <summary>Set once handled. Unhandled messages are picked up oldest first.</summary>
    public DateTime? ProcessedAt { get; set; }

    /// <summary>Failed attempts. After <c>OutboxOptions.MaxAttempts</c> the message is left for a person to look at.</summary>
    public int Attempts { get; set; }

    public string? LastError { get; set; }
}
