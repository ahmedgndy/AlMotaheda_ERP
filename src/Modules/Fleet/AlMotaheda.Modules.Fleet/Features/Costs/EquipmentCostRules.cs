using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Projects.Contracts;

namespace AlMotaheda.Modules.Fleet.Features.Costs;

/// <summary>
/// How the server sets the amount of an equipment cost (05-fleet.md, section 2). The client's amount is used only
/// for a lump sum; every other amount is computed here and rounded once, to the piastre.
/// </summary>
internal static class EquipmentCostAmounts
{
    /// <summary>Fuel: litres × price per litre.</summary>
    public static decimal Fuel(decimal liters, decimal pricePerLiter) => Money.Round(liters * pricePerLiter);

    /// <summary>A day, month or hour basis: quantity × rate.</summary>
    public static decimal ByRate(decimal quantity, decimal rate) => Money.Round(quantity * rate);

    /// <summary>The amount of one cost. The inputs must already be validated for the type and basis.</summary>
    public static decimal Compute(
        EquipmentCostType type, CostBasis? basis, decimal? quantity, decimal? rate, decimal? liters, decimal? pricePerLiter, decimal? lumpAmount)
    {
        if (type == EquipmentCostType.Fuel)
        {
            return Fuel(liters!.Value, pricePerLiter!.Value);
        }

        return basis == CostBasis.Lump ? lumpAmount!.Value : ByRate(quantity!.Value, rate!.Value);
    }
}

/// <summary>The four columns of the equipment screens: rental, fuel, maintenance (with repair), other.</summary>
internal enum CostGroup
{
    Rental,
    Fuel,
    Maintenance,
    Other,
}

internal static class EquipmentCostGroups
{
    /// <summary>Repair counts with maintenance; transport and other costs count as other (the prototype's grouping).</summary>
    public static CostGroup Of(EquipmentCostType type) => type switch
    {
        EquipmentCostType.Rental => CostGroup.Rental,
        EquipmentCostType.Fuel => CostGroup.Fuel,
        EquipmentCostType.Maintenance or EquipmentCostType.Repair => CostGroup.Maintenance,
        _ => CostGroup.Other,
    };
}

/// <summary>
/// One equipment cost as every calculation sees it: the columns that decide what it costs and what is paid.
/// Loaded flat, per project, by <see cref="EquipmentCostQueries.SettlementRows"/>.
/// </summary>
/// <param name="PaymentRecorded">True when the one payment of a credit cost is recorded (and not voided).</param>
/// <param name="ProjectItemId">The work item of the cost; <c>null</c> for a cost of the whole site.</param>
internal sealed record EquipmentCostSettlementRow(
    int CostId,
    int EquipmentId,
    EquipmentCostType Type,
    EquipmentCostStatus Status,
    CostChannel Channel,
    DateOnly CostDate,
    decimal Amount,
    decimal? Liters,
    bool PaymentRecorded,
    int? ProjectItemId = null);

/// <summary>
/// The one rule for what an equipment cost costs and what of it is paid (overview, sections 3.1 and 3.3). The
/// lists, the summary and the figures given to the Projects module all go through here, so they can't disagree.
/// </summary>
internal static class EquipmentCostSettlement
{
    /// <summary>The recognized cost: the amount once approved, otherwise nothing.</summary>
    public static decimal Recognized(EquipmentCostStatus status, decimal amount) =>
        status == EquipmentCostStatus.Approved ? amount : 0;

    /// <summary>
    /// What is paid: custody when approved (the cash left on site); credit only when its one payment for the full
    /// amount is recorded (decision F2). Nothing is paid on a cost that is not approved.
    /// </summary>
    public static decimal Paid(EquipmentCostStatus status, CostChannel channel, decimal amount, bool paymentRecorded)
    {
        if (status != EquipmentCostStatus.Approved)
        {
            return 0;
        }

        return channel == CostChannel.Custody || paymentRecorded ? amount : 0;
    }

    public static decimal Recognized(EquipmentCostSettlementRow row) => Recognized(row.Status, row.Amount);

    public static decimal Paid(EquipmentCostSettlementRow row) => Paid(row.Status, row.Channel, row.Amount, row.PaymentRecorded);

    public static decimal Outstanding(EquipmentCostSettlementRow row) => Recognized(row) - Paid(row);

    /// <summary>The payment state of an approved cost; <c>null</c> while nothing is owed because nothing is approved.</summary>
    public static string? PaymentState(EquipmentCostSettlementRow row) =>
        row.Status == EquipmentCostStatus.Approved ? PaymentStates.Of(row.Amount, Paid(row)) : null;

