using System.Security.Claims;

namespace AlMotaheda.Modules.Projects.Contracts;

/// <summary>
/// A module that holds records waiting for someone: a record to review, a record returned to its recorder, a report
/// asked of someone. The work inbox (<c>GET /api/inbox</c>) asks every source and shows the caller what is waiting
/// for them. Projects, Workforce, Subcontractors and Fleet each implement it once.
/// </summary>
/// <remarks>
/// The inbox is computed from the records themselves on every call, so it can't miss an event, can't show something
/// already handled, and survives restarts and reconnects. A source answers only with records the caller may act on:
/// it checks the caller's permission for its module and never answers for projects outside
/// <see cref="WorkInboxQuery.ProjectIds"/>.
/// </remarks>
public interface IWorkInboxSource
{
    Task<IReadOnlyList<WorkInboxItem>> ListAsync(WorkInboxQuery query, CancellationToken cancellationToken);
}

/// <param name="ProjectIds">The projects the caller may see and still act on (not completed, or the caller is an Executive).</param>
/// <param name="Limit">The most items one source returns; the oldest come first.</param>
public sealed record WorkInboxQuery(ClaimsPrincipal Caller, int UserId, IReadOnlyCollection<int> ProjectIds, int Limit);

/// <summary>Why an item is in the caller's inbox.</summary>
public static class WorkInboxReasons
{
    /// <summary>Someone else recorded it; the caller may approve, return or reject it.</summary>
    public const string Review = "Review";

    /// <summary>The caller recorded it and a reviewer returned it to be fixed.</summary>
    public const string Returned = "Returned";

    /// <summary>A report was asked of the caller and is not submitted yet.</summary>
    public const string Requested = "Requested";
}

/// <summary>The record types the inbox links to. The same names as the archive's linked record types.</summary>
public static class WorkInboxRecordTypes
{
    public const string Expense = "Expense";
    public const string DailyReport = "DailyReport";
    public const string ReportRequest = "ReportRequest";
    public const string PriceChange = "PriceChange";
    public const string LaborSheet = "LaborSheet";
    public const string EquipmentCost = "EquipmentCost";
    public const string Measurement = "Measurement";
    public const string Manpower = "Manpower";

    /// <summary>A weekly labor settlement waiting for a custody reviewer's confirmation.</summary>
    public const string PayrollSettlement = "PayrollSettlement";

    /// <summary>A change of a subcontract's value waiting for a decision; its parent is the subcontract.</summary>
    public const string ContractVariation = "ContractVariation";

    /// <summary>A transaction priced above its reference range, waiting for the Executive in Price Control.</summary>
    public const string PriceException = "PriceException";

    /// <summary>A vehicle expense claim (fuel, maintenance…) waiting for the accountant's review (Fleet).</summary>
    public const string VehicleExpense = "VehicleExpense";

    /// <summary>A vehicle violation waiting for review (Fleet).</summary>
    public const string VehicleViolation = "VehicleViolation";

    /// <summary>A custody transfer waiting for a decision, or cash to confirm receiving (company-wide unless it funds a project custody).</summary>
    public const string CustodyTransfer = "CustodyTransfer";

    /// <summary>A reference price range proposed and waiting for the Executive (company-wide).</summary>
    public const string PriceRange = "PriceRange";

    /// <summary>An employee's HR request waiting for HR, or answered and waiting for the employee (company-wide).</summary>
    public const string HrRequest = "HrRequest";

    /// <summary>A payment request waiting for the Executive's decision.</summary>
    public const string PaymentRequest = "PaymentRequest";
}

/// <param name="Reason">One of <see cref="WorkInboxReasons"/>.</param>
/// <param name="RecordType">One of <see cref="WorkInboxRecordTypes"/>.</param>
/// <param name="ParentId">The record the item lives under when it has no page of its own (the subcontract of a measurement, the catalog item of a price change).</param>
/// <param name="Date">The business date of the record.</param>
/// <param name="Since">When it started waiting (recorded, submitted or returned).</param>
/// <param name="Summary">A short description in the language it was typed in.</param>
/// <param name="Amount">The amount, only when the caller may see money in the source's module.</param>
/// <param name="FromUserId">Who is waiting on the caller: the recorder for a review, the reviewer for a return, the requester for a report.</param>
/// <param name="Note">The reviewer's note on a returned record.</param>
public sealed record WorkInboxItem(
    string Reason,
    string RecordType,
    int? ProjectId,
    int RecordId,
    int? ParentId,
    DateOnly Date,
    DateTime Since,
    string Summary,
    decimal? Amount,
    int? FromUserId,
    string? Note);
