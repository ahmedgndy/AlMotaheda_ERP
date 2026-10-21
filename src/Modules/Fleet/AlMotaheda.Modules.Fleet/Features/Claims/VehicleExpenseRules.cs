using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features.Costs;
using AlMotaheda.Modules.Projects.Contracts;

namespace AlMotaheda.Modules.Fleet.Features.Claims;

/// <summary>The answer to "may this caller do this to this claim?", with the reason when not.</summary>
internal enum ClaimDecision
{
    Allowed,

    /// <summary>The caller submitted the claim, or is the driver who paid it: someone else reviews it (409 <c>own_record</c>).</summary>
    OwnRecord,

    /// <summary>The claim is not in a status that allows this (409 <c>invalid_status</c>).</summary>
    WrongStatus,

    /// <summary>Escalated, or above the policy's limit: only an Executive decides (403 <c>executive_required</c>).</summary>
    NeedsExecutive,

    /// <summary>Only the submitter resubmits (403).</summary>
    NotSubmitter,
}

/// <summary>What a claim's reviewer found: the flags to show, never a block.</summary>
/// <param name="Kind">One of <see cref="VehicleExpenseFlagKind"/>.</param>
internal sealed record ClaimFlag(VehicleExpenseFlagKind Kind, string Detail, int? RelatedExpenseId = null);

/// <summary>
/// The vehicle expense claim rules, with no database access: the amount of a fuel claim, who may review what, and
/// the review flags (duplicates, odometer plausibility). A flag is information for the reviewer: the claim goes
/// through review like any other, with the flag shown; nothing is blocked or accused by a flag.
/// </summary>
internal static class VehicleExpenseRules
{
    /// <summary>Fuel: litres × price per litre, rounded once to the piastre; every other type: the typed amount.</summary>
    public static decimal Amount(VehicleExpenseType type, decimal? fuelQuantity, decimal? unitPrice, decimal? typedAmount) =>
        type == VehicleExpenseType.Fuel ? Money.Round(fuelQuantity!.Value * unitPrice!.Value) : typedAmount!.Value;

    /// <summary>
    /// Approve, reject, ask for evidence or escalate: only while the claim waits for review, never by its submitter
    /// or its driver; an escalated claim, or one above <paramref name="executiveAbove"/> (when set), only by an Executive.
    /// </summary>
    public static ClaimDecision Review(VehicleExpense claim, int callerUserId, int? callerEmployeeId, bool callerIsExecutive, decimal executiveAbove)
    {
        if (claim.CreatedBy == callerUserId || (callerEmployeeId is { } employee && claim.DriverEmployeeId == employee))
        {
            return ClaimDecision.OwnRecord;
        }

        if (!claim.InReview)
        {
            return ClaimDecision.WrongStatus;
        }

        var needsExecutive = claim.Status == VehicleExpenseStatus.Escalated || (executiveAbove > 0 && claim.Amount > executiveAbove);
        return needsExecutive && !callerIsExecutive ? ClaimDecision.NeedsExecutive : ClaimDecision.Allowed;
    }

    /// <summary>Resubmit after an evidence request: only the submitter, only from EvidenceRequested.</summary>
    public static ClaimDecision Resubmit(VehicleExpense claim, int callerUserId)
    {
        if (claim.CreatedBy != callerUserId)
        {
            return ClaimDecision.NotSubmitter;
        }

        return claim.Status == VehicleExpenseStatus.EvidenceRequested ? ClaimDecision.Allowed : ClaimDecision.WrongStatus;
    }

    /// <summary>Reverse a posting: only an approved, posted claim, never by its submitter.</summary>
    public static ClaimDecision Reverse(VehicleExpense claim, int callerUserId)
    {
        if (claim.CreatedBy == callerUserId)
        {
            return ClaimDecision.OwnRecord;
        }

        return claim.Status == VehicleExpenseStatus.Approved && claim.PostingStatus == VehiclePostingStatus.Posted ? ClaimDecision.Allowed : ClaimDecision.WrongStatus;
    }

    /// <summary>
    /// The review flags of a new claim, from what is already recorded. <paramref name="sameInvoiceNo"/>,
    /// <paramref name="sameReceipt"/> and <paramref name="sameAmountSameDay"/> are the ids of other claims found;
    /// <paramref name="lastOdometer"/> is the last trusted reading and its date.
    /// </summary>
    public static IReadOnlyList<ClaimFlag> Flags(
        decimal odometerKm,
        DateOnly expenseDate,
        (decimal Km, DateOnly? On) lastOdometer,
        decimal implausibleKmPerDay,
        IReadOnlyList<int> sameInvoiceNo,
        IReadOnlyList<int> sameReceipt,
        IReadOnlyList<int> sameAmountSameDay)
    {
        var flags = new List<ClaimFlag>();
        flags.AddRange(sameInvoiceNo.Select(id => new ClaimFlag(VehicleExpenseFlagKind.DuplicateInvoiceNo, "The same invoice number is on another claim.", id)));
        flags.AddRange(sameReceipt.Select(id => new ClaimFlag(VehicleExpenseFlagKind.DuplicateReceipt, "The same receipt photo is on another claim.", id)));
        flags.AddRange(sameAmountSameDay.Select(id => new ClaimFlag(VehicleExpenseFlagKind.SameAmountVehicleDay, "Another claim on this vehicle has the same amount on the same day.", id)));

        if (lastOdometer.Km > 0 && odometerKm <= lastOdometer.Km)
        {
            flags.Add(new ClaimFlag(VehicleExpenseFlagKind.OdometerNotIncreasing, $"The reading {odometerKm:0.0} km is not above the last trusted reading {lastOdometer.Km:0.0} km."));
        }
        else if (lastOdometer.Km > 0 && lastOdometer.On is { } on)
        {
            var days = Math.Max(1, expenseDate.DayNumber - on.DayNumber);
            var perDay = (odometerKm - lastOdometer.Km) / days;
            if (perDay >= implausibleKmPerDay)
            {
                flags.Add(new ClaimFlag(VehicleExpenseFlagKind.ImplausibleDistance, $"{odometerKm - lastOdometer.Km:0.0} km since the last reading on {on:yyyy-MM-dd}: {perDay:0} km a day."));
            }
        }

        return flags;
    }
}
