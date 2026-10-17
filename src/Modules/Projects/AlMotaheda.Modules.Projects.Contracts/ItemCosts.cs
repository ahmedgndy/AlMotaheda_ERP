namespace AlMotaheda.Modules.Projects.Contracts;

/// <summary>
/// The recognized cost that one source (labor, or subcontract work) puts on one project item.
/// Only approved records are ever included.
/// </summary>
/// <param name="ItemId">The project item, or <c>null</c> for approved records not linked to an item yet.</param>
/// <param name="Cost">Recognized cost.</param>
/// <param name="Paid">The part of <paramref name="Cost"/> settled by payments.</param>
/// <param name="RecordCount">How many source records contribute.</param>
/// <param name="PartyCount">How many different workers or subcontractors contribute.</param>
public sealed record ItemCostTotal(int? ItemId, decimal Cost, decimal Paid, int RecordCount, int PartyCount);

/// <summary>
/// Approved internal labor, as the project items screens need it. Implemented by the Workforce module.
/// </summary>
public interface ILaborItemCosts
{
    /// <summary>One total per item that has approved labor, plus one with a <c>null</c> item for labor not linked yet.</summary>
    Task<IReadOnlyList<ItemCostTotal>> TotalsByItemAsync(int projectId, CancellationToken cancellationToken);

    /// <summary>The approved labor lines of one item, or of "not linked yet" when <paramref name="itemId"/> is <c>null</c>.</summary>
    Task<IReadOnlyList<LaborItemLine>> LinesAsync(int projectId, int? itemId, CancellationToken cancellationToken);

    /// <summary>True when any labor record, approved or not, is linked to the item. Such an item can't be deleted.</summary>
    Task<bool> HasLinksAsync(int projectId, int itemId, CancellationToken cancellationToken);
}

/// <summary>
/// One worker's approved day, or the part of it allocated to one project item.
/// </summary>
/// <param name="LineId">The attendance line: the originating approved labor record.</param>
/// <param name="SheetId">The daily sheet the line is on.</param>
/// <param name="LineNet">The whole day's net pay: base + overtime + allowances − deductions.</param>
/// <param name="Portion">The part of the day on this item (0–1]. For "not linked yet": the part with no item.</param>
/// <param name="Amount">The part of <paramref name="LineNet"/> on this item. This is the recognized cost.</param>
/// <param name="Paid">The part of the worker's payments that settles <paramref name="Amount"/>.</param>
/// <param name="PaymentState">One of <see cref="PaymentStates"/>.</param>
public sealed record LaborItemLine(
    int LineId,
    int SheetId,
    DateOnly WorkDate,
    string Shift,
    int WorkerId,
    string WorkerNameAr,
    string WorkerNameEn,
    int TradeId,
    string TradeNameAr,
    string TradeNameEn,
    string WorkType,
    decimal Days,
    decimal OvertimeHours,
    decimal DailyRate,
    decimal BaseAmount,
    decimal OvertimeAmount,
    decimal AllowancesAmount,
    decimal DeductionsAmount,
    string? DeductionReasons,
    decimal LineNet,
    decimal Portion,
    decimal Amount,
    decimal Paid,
    decimal Outstanding,
    string PaymentState,
    int RecordedByUserId);

/// <summary>
/// Approved subcontract work, as the project items screens need it. Implemented by the Subcontractors module.
/// </summary>
public interface ISubcontractItemCosts
{
    /// <summary>One total per item that has approved work, plus one with a <c>null</c> item for work not linked yet.</summary>
    Task<IReadOnlyList<ItemCostTotal>> TotalsByItemAsync(int projectId, CancellationToken cancellationToken);

    /// <summary>The approved work of one item, or of "not linked yet" when <paramref name="itemId"/> is <c>null</c>.</summary>
    Task<IReadOnlyList<SubcontractItemLine>> LinesAsync(int projectId, int? itemId, CancellationToken cancellationToken);

    /// <summary>True when any contract item or manpower sheet is linked to the item. Such an item can't be deleted.</summary>
    Task<bool> HasLinksAsync(int projectId, int itemId, CancellationToken cancellationToken);
}

/// <summary>One approved piece of subcontract work: a measurement of a contract item, or a manpower sheet.</summary>
/// <param name="RecordType"><c>Measurement</c> or <c>Manpower</c>.</param>
/// <param name="RecordId">The id of the measurement or the manpower sheet: the originating record.</param>
/// <param name="ContractItemId">The contract item measured; <c>null</c> for a manpower sheet.</param>
/// <param name="Amount">The recognized amount. The contract value is never in it.</param>
/// <param name="Paid">Payment allocations on this work. An unapplied advance is not in it.</param>
/// <param name="PaymentState">One of <see cref="PaymentStates"/>.</param>
public sealed record SubcontractItemLine(
    string RecordType,
    int RecordId,
    int ContractId,
    string? ContractNo,
    int SubcontractorId,
    string SubcontractorNameAr,
    string SubcontractorNameEn,
    string Specialty,
    int? ContractItemId,
    string Description,
    DateOnly Date,
    decimal Quantity,
    string Unit,
    decimal UnitPrice,
    decimal Amount,
    decimal Paid,
    decimal Outstanding,
    string PaymentState);

/// <summary>
/// A module whose approved records can be linked to project items one by one, beyond suppliers, labor and subcontract
/// work: equipment costs (Fleet) and allocated staff salaries (HR). Registered once per module.
/// </summary>
public interface IItemCostSource
{
    /// <summary>One of <see cref="CostGroups"/> (<see cref="CostGroups.Equipment"/> or <see cref="CostGroups.Staff"/>).</summary>
    string Group { get; }

    /// <summary>One total per item that has approved records, plus one with a <c>null</c> item for records not linked yet.</summary>
    Task<IReadOnlyList<ItemCostTotal>> TotalsByItemAsync(int projectId, CancellationToken cancellationToken);

    /// <summary>The approved records of one item, or of "not linked yet" when <paramref name="itemId"/> is <c>null</c>.</summary>
    Task<IReadOnlyList<ItemSourceLine>> LinesAsync(int projectId, int? itemId, CancellationToken cancellationToken);

    /// <summary>True when any record, approved or not, is linked to the item. Such an item can't be deleted.</summary>
    Task<bool> HasLinksAsync(int projectId, int itemId, CancellationToken cancellationToken);
}

/// <summary>One approved record of an <see cref="IItemCostSource"/>, as the item detail lists it.</summary>
/// <param name="RecordType">The record's kind, for example <c>EquipmentCost</c> or <c>SalaryAllocation</c>.</param>
/// <param name="RecordId">The originating record.</param>
/// <param name="Kind">A finer kind inside the group, for example <c>Fuel</c> or <c>SpareParts</c>.</param>
/// <param name="PaymentState">One of <see cref="PaymentStates"/>.</param>
public sealed record ItemSourceLine(
    string RecordType,
    int RecordId,
    DateOnly Date,
    string Kind,
    string Description,
    int? PartyId,
    string? PartyNameAr,
    string? PartyNameEn,
    decimal? Quantity,
    string? Unit,
    decimal Amount,
    decimal Paid,
    decimal Outstanding,
    string PaymentState);
