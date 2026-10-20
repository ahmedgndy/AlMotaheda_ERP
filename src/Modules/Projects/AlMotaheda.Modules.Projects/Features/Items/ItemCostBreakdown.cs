using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;
using AlMotaheda.Modules.Projects.Features.Prices;

namespace AlMotaheda.Modules.Projects.Features.Items;

/// <summary>The kinds of transaction a work-item breakdown lists. Stored nowhere; sent and received by name.</summary>
internal static class BreakdownTypes
{
    /// <summary>A supplier delivery (Projects): quantity × unit price.</summary>
    public const string Supply = "Supply";

    /// <summary>A site expense linked to the item (Projects).</summary>
    public const string SiteExpense = "SiteExpense";

    /// <summary>Materials bought in cash from custody with no registered supplier (Projects).</summary>
    public const string DirectPurchase = "DirectPurchase";

    /// <summary>A worker's approved day, or its part on the item (Workforce).</summary>
    public const string Labor = "Labor";

    /// <summary>Approved measured work or a manpower sheet of a subcontract (Subcontractors).</summary>
    public const string Subcontract = "Subcontract";

    /// <summary>An approved cost of a machine on the item: rental, fuel, maintenance, repair, transport, other (Fleet).</summary>
    public const string Equipment = "Equipment";

    public static IReadOnlyList<string> All { get; } = [Supply, DirectPurchase, SiteExpense, Labor, Subcontract, Equipment];

