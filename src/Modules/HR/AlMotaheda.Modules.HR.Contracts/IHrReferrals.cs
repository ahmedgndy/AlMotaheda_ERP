using AlMotaheda.BuildingBlocks.Data;

namespace AlMotaheda.Modules.HR.Contracts;

/// <summary>
/// A referral of an employee to HR for investigation, written by the module that found the case (Fleet: a rejected
/// vehicle claim, a violation) in its own transaction. HR investigates and decides; only HR's own payroll flow may
/// then raise a deduction. Nothing here deducts anything.
/// </summary>
public interface IHrReferrals
{
    /// <summary>Opens the referral and returns its id. The same source (type and id) is referred once (unique).</summary>
    Task<int> ReferAsync(HrReferralDraft draft, SharedTransaction transaction, CancellationToken cancellationToken);

    /// <summary>The referral of each source, by source id, with its status and the deduction it led to, if any.</summary>
    Task<IReadOnlyDictionary<int, HrReferralState>> StatesAsync(string sourceType, IReadOnlyCollection<int> sourceIds, CancellationToken cancellationToken);
}

/// <summary>The records that can be referred to HR.</summary>
public static class HrReferralSources
{
    /// <summary>A rejected vehicle expense claim (Fleet).</summary>
    public const string VehicleExpense = "VehicleExpense";

    /// <summary>A vehicle violation (Fleet).</summary>
    public const string VehicleViolation = "VehicleViolation";

    public static IReadOnlyList<string> All { get; } = [VehicleExpense, VehicleViolation];
}

/// <param name="Amount">The amount at stake when established (the rejected claim, the fine), for HR's information only.</param>
/// <param name="ReferenceNo">The record's number, for the referral screen.</param>
public sealed record HrReferralDraft(
    string SourceType,
    int SourceId,
    int EmployeeId,
    int? VehicleId,
    string Subject,
    string Details,
    decimal? Amount,
    string? ReferenceNo,
    int ReferredBy);

/// <param name="Status">Open, UnderInvestigation or Closed.</param>
/// <param name="Outcome">For a closed referral: DeductionApplied, Warning or NoAction.</param>
public sealed record HrReferralState(int ReferralId, int SourceId, string Status, string? Outcome, int? DeductionInputId);
