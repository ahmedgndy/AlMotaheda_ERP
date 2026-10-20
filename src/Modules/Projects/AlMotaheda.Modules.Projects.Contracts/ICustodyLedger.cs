using AlMotaheda.BuildingBlocks.Data;

namespace AlMotaheda.Modules.Projects.Contracts;

/// <summary>
/// The cash custody ("العهدة") of one person on one project, owned by Projects. Other modules that pay out of a
/// custody (Workforce: weekly labor paid in cash by the site engineer) write their outflow here, in their own
/// transaction, so the custody balance has one source.
/// </summary>
public interface ICustodyLedger
{
    /// <summary>What the holder has in hand on the project now: paid fundings − returns − every outflow not released.</summary>
    Task<decimal> BalanceAsync(int projectId, int holderUserId, CancellationToken cancellationToken);

    /// <summary>True when the user is an active member of the project, and so may hold a custody on it.</summary>
    Task<bool> CanHoldAsync(int projectId, int holderUserId, CancellationToken cancellationToken);

    /// <summary>
    /// Records an outflow that waits for review (status Pending: it already lowers the balance), in the caller's
    /// transaction. <paramref name="sourceType"/> and <paramref name="sourceId"/> name the paying record; the pair is
    /// unique, so the same record never takes money twice.
    /// </summary>
    Task HoldAsync(CustodyOutflow outflow, SharedTransaction transaction, CancellationToken cancellationToken);

    /// <summary>The reviewer confirmed the paying record: the outflow is final (Posted).</summary>
    Task PostAsync(string sourceType, int sourceId, SharedTransaction transaction, CancellationToken cancellationToken);

    /// <summary>
    /// The paying record was rejected (a pending outflow) or reversed (a posted one): the outflow no longer counts
    /// (Released). Kept for the audit.
    /// </summary>
    Task ReleaseAsync(string sourceType, int sourceId, SharedTransaction transaction, CancellationToken cancellationToken);
}

/// <param name="Method">How the holder paid out: Cash (or a wallet transfer from the custody money).</param>
public sealed record CustodyOutflow(
    int ProjectId, int HolderUserId, decimal Amount, DateOnly EntryDate, string SourceType, int SourceId, string Method, string? Reference, int CreatedBy);

/// <summary>The kinds of record that pay out of a custody through <see cref="ICustodyLedger"/>.</summary>
public static class CustodySourceTypes
{
    public const string PayrollSettlement = "PayrollSettlement";
}

/// <summary>What happens when an outflow is more than the custody holds. Configured in <c>Projects:CustodyBalanceCheck</c>.</summary>
public static class CustodyBalancePolicies
{
    /// <summary>Default: saved, and the balance shows negative (money the company owes the holder) until funded.</summary>
    public const string Warn = "Warn";

    /// <summary>Refused (409 <c>custody_insufficient</c>).</summary>
    public const string Block = "Block";
}

/// <summary>
/// The custody refused an outflow: <see cref="Code"/> is <c>custody_busy</c> (another movement of the same custody is
/// being saved: try again) or <c>custody_insufficient</c> (policy Block and the custody holds less than the amount).
/// </summary>
public sealed class CustodyRefusedException(string code, string message) : Exception(message)
{
    public const string Busy = "custody_busy";
    public const string Insufficient = "custody_insufficient";

    public string Code { get; } = code;
}
