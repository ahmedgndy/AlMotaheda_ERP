using AlMotaheda.Modules.Workforce.Data;

namespace AlMotaheda.Modules.Workforce.Features.Sheets;

/// <summary>The answer to "may this caller do this to this sheet?", with the reason when not.</summary>
internal enum SheetDecision
{
    Allowed,

    /// <summary>The caller recorded the sheet, so someone else must review it (409 <c>own_record</c>).</summary>
    OwnRecord,

    /// <summary>Only the recorder edits and submits (403).</summary>
    NotRecorder,

    /// <summary>The sheet is not in a status that allows this (409 <c>invalid_status</c>).</summary>
    WrongStatus,
}

/// <summary>
/// The sheet workflow (03-workforce.md, "Sheet workflow"), with no database access:
/// Draft → Submitted → Approved, Returned (back to the recorder) or Rejected (final).
/// </summary>
internal static class SheetRules
{
    /// <summary>Approve, return or reject: only from Submitted, and never by the person who recorded it.</summary>
    public static SheetDecision Review(SheetStatus status, int recorderUserId, int callerUserId)
    {
        if (recorderUserId == callerUserId)
        {
            return SheetDecision.OwnRecord;
        }

        return status == SheetStatus.Submitted ? SheetDecision.Allowed : SheetDecision.WrongStatus;
    }

    /// <summary>Edit and submit: only the recorder, only while the sheet is Draft or Returned.</summary>
    public static SheetDecision Edit(SheetStatus status, int recorderUserId, int callerUserId)
    {
        if (recorderUserId != callerUserId)
        {
            return SheetDecision.NotRecorder;
        }

        return IsEditable(status) ? SheetDecision.Allowed : SheetDecision.WrongStatus;
    }

    public static bool IsEditable(SheetStatus status) => status is SheetStatus.Draft or SheetStatus.Returned;

    /// <summary>
    /// Who may change the item allocations: anyone with workers Edit or Approve while the sheet isn't rejected, or
    /// the recorder (workers Create) while the sheet is Draft or Returned, the same as editing it. Once submitted,
    /// the sheet is in the reviewer's hands, so only Edit or Approve may change it (03-workforce.md). A rejected
    /// sheet's lines are void and count nowhere.
    /// </summary>
    public static SheetDecision Allocate(SheetStatus status, int recorderUserId, int callerUserId, bool canEditOrApprove, bool canCreate)
    {
        if (status == SheetStatus.Rejected)
        {
            return SheetDecision.WrongStatus;
        }

        if (canEditOrApprove)
        {
            return SheetDecision.Allowed;
        }

        if (!canCreate || recorderUserId != callerUserId)
        {
            return SheetDecision.NotRecorder;
        }

        return IsEditable(status) ? SheetDecision.Allowed : SheetDecision.NotRecorder;
    }

    /// <summary>A sheet is for today or one of the 7 days before: labor is recorded while it is fresh.</summary>
    public static bool WorkDateAllowed(DateOnly workDate, DateOnly today) =>
        workDate <= today && workDate >= today.AddDays(-SheetForm.DaysBack);

    /// <summary>
    /// Pending review: submitted and waiting for a reviewer. A returned sheet waits for its recorder, not a reviewer,
    /// so it is not counted, the same as the other cost groups. Shown apart; never cost.
    /// </summary>
    public static bool IsPending(SheetStatus status) => status is SheetStatus.Submitted;
}
