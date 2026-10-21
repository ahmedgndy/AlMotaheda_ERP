using AlMotaheda.BuildingBlocks.Data;

namespace AlMotaheda.Modules.Finance.Contracts;

/// <summary>The kinds of custody account Finance registers (<c>finance.CustodyAccount.Kind</c>). Stored by name.</summary>
/// <remarks>
/// Finance keeps the movements of <see cref="Accountant"/> and <see cref="PayrollBatch"/> accounts itself. The
/// movements of an <see cref="EngineerProject"/> account are the project custody of the Projects module
/// (<c>projects.CustodyEntry</c>) and those of a <see cref="Vehicle"/> account are the vehicle custody of the Fleet
/// module (<c>fleet.VehicleCustodyEntry</c>): each module keeps one ledger for the money it answers for, and Finance
/// reads and writes them through <see cref="ICustodyLedgerSource"/>. <see cref="Office"/> is reserved for the office
/// petty cash of the Office module (another branch) and is not opened here.
/// </remarks>
public static class CustodyAccountKinds
{
    public const string Accountant = "Accountant";
    public const string PayrollBatch = "PayrollBatch";
    public const string EngineerProject = "EngineerProject";
    public const string Vehicle = "Vehicle";
    public const string Office = "Office";

    public static IReadOnlyList<string> All { get; } = [Accountant, PayrollBatch, EngineerProject, Vehicle, Office];

    /// <summary>Kinds whose movements live in <c>finance.CustodyMovement</c>.</summary>
    public static bool IsLedgeredByFinance(string kind) => kind is Accountant or PayrollBatch;
}

/// <summary>
/// Implemented by the module that keeps the ledger of one custody kind (Projects for <c>EngineerProject</c>, Fleet
/// for <c>Vehicle</c>). Finance asks it before a transfer (does the target exist, who holds it, what does it hold)
/// and writes the two ends of a transfer in Finance's transaction, so the pair of movements commits or rolls back
/// together. Every write names its source (<see cref="CustodyLedgerWrite.SourceType"/>, <see cref="CustodyLedgerWrite.SourceId"/>),
/// which the owning module keeps unique, so the same transfer never moves money twice.
/// </summary>
public interface ICustodyLedgerSource
{
    /// <summary>One of <see cref="CustodyAccountKinds"/>.</summary>
    string Kind { get; }

    /// <summary>
    /// The custody target, or <c>null</c> when the reference (a project, a vehicle) doesn't exist or
    /// <paramref name="holderUserId"/> may not hold it (not an active member; a vehicle has no holder user).
    /// </summary>
    Task<CustodyTarget?> FindAsync(int referenceId, int? holderUserId, CancellationToken cancellationToken);

    /// <summary>What the custody holds now: posted balance and the pending outflows not reviewed yet.</summary>
    Task<CustodyLedgerFigures> FiguresAsync(int referenceId, int? holderUserId, CancellationToken cancellationToken);

    /// <summary>Money in (a received transfer, an adjustment up), posted, in the caller's transaction, under the custody's lock.</summary>
    Task ReceiveAsync(CustodyLedgerWrite write, SharedTransaction transaction, CancellationToken cancellationToken);

    /// <summary>
    /// Money out (a return up the chain, a reversed receipt, an adjustment down), posted, in the caller's transaction,
    /// under the custody's lock. Throws <see cref="CustodyLedgerRefusedException"/> when the custody is busy, or when
    /// the amount is above the balance under policy Block.
    /// </summary>
    Task WithdrawAsync(CustodyLedgerWrite write, SharedTransaction transaction, CancellationToken cancellationToken);

    /// <summary>The custody's activity between two dates, for the central custody page (received, spent, pending, unsupported, last entry).</summary>
    Task<CustodyActivity> ActivityAsync(int referenceId, int? holderUserId, DateOnly from, DateOnly to, CancellationToken cancellationToken);

    /// <summary>One page of the custody's statement, newest first, between two optional dates.</summary>
    Task<CustodyStatementPage> StatementAsync(int referenceId, int? holderUserId, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken cancellationToken);
}

/// <summary>
/// What happened on a custody in a period, as its ledger module counts it (section 16). Every figure is a sum of
/// ledger entries, never a stored total.
/// </summary>
/// <param name="Received">Cash in: fundings and adjustments up.</param>
/// <param name="Returned">Cash handed back up the chain.</param>
/// <param name="TransferredOut">Cash handed down the chain to other custodies (an accountant funding an engineer): neither spent nor returned.</param>
/// <param name="Spent">Posted outflows: approved expenses, wages, claims (the recognised spending).</param>
/// <param name="Pending">Outflows recorded and not reviewed yet (exposure), as of now.</param>
/// <param name="Unsupported">Posted outflows with no evidence behind them (no document, receipt or invoice linked).</param>
/// <param name="LastEntryDate">The date of the latest entry of any kind, or <c>null</c> for an empty custody.</param>
public sealed record CustodyActivity(
    decimal Received, decimal Returned, decimal TransferredOut, decimal Spent, decimal Pending, int PendingCount, decimal Unsupported, int UnsupportedCount, DateOnly? LastEntryDate)
{
    public static CustodyActivity Empty { get; } = new(0, 0, 0, 0, 0, 0, 0, 0, null);
}

