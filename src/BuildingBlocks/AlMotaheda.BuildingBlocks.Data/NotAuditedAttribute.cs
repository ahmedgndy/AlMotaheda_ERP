namespace AlMotaheda.BuildingBlocks.Data;

/// <summary>
/// Leaves an entity, or one property, out of the audit trail and the outbox.
/// </summary>
/// <remarks>
/// On a class: bookkeeping rows that are not business changes (sign-in state, refresh tokens).
/// On a property: secrets that must never be copied anywhere (password hashes).
/// Row versions are always left out.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, Inherited = false)]
public sealed class NotAuditedAttribute : Attribute;
