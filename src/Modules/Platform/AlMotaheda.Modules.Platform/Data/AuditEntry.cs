namespace AlMotaheda.Modules.Platform.Data;

/// <summary>
/// One row added, changed or deleted by any module: who, when, which record, and the old and new value of each field
/// that changed. Written by <c>AuditTrailInterceptor</c> in the same transaction as the change. Never updated or
/// deleted.
/// </summary>
internal sealed class AuditEntry
{
    public long Id { get; set; }

    public DateTime OccurredAt { get; set; }

    /// <summary>The signed-in user, or <c>null</c> for the system (startup, background work).</summary>
    public int? UserId { get; set; }

    /// <summary>The schema of the module that owns the record, for example <c>projects</c>.</summary>
    public string Module { get; set; } = "";

    /// <summary>The entity type, for example <c>ProjectExpense</c>.</summary>
    public string EntityType { get; set; } = "";

    /// <summary>The primary key; composite keys are joined with commas.</summary>
    public string EntityKey { get; set; } = "";

    /// <summary><c>Added</c>, <c>Modified</c> or <c>Deleted</c>.</summary>
    public string Action { get; set; } = "";

    public int? ProjectId { get; set; }

    /// <summary>JSON: <c>{ "Field": [old, new], … }</c>. Secrets and row versions are never included.</summary>
    public string Changes { get; set; } = "{}";

    /// <summary>The request's trace id, to match the entry with the logs.</summary>
    public string? TraceId { get; set; }
}