/// <summary>One line of a custody statement as the central page shows it, whichever module keeps the ledger.</summary>
/// <param name="Amount">Signed: positive cash in, negative cash out.</param>
/// <param name="Status"><c>Posted</c>, <c>Pending</c> or <c>Released</c>.</param>
/// <param name="SourceType">The record behind the line, in the owning module's words (for example <c>PayrollSettlement</c>, <c>VehicleExpense</c>, <c>CustodyTransfer</c>).</param>
/// <param name="HasEvidence">True when a document, receipt or invoice is linked to the record behind the line (or the line is not an expense).</param>
public sealed record CustodyStatementLine(
    DateOnly EntryDate, string Kind, string Status, decimal Amount, string SourceType, int SourceId, string? Reference, string? Notes, bool HasEvidence, int? ActorUserId, int? ProjectId);

public sealed record CustodyStatementPage(IReadOnlyList<CustodyStatementLine> Lines, int TotalCount);

/// <param name="ProjectId">The project the custody belongs to, when it has one (an engineer's project custody): decides who may see it.</param>
public sealed record CustodyTarget(int ReferenceId, int? HolderUserId, string NameAr, string NameEn, int? ProjectId);

/// <param name="Posted">What the custody holds after every posted movement (negative when the holder paid out of pocket).</param>
/// <param name="Pending">Outflows recorded but not reviewed yet (a submitted claim, a payroll waiting for confirmation): the exposure.</param>
public sealed record CustodyLedgerFigures(decimal Posted, decimal Pending);

/// <param name="Amount">Always positive; the method called says the direction.</param>
/// <param name="SourceType">One of <see cref="CustodyLedgerSources"/>.</param>
/// <param name="AccountableUserId">For a vehicle custody: left to the owning module (the accountable driver comes from the assignment history).</param>
public sealed record CustodyLedgerWrite(
    int ReferenceId,
    int? HolderUserId,
    decimal Amount,
    DateOnly EntryDate,
    string SourceType,
    int SourceId,
    string? Reference,
    string? Notes,
    int ActorUserId);

/// <summary>The records Finance writes into another module's custody ledger.</summary>
public static class CustodyLedgerSources
{
    /// <summary>A custody transfer received (money in) or returned (money out); the id is the transfer's.</summary>
    public const string Transfer = "CustodyTransfer";

    /// <summary>The reversal of a transfer: the opposite movement, with the transfer's id.</summary>
    public const string TransferReversal = "CustodyTransferReversal";

    /// <summary>A reviewed adjustment after a cash count; the id is the adjustment's.</summary>
    public const string Adjustment = "CustodyAdjustment";
}

/// <summary>The owning ledger refused a write: <see cref="Code"/> is <c>custody_busy</c> or <c>custody_insufficient</c>.</summary>
public sealed class CustodyLedgerRefusedException(string code, string message) : Exception(message)
{
    public const string Busy = "custody_busy";
    public const string Insufficient = "custody_insufficient";

    public string Code { get; } = code;
}

/// <summary>The kinds of payroll a funding batch pays (<c>finance.PayrollBatch.Kind</c>). Stored by name.</summary>
public static class PayrollBatchKinds
{
    /// <summary>Daily-paid workers of a project for a period (Workforce: labor sheets and weekly settlements).</summary>
    public const string DailyLabor = "DailyLabor";

    /// <summary>The monthly salary sheet of the office employees, engineers and staff (HR).</summary>
    public const string Salaries = "Salaries";

    public static IReadOnlyList<string> All { get; } = [DailyLabor, Salaries];
}

/// <summary>
/// Implemented by the module that calculates a payroll (Workforce for daily labor, HR for salaries). Finance asks
/// it before funding a batch and before each distribution, so a batch never pays more than what the owning
/// module's approved records say is owed. Finance never calculates pay itself.
/// </summary>
public interface IPayrollEntitlementSource
{
    /// <summary>One of <see cref="PayrollBatchKinds"/>.</summary>
    string Kind { get; }

    Task<PayrollEntitlement> EntitlementAsync(PayrollEntitlementQuery query, CancellationToken cancellationToken);
}

/// <param name="ProjectId">For daily labor: the project; for salaries: ignored.</param>
/// <param name="SalarySheetId">For salaries: the sheet; for daily labor: ignored.</param>
public sealed record PayrollEntitlementQuery(int? ProjectId, DateOnly PeriodFrom, DateOnly PeriodTo, int? SalarySheetId);

/// <param name="Exists">False when the project or sheet is unknown (the batch can't name it).</param>
/// <param name="Approved">What approved records say is owed for the period, before payments.</param>
/// <param name="Paid">What is paid on them (confirmed settlements, paid requests).</param>
/// <param name="Held">What pending settlements and open requests already hold: not paid, but taken.</param>
/// <param name="Outstanding">Approved − Paid − Held.</param>
public sealed record PayrollEntitlement(bool Exists, decimal Approved, decimal Paid, decimal Held, decimal Outstanding, string? Status = null)
{
    public static PayrollEntitlement Unknown { get; } = new(false, 0, 0, 0, 0);
}
