using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Subcontractors.Data;

namespace AlMotaheda.Modules.Subcontractors.Features.Work;

/// <summary>The answer to "may this caller review this record?", with the reason when not.</summary>
internal enum ReviewDecision
{
    Allowed,

    /// <summary>The caller recorded it, so someone else must review it (409 <c>own_record</c>).</summary>
    OwnRecord,

    /// <summary>Only a recorded record can be reviewed (409 <c>invalid_status</c>).</summary>
    WrongStatus,
}

/// <summary>
/// The rules of measured work and manpower sheets (04-subcontractors.md, section 2), with no database access.
/// </summary>
internal static class WorkRules
{
    /// <summary>
    /// Measured quantities are compared with a small tolerance, so a line measured in percentages (33.33 % three
    /// times) can still be closed.
    /// </summary>
    public const decimal QuantityTolerance = 0.0005m;

    /// <summary>Approve or reject: only from Recorded, and never by the person who recorded it.</summary>
    public static ReviewDecision Review(WorkStatus status, int recorderUserId, int callerUserId)
    {
        if (recorderUserId == callerUserId)
        {
            return ReviewDecision.OwnRecord;
        }

        return status == WorkStatus.Recorded ? ReviewDecision.Allowed : ReviewDecision.WrongStatus;
    }

    /// <summary>A rejected sheet is never cost, so linking it to a project item would mean nothing.</summary>
    public static bool CanLink(WorkStatus status) => status != WorkStatus.Rejected;

    /// <summary>The quantity of a Job or Lot line measured as a percentage of the line, to three decimals.</summary>
    public static decimal QuantityFromPercent(decimal percentOfLine, decimal itemQuantity) =>
        Math.Round(percentOfLine / 100 * itemQuantity, 3, MidpointRounding.AwayFromZero);

    /// <summary>The share of the line a quantity is, in percent, for Job and Lot lines.</summary>
    public static decimal PercentOfLine(decimal quantity, decimal itemQuantity) =>
        itemQuantity == 0 ? 0 : Math.Round(quantity / itemQuantity * 100, 2, MidpointRounding.AwayFromZero);

    /// <summary>What is left to measure on an item: its quantity minus every measurement that is not rejected.</summary>
    public static decimal RemainingQuantity(decimal itemQuantity, decimal measuredNotRejected) =>
        Math.Max(0, itemQuantity - measuredNotRejected);

    /// <summary>
    /// Over-quantity is blocked: what is measured and not rejected, plus the new quantity, must fit in the item
    /// quantity (with <see cref="QuantityTolerance"/>). Checked at recording and again at approval.
    /// </summary>
    public static bool FitsItemQuantity(decimal itemQuantity, decimal measuredNotRejected, decimal newQuantity) =>
        measuredNotRejected + newQuantity <= itemQuantity + QuantityTolerance;

    /// <summary>The amount of a measurement: quantity × the unit price copied from the item, to the piastre.</summary>
    public static decimal MeasurementAmount(decimal quantity, decimal unitPrice) => Money.Round(quantity * unitPrice);

    /// <summary>The amount of a manpower sheet: workers × days × daily rate, to the piastre.</summary>
    public static decimal ManpowerAmount(int workers, decimal days, decimal dailyRate) => Money.Round(workers * days * dailyRate);

    /// <summary>
    /// The ceiling of a daily-rate contract: the sheets that are not rejected, plus the new one, must fit in the
    /// contract value.
    /// </summary>
    public static bool FitsCeiling(decimal contractValue, decimal sheetsNotRejected, decimal newAmount) =>
        sheetsNotRejected + newAmount <= contractValue;

    /// <summary>Payments that are not voided, plus the new one, must fit in the contract value.</summary>
    public static bool FitsContractValue(decimal contractValue, decimal paidNotVoided, decimal newAmount) =>
        paidNotVoided + newAmount <= contractValue;
}