    /// <summary>Any letter case is accepted; the canonical name is returned.</summary>
    public static bool TryParse(string? value, out string type)
    {
        type = All.FirstOrDefault(candidate => string.Equals(candidate, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "";
        return type.Length > 0;
    }
}

/// <summary>The filters of a breakdown, after validation. Each one narrows the transactions and the summaries.</summary>
internal sealed record BreakdownFilter(
    DateOnly? From,
    DateOnly? To,
    int? SubItemId,
    int? SupplierId,
    int? WorkerId,
    int? SubcontractorId,
    string? Type,
    int? EquipmentId = null)
{
    public static BreakdownFilter None { get; } = new(null, null, null, null, null, null, null);

    public bool Keeps(BreakdownTransaction row) =>
        (From is not { } from || row.Date >= from)
        && (To is not { } to || row.Date <= to)
        && (SubItemId is not { } item || row.ItemId == item)
        && (SupplierId is not { } supplier || (row.PartyType == BreakdownParties.Supplier && row.PartyId == supplier))
        && (WorkerId is not { } worker || (row.PartyType == BreakdownParties.Worker && row.PartyId == worker))
        && (SubcontractorId is not { } sub || (row.PartyType == BreakdownParties.Subcontractor && row.PartyId == sub))
        && (EquipmentId is not { } machine || (row.PartyType == BreakdownParties.Equipment && row.PartyId == machine))
        && (Type is not { } type || row.Type == type);
}

internal static class BreakdownParties
{
    public const string Supplier = "Supplier";
    public const string Worker = "Worker";
    public const string Subcontractor = "Subcontractor";

    /// <summary>A machine of the register (Fleet): the party of an equipment cost.</summary>
    public const string Equipment = "Equipment";

    public const string None = "None";
}

/// <summary>
/// One recognized (approved) cost record on the item or one of its sub-items, whatever module owns it. Amount, paid
/// and outstanding are the record's own figures on this item: nothing here is a copy of another row.
/// </summary>
/// <param name="RecordId">The source record: an expense, a labor line, a measurement, a manpower sheet or an equipment cost.</param>
/// <param name="RecordKind">
/// <c>Measurement</c> or <c>Manpower</c> for subcontract work; the cost type (<c>Rental</c>, <c>Fuel</c>, …) for
/// equipment; <c>null</c> otherwise.
/// </param>
/// <param name="SheetId">The labor sheet of a labor line.</param>
/// <param name="Quantity">Supply quantity, labor days on this item (days × portion), subcontract quantity, or equipment litres, days, months or hours.</param>
/// <param name="Channel">Supply, site expense and equipment only: <c>Custody</c> (paid on site) or <c>Credit</c> (an invoice).</param>
internal sealed record BreakdownTransaction(
    string Type,
    int RecordId,
    string? RecordKind,
    int? SheetId,
    DateOnly Date,
    int ItemId,
    string PartyType,
    int? PartyId,
    string? PartyNameAr,
    string? PartyNameEn,
    string? Description,
    decimal? Quantity,
    string? Unit,
    decimal? UnitPrice,
    decimal Amount,
    decimal Paid,
    decimal Outstanding,
    string PaymentState,
    string? Channel,
    string? InvoiceNo,
    PriceCheckView? PriceCheck);

/// <summary>What one supplier delivered to the item, what it invoiced and what it is still owed.</summary>
/// <param name="Invoiced">Approved credit records: the supplier's invoices (the liability).</param>
/// <param name="CustodyPaid">Approved records paid on site from custody: cost and paid at once, never owed.</param>
/// <param name="Paid">Custody amounts plus allocations of payments that are not voided.</param>
/// <param name="Outstanding">Invoiced − allocations to those invoices: what the company still owes the supplier here.</param>
/// <param name="Quantities">Quantity supplied per unit (supplies only). A physical quantity, never added to money.</param>
internal sealed record BreakdownSupplier(
    int SupplierId,
    string? NameAr,
    string? NameEn,
    int RecordCount,
    decimal Invoiced,
    decimal CustodyPaid,
    decimal Paid,
    decimal Outstanding,
    IReadOnlyList<UnitQuantity> Quantities);

internal sealed record UnitQuantity(string Unit, decimal Quantity);

/// <summary>One worker's approved labor on the item.</summary>
internal sealed record BreakdownWorker(int WorkerId, string? NameAr, string? NameEn, int LineCount, decimal Days, decimal Cost, decimal Paid, decimal Outstanding);

/// <summary>One subcontractor on the item: approved work (cost), payments on it, and what is signed but not executed.</summary>
/// <param name="Committed">Remaining value of its contract lines on the item. Never cost, never in a total.</param>
internal sealed record BreakdownSubcontractor(
    int SubcontractorId, string? NameAr, string? NameEn, int RecordCount, decimal Recognized, decimal Paid, decimal Outstanding, decimal Committed);

/// <summary>
/// One machine's approved costs on the item. An equipment cost on credit is owed to its supplier through Fleet's own
/// payment, so it is never in <see cref="BreakdownSupplier"/>.
/// </summary>
internal sealed record BreakdownMachine(int EquipmentId, string? NameAr, string? NameEn, int RecordCount, decimal Cost, decimal Paid, decimal Outstanding);

/// <summary>The sums of the transactions that passed the filters, per type and overall.</summary>
internal sealed record BreakdownTotals(int RecordCount, decimal Amount, decimal Paid, decimal Outstanding, IReadOnlyList<BreakdownTypeTotal> ByType);

internal sealed record BreakdownTypeTotal(string Type, int RecordCount, decimal Amount, decimal Paid, decimal Outstanding);

/// <summary>
/// The approved budget of the item and its sub-items against their recognized cost.
/// </summary>
/// <param name="Budgeted">Approved budget lines on the item or a sub-item; 0 when there is no approved budget.</param>
/// <param name="Remaining">Budgeted − actual; <c>null</c> when nothing is budgeted (it would mean nothing).</param>
/// <param name="PercentUsed">Actual ÷ budgeted × 100, to two decimals; <c>null</c> when nothing is budgeted.</param>
internal sealed record BreakdownBudget(int? BudgetId, int? Version, decimal Budgeted, decimal Actual, decimal? Remaining, decimal? PercentUsed, bool OverBudget);

/// <summary>Records on the item that are not cost yet: supplier records recorded or returned, waiting for review.</summary>
internal sealed record BreakdownPending(int SupplierRecordCount, decimal SupplierAmount);

/// <summary>The commitment of the item: signed subcontract value not executed yet.</summary>
internal sealed record BreakdownCommitments(decimal SubcontractRemaining, IReadOnlyList<SubcontractItemCommitment> Lines);

/// <summary>
/// The arithmetic of a work-item breakdown, with no database access. The handler loads the approved records of the
/// item and its sub-items from every source and calls this; the unit tests call it with real numbers.
/// </summary>
/// <remarks>
/// <para>Every figure is a sum of persisted, approved source records. Four different facts are kept apart and never
/// added together: the budget (a plan), the commitment (signed, not executed), the recognized cost (approved
/// records) and the payments (money that left). Outstanding = recognized − paid, per record.</para>
/// <para>No double counting: a record is linked to exactly one item, so it appears once whether the main item or the
/// sub-item is opened; the commitment of a contract line counts only its value not yet executed, and its executed
/// part is already cost.</para>
/// </remarks>
internal static class ItemCostBreakdown
{
    /// <summary>The approved supplier records as transactions, figures settled by <see cref="ExpenseSettlement"/>.</summary>
    public static IEnumerable<BreakdownTransaction> FromExpenses(IEnumerable<ExpenseRow> rows) =>
        rows.Where(row => row.Status == ExpenseStatus.Approved && row.ProjectItemId is not null)
            .Select(row =>
            {
                var settlement = row.Settlement;
                var paid = ExpenseSettlement.Paid(settlement);
                return new BreakdownTransaction(
                    row.Kind != ExpenseKind.Supply ? BreakdownTypes.SiteExpense
                        : row.SupplierId is null ? BreakdownTypes.DirectPurchase : BreakdownTypes.Supply,
                    row.Id,
                    null,
                    null,
                    row.ExpenseDate,
                    row.ProjectItemId!.Value,
                    row.SupplierId is null ? BreakdownParties.None : BreakdownParties.Supplier,
                    row.SupplierId,
                    row.SupplierNameAr ?? row.PayeeName,
                    row.SupplierNameEn ?? row.PayeeName,
                    row.Kind == ExpenseKind.Supply ? row.ItemName : row.Description,
                    row.Quantity,
                    row.Unit,
                    row.UnitPrice,
                    settlement.Amount,
                    paid,
                    settlement.Amount - paid,
                    PaymentStates.Of(settlement.Amount, paid),
                    row.Channel.ToString(),
                    row.InvoiceNo,
                    PriceCheckView.From(row));
            });

    public static IEnumerable<BreakdownTransaction> FromLabor(IReadOnlyDictionary<int, IReadOnlyList<LaborItemLine>> linesByItem) =>
        linesByItem.SelectMany(pair => pair.Value.Select(line => new BreakdownTransaction(
            BreakdownTypes.Labor,
            line.LineId,
            null,
            line.SheetId,
            line.WorkDate,
            pair.Key,
            BreakdownParties.Worker,
            line.WorkerId,
            line.WorkerNameAr,
            line.WorkerNameEn,
            line.WorkType,
            Math.Round(line.Days * line.Portion, 3, MidpointRounding.AwayFromZero),
            MeasurementUnits.Day,
            line.DailyRate,
            line.Amount,
            line.Paid,
            line.Outstanding,
            line.PaymentState,
            null,
            null,
            null)));

    public static IEnumerable<BreakdownTransaction> FromSubcontracts(IReadOnlyDictionary<int, IReadOnlyList<SubcontractItemLine>> linesByItem) =>
        linesByItem.SelectMany(pair => pair.Value.Select(line => new BreakdownTransaction(
            BreakdownTypes.Subcontract,
            line.RecordId,
            line.RecordType,
            null,
            line.Date,
            pair.Key,
            BreakdownParties.Subcontractor,
            line.SubcontractorId,
            line.SubcontractorNameAr,
            line.SubcontractorNameEn,
            line.Description,
            line.Quantity,
            line.Unit,
            line.UnitPrice,
            line.Amount,
            line.Paid,
            line.Outstanding,
            line.PaymentState,
            null,
            line.ContractNo,
            null)));

    /// <summary>
    /// The approved equipment costs (Fleet) as transactions: the machine is the party, the cost type the record kind.
    /// Paid follows Fleet's rule (custody at approval, credit once its payment is recorded).
    /// </summary>
    public static IEnumerable<BreakdownTransaction> FromEquipment(IReadOnlyDictionary<int, IReadOnlyList<EquipmentItemLine>> linesByItem) =>
        linesByItem.SelectMany(pair => pair.Value.Select(line => new BreakdownTransaction(
            BreakdownTypes.Equipment,
            line.CostId,
            line.CostType,
            null,
            line.CostDate,
            pair.Key,
            BreakdownParties.Equipment,
            line.EquipmentId,
            line.EquipmentNameAr,
            line.EquipmentNameEn,
            line.Description,
            line.Quantity,
            line.Unit,
            line.UnitPrice,
            line.Amount,
            line.Paid,
            line.Outstanding,
            line.PaymentState,
            line.Channel,
            line.InvoiceNo,
            null)));

    /// <summary>The transactions that pass <paramref name="filter"/>, newest first.</summary>
    public static IReadOnlyList<BreakdownTransaction> Filter(IEnumerable<BreakdownTransaction> all, BreakdownFilter filter) =>
        all.Where(filter.Keeps)
            .OrderByDescending(row => row.Date)
            .ThenBy(row => BreakdownTypes.All.ToList().IndexOf(row.Type))
            .ThenByDescending(row => row.RecordId)
            .ToList();

    public static BreakdownTotals Totals(IReadOnlyList<BreakdownTransaction> rows) => new(
        rows.Count,
        rows.Sum(row => row.Amount),
        rows.Sum(row => row.Paid),
        rows.Sum(row => row.Outstanding),
        BreakdownTypes.All
            .Select(type =>
            {
                var ofType = rows.Where(row => row.Type == type).ToList();
                return new BreakdownTypeTotal(type, ofType.Count, ofType.Sum(row => row.Amount), ofType.Sum(row => row.Paid), ofType.Sum(row => row.Outstanding));
            })
            .ToList());

    /// <summary>
    /// Per supplier: invoiced (credit), custody, paid, outstanding and quantities. Outstanding is only ever owed on
    /// invoices: custody records were paid on site, so their outstanding is zero by the settlement rule.
    /// </summary>
    public static IReadOnlyList<BreakdownSupplier> Suppliers(IReadOnlyList<BreakdownTransaction> rows) =>
        rows.Where(row => row.PartyType == BreakdownParties.Supplier && row.PartyId is not null)
            .GroupBy(row => row.PartyId!.Value)
            .OrderBy(group => group.Key)
            .Select(group => new BreakdownSupplier(
                group.Key,
                group.First().PartyNameAr,
                group.First().PartyNameEn,
                group.Count(),
                group.Where(row => row.Channel == nameof(PaymentChannel.Credit)).Sum(row => row.Amount),
                group.Where(row => row.Channel == nameof(PaymentChannel.Custody)).Sum(row => row.Amount),
                group.Sum(row => row.Paid),
                group.Sum(row => row.Outstanding),
                group.Where(row => row.Type == BreakdownTypes.Supply && row.Unit is not null && row.Quantity is not null)
                    .GroupBy(row => row.Unit!)
                    .OrderBy(unit => unit.Key, StringComparer.Ordinal)
                    .Select(unit => new UnitQuantity(unit.Key, unit.Sum(row => row.Quantity!.Value)))
                    .ToList()))
            .ToList();

    public static IReadOnlyList<BreakdownWorker> Workers(IReadOnlyList<BreakdownTransaction> rows) =>
        rows.Where(row => row.Type == BreakdownTypes.Labor && row.PartyId is not null)
            .GroupBy(row => row.PartyId!.Value)
            .OrderBy(group => group.Key)
            .Select(group => new BreakdownWorker(
                group.Key,
                group.First().PartyNameAr,
                group.First().PartyNameEn,
                group.Count(),
                group.Sum(row => row.Quantity ?? 0),
                group.Sum(row => row.Amount),
                group.Sum(row => row.Paid),
                group.Sum(row => row.Outstanding)))
            .ToList();

    /// <summary>Per machine: its approved costs on the item, what is paid and what is still owed.</summary>
    public static IReadOnlyList<BreakdownMachine> Machines(IReadOnlyList<BreakdownTransaction> rows) =>
        rows.Where(row => row.Type == BreakdownTypes.Equipment && row.PartyId is not null)
            .GroupBy(row => row.PartyId!.Value)
            .OrderBy(group => group.Key)
            .Select(group => new BreakdownMachine(
                group.Key,
                group.First().PartyNameAr,
                group.First().PartyNameEn,
                group.Count(),
                group.Sum(row => row.Amount),
                group.Sum(row => row.Paid),
                group.Sum(row => row.Outstanding)))
            .ToList();

    /// <param name="commitments">The commitments that pass the item and subcontractor filters (dates don't apply: a commitment is "as of now").</param>
    public static IReadOnlyList<BreakdownSubcontractor> Subcontractors(
        IReadOnlyList<BreakdownTransaction> rows, IReadOnlyList<SubcontractItemCommitment> commitments)
    {
        var work = rows.Where(row => row.Type == BreakdownTypes.Subcontract && row.PartyId is not null).ToList();
        var ids = work.Select(row => row.PartyId!.Value).Concat(commitments.Select(line => line.SubcontractorId)).Distinct().Order();

        return ids.Select(id =>
            {
                var mine = work.Where(row => row.PartyId == id).ToList();
                var signed = commitments.Where(line => line.SubcontractorId == id).ToList();
                return new BreakdownSubcontractor(
                    id,
                    mine.FirstOrDefault()?.PartyNameAr ?? signed.FirstOrDefault()?.SubcontractorNameAr,
                    mine.FirstOrDefault()?.PartyNameEn ?? signed.FirstOrDefault()?.SubcontractorNameEn,
                    mine.Count,
                    mine.Sum(row => row.Amount),
                    mine.Sum(row => row.Paid),
                    mine.Sum(row => row.Outstanding),
                    signed.Sum(line => line.Remaining));
            })
            .ToList();
    }

    /// <param name="hasApprovedBudget">False when the project has no approved budget: nothing is budgeted.</param>
    public static BreakdownBudget Budget(int? budgetId, int? version, bool hasApprovedBudget, decimal budgeted, decimal actual)
    {
        if (!hasApprovedBudget || budgeted <= 0)
        {
            return new BreakdownBudget(budgetId, version, hasApprovedBudget ? budgeted : 0, actual, null, null, false);
        }

        var remaining = budgeted - actual;
        var percent = Math.Round(actual / budgeted * 100, 2, MidpointRounding.AwayFromZero);
        return new BreakdownBudget(budgetId, version, budgeted, actual, remaining, percent, remaining < 0);
    }
}