    /// <summary>
    /// The equipment group's figures for the Projects module: recognized = approved costs, paid as above, pending
    /// review = recorded costs (returned ones wait for their recorder, not a reviewer), committed = 0.
    /// </summary>
    public static ProjectCostFigures Figures(IEnumerable<EquipmentCostSettlementRow> rows)
    {
        decimal recognized = 0, paid = 0, pending = 0;
        int recognizedCount = 0, pendingCount = 0;

        foreach (var row in rows)
        {
            if (row.Status == EquipmentCostStatus.Approved)
            {
                recognized += row.Amount;
                paid += Paid(row);
                recognizedCount++;
            }
            else if (row.Status == EquipmentCostStatus.Recorded)
            {
                pending += row.Amount;
                pendingCount++;
            }
        }

        return new ProjectCostFigures(recognized, paid, recognizedCount, pending, pendingCount, Committed: 0);
    }

    /// <summary>
    /// The approved costs per work item, for the Projects items screens: one total per item, plus one with a
    /// <c>null</c> item for the costs of the whole site. Cost and paid follow the rule of <see cref="Figures"/>, so the
    /// totals add up to its recognized and paid amounts: linking a cost to an item moves it, never adds it twice.
    /// The party count is the number of different machines.
    /// </summary>
    public static IReadOnlyList<ItemCostTotal> ItemTotals(IEnumerable<EquipmentCostSettlementRow> rows) =>
        rows.Where(row => row.Status == EquipmentCostStatus.Approved)
            .GroupBy(row => row.ProjectItemId)
            .OrderBy(group => group.Key is null)
            .ThenBy(group => group.Key)
            .Select(group => new ItemCostTotal(
                group.Key,
                group.Sum(row => row.Amount),
                group.Sum(Paid),
                group.Count(),
                group.Select(row => row.EquipmentId).Distinct().Count()))
            .ToList();
}

/// <summary>The answer to "may this caller do this to this cost?", with the reason when not.</summary>
internal enum CostDecision
{
    Allowed,

    /// <summary>The caller recorded the cost, so someone else must review it (409 <c>own_record</c>).</summary>
    OwnRecord,

    /// <summary>Only the recorder may fix and resubmit (403).</summary>
    NotRecorder,

    /// <summary>The cost is not in a status (or channel) that allows this (409 <c>invalid_status</c>).</summary>
    WrongStatus,

    /// <summary>The cost already has its payment (409 <c>already_paid</c>).</summary>
    AlreadyPaid,

    /// <summary>The cost has no payment to void (409 <c>not_paid</c>).</summary>
    NotPaid,
}

/// <summary>
/// The equipment cost workflow, with no database access. The same as project expenses: Recorded → Approved,
/// Returned or Rejected by a reviewer who is not the recorder; Returned → Recorded again by the recorder.
/// </summary>
internal static class EquipmentCostRules
{
    /// <summary>Approve, return or reject: only from Recorded, and never by the person who recorded it.</summary>
    public static CostDecision Review(EquipmentCostStatus status, int recorderUserId, int callerUserId)
    {
        if (recorderUserId == callerUserId)
        {
            return CostDecision.OwnRecord;
        }

        return status == EquipmentCostStatus.Recorded ? CostDecision.Allowed : CostDecision.WrongStatus;
    }

    /// <summary>Fix and resubmit: only the recorder, only while the cost is Recorded or Returned.</summary>
    public static CostDecision Resubmit(EquipmentCostStatus status, int recorderUserId, int callerUserId)
    {
        if (recorderUserId != callerUserId)
        {
            return CostDecision.NotRecorder;
        }

        return status is EquipmentCostStatus.Recorded or EquipmentCostStatus.Returned ? CostDecision.Allowed : CostDecision.WrongStatus;
    }

    /// <summary>Recording the payment: only an approved credit cost, and only once.</summary>
    public static CostDecision Pay(EquipmentCostStatus status, CostChannel channel, bool paymentRecorded)
    {
        if (status != EquipmentCostStatus.Approved || channel != CostChannel.Credit)
        {
            return CostDecision.WrongStatus;
        }

        return paymentRecorded ? CostDecision.AlreadyPaid : CostDecision.Allowed;
    }

    /// <summary>Voiding the payment: only when there is one.</summary>
    public static CostDecision VoidPayment(bool paymentRecorded) => paymentRecorded ? CostDecision.Allowed : CostDecision.NotPaid;
}
