using AlMotaheda.Modules.Projects.Data;

namespace AlMotaheda.Modules.Projects.Features.Expenses;

/// <summary>The answer to "may this caller do this to this record?", with the reason when not.</summary>
internal enum ExpenseDecision
{
    Allowed,

    /// <summary>The caller recorded the record, so someone else must review it (409 <c>own_record</c>).</summary>
    OwnRecord,

    /// <summary>Only the recorder may fix and resubmit (403).</summary>
    NotRecorder,

    /// <summary>The record is not in a status that allows this (409 <c>invalid_status</c>).</summary>
    WrongStatus,
}

/// <summary>
/// The expense workflow (01-projects-core.md, section 2), with no database access:
/// Recorded → Approved, Returned or Rejected by a reviewer; Returned → Recorded again by the recorder.
/// </summary>
internal static class ExpenseRules
{
    /// <summary>Approve, return or reject: only from Recorded, and never by the person who recorded it.</summary>
    public static ExpenseDecision Review(ExpenseStatus status, int recorderUserId, int callerUserId)
    {
        if (recorderUserId == callerUserId)
        {
            return ExpenseDecision.OwnRecord;
        }

        return status == ExpenseStatus.Recorded ? ExpenseDecision.Allowed : ExpenseDecision.WrongStatus;
    }

    /// <summary>Fix and resubmit: only the recorder, only while the record is Recorded or Returned.</summary>
    public static ExpenseDecision Resubmit(ExpenseStatus status, int recorderUserId, int callerUserId)
    {
        if (recorderUserId != callerUserId)
        {
            return ExpenseDecision.NotRecorder;
        }

        return status is ExpenseStatus.Recorded or ExpenseStatus.Returned ? ExpenseDecision.Allowed : ExpenseDecision.WrongStatus;
    }

    /// <summary>
    /// Fuel, rental, maintenance and repair of equipment are recorded only in Site equipment (Fleet), so a supply or
    /// expense from a fuel or equipment supplier is refused: the same cost would otherwise count twice (decision F3).
    /// </summary>
    public static bool IsEquipmentSupplier(SupplierKind kind) => kind is SupplierKind.Fuel or SupplierKind.Equipment;

    /// <summary>
    /// The link to a project item can change in any status except Rejected: a rejected record is final and is
    /// never a cost, so moving it between items would mean nothing.
    /// </summary>
    public static bool CanLink(ExpenseStatus status) => status != ExpenseStatus.Rejected;

    /// <summary>Only an approved credit record can be settled by a supplier payment.</summary>
    public static bool CanBePaid(ExpenseStatus status, PaymentChannel channel) =>
        status == ExpenseStatus.Approved && channel == PaymentChannel.Credit;
}
