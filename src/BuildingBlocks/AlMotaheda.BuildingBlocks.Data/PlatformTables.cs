namespace AlMotaheda.BuildingBlocks.Data;

/// <summary>
/// The two Platform tables every module writes to, inside its own transaction, through
/// <see cref="AuditTrailInterceptor"/>. Only the Platform module creates them (its migrations) and reads them.
/// </summary>
/// <remarks>
/// The one deliberate exception to "a module writes only its own schema": the audit trail and the outbox must be
/// committed together with the change they describe, so they can't be lost or written for a change that was
/// rolled back. Apply the Platform migrations first (Platform is first in ModuleCatalog).
/// </remarks>
public static class PlatformTables
{
    public const string Schema = "platform";
    public const string AuditEntry = "AuditEntry";
    public const string OutboxMessage = "OutboxMessage";

    /// <summary>The one outbox message type written for every save: the list of records that changed.</summary>
    public const string EntitiesChangedMessage = "EntitiesChanged";
}
