using AlMotaheda.Modules.Fleet.Data;

namespace AlMotaheda.Modules.Fleet.Features.Assignments;

/// <summary>The answer to "may this machine go to a project from this date?", with the reason when not.</summary>
internal enum AssignDecision
{
    Allowed,

    /// <summary>It has an open assignment, on this project or another (409 <c>equipment_assigned</c>).</summary>
    AlreadyAssigned,

    /// <summary>It is under maintenance, lost or retired (409 <c>equipment_unavailable</c>).</summary>
    Unavailable,

    /// <summary>The start date is before the end of its last stay somewhere (400 on <c>startDate</c>).</summary>
    OverlapsHistory,
}

/// <summary>
/// The assignment rules (05-fleet.md, sections 1 and 2), with no database access. A machine is on one project at a
/// time, and its stays never overlap: a new stay starts on or after the day the last one ended (a machine can
/// leave one site and reach the next on the same day).
/// </summary>
internal static class AssignmentRules
{
    public static AssignDecision CanAssign(EquipmentStatus status, bool hasOpenAssignment, DateOnly? lastEndDate, DateOnly startDate)
    {
        if (hasOpenAssignment)
        {
            return AssignDecision.AlreadyAssigned;
        }

        if (status != EquipmentStatus.InService)
        {
            return AssignDecision.Unavailable;
        }

        return lastEndDate is { } lastEnd && startDate < lastEnd ? AssignDecision.OverlapsHistory : AssignDecision.Allowed;
    }

    /// <summary>An end date is valid from the stay's start date up to today.</summary>
    public static bool CanEndOn(DateOnly startDate, DateOnly endDate, DateOnly today) => endDate >= startDate && endDate <= today;
}
