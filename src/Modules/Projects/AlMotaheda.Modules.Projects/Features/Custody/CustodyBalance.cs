using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Custody;

/// <summary>The custody permission module (seeded with the others): View, Create (ask for funding), Approve (review), See money.</summary>
internal static class CustodyPermissions
{
    public const string Module = "custody";
}

/// <summary>One movement of a custody as every calculation sees it.</summary>
/// <param name="Kind">Funding, Return, Payroll, or <c>null</c> for a purchase or site expense recorded on custody.</param>
/// <param name="Pending">A purchase not reviewed yet (recorded or returned), or a payroll outflow waiting for its settlement's review.</param>
/// <param name="Type">One of <see cref="CustodyMovementTypes"/>.</param>
internal sealed record CustodyMovement(
    DateOnly Date,
    CustodyEntryKind? Kind,
    string Type,
    decimal Amount,
    bool Pending,
    string SourceType,
    int SourceId,
    string? Description,
    int? ProjectItemId,
    string Status,
    int RecordedBy,
    DateTime RecordedAt);

/// <summary>What a custody line is, as the client shows it.</summary>
internal static class CustodyMovementTypes
{
    public const string Funding = "Funding";
    public const string Return = "Return";
    public const string Payroll = "Payroll";

    /// <summary>A material bought on site and paid in cash (a supply record on custody).</summary>
    public const string Purchase = "Purchase";

    /// <summary>A site expense paid in cash.</summary>
    public const string Expense = "Expense";
}

/// <summary>The figures of one person's custody on one project.</summary>
/// <param name="Received">Fundings paid (cash in).</param>
/// <param name="Returned">Cash handed back to the company.</param>
/// <param name="SpentApproved">Purchases and expenses approved, and payroll confirmed: used and accepted.</param>
/// <param name="SpentPending">Purchases waiting for review and payroll waiting for confirmation: the cash is gone, the review is not done.</param>
/// <param name="Balance">Received − Returned − SpentApproved − SpentPending: the cash the holder should have. Negative when the holder paid out of pocket.</param>
internal sealed record CustodyFigures(decimal Received, decimal Returned, decimal SpentApproved, decimal SpentPending, decimal Balance, int PendingCount)
{
    public static CustodyFigures Empty { get; } = new(0, 0, 0, 0, 0, 0);
}

/// <summary>
/// The custody arithmetic, with no database access. One rule for the holder's screen, the accountant's list, the
/// balance check before a purchase and the weekly payroll, so they can't disagree.
/// </summary>
/// <remarks>
/// A funding is money moved to the holder, not a cost; a purchase paid from custody is the cost (recognized when it
/// is approved, like any record) and is the outflow, once. A rejected record and a released payroll count nowhere.
/// </remarks>
internal static class CustodyBalance
{
    public static CustodyFigures Compute(IEnumerable<CustodyMovement> movements)
    {
        decimal received = 0, returned = 0, approved = 0, pending = 0;
        var pendingCount = 0;
        foreach (var movement in movements)
        {
            switch (movement.Kind)
            {
                case CustodyEntryKind.Funding:
                    received += movement.Amount;
                    break;
                case CustodyEntryKind.Return:
                    returned += movement.Amount;
                    break;
                default:
                    if (movement.Pending)
                    {
                        pending += movement.Amount;
                        pendingCount++;
                    }
                    else
                    {
                        approved += movement.Amount;
                    }

                    break;
            }
        }

        return new CustodyFigures(received, returned, approved, pending, received - returned - approved - pending, pendingCount);
    }

    /// <summary>Running balance, oldest first: cash in is positive, cash out negative.</summary>
    public static IReadOnlyList<(CustodyMovement Movement, decimal Balance)> Run(IEnumerable<CustodyMovement> movements)
    {
        decimal balance = 0;
        var lines = new List<(CustodyMovement, decimal)>();
        foreach (var movement in movements.OrderBy(movement => movement.Date).ThenBy(movement => movement.RecordedAt).ThenBy(movement => movement.SourceId))
        {
            balance += movement.Kind == CustodyEntryKind.Funding ? movement.Amount : -movement.Amount;
            lines.Add((movement, balance));
        }

        return lines;
    }
}

/// <summary>The custody queries, as <see cref="IQueryable{T}"/> so tests can prove they translate.</summary>
internal static class CustodyQueries
{
    /// <summary>
    /// The custody entries (fundings, returns, payroll) of a project, of one holder or all, that still count:
    /// released payroll is left out.
    /// </summary>
    public static IQueryable<CustodyEntry> Entries(ProjectsDbContext db, int projectId, int? holderUserId) =>
        db.CustodyEntries.AsNoTracking()
            .Where(entry => entry.ProjectId == projectId && entry.Status != CustodyEntryStatus.Released)
            .Where(entry => holderUserId == null || entry.HolderUserId == holderUserId);

    /// <summary>
    /// The purchases and site expenses paid from custody on a project (by their recorder), not rejected, with their
    /// adjusted amount (approved corrections included).
    /// </summary>
    public static IQueryable<CustodyExpenseRow> Expenses(ProjectsDbContext db, int projectId, int? holderUserId) =>
        db.ProjectExpenses.AsNoTracking()
            .Where(expense => expense.ProjectId == projectId && expense.Channel == PaymentChannel.Custody && expense.Status != ExpenseStatus.Rejected)
            .Where(expense => holderUserId == null || expense.CreatedBy == holderUserId)
            .Select(expense => new CustodyExpenseRow(
                expense.Id,
                expense.CreatedBy,
                expense.ExpenseDate,
                expense.Kind,
                expense.Status,
                expense.Amount + (expense.Adjustments.Where(adjustment => adjustment.Status == AdjustmentStatus.Approved)
                    .Sum(adjustment => (decimal?)adjustment.Amount) ?? 0),
                expense.Kind == ExpenseKind.Supply ? expense.ItemName : expense.Description,
                expense.ProjectItemId,
                expense.CreatedAt));

    /// <summary>Every movement of a project's custodies (or one holder's), flat, for <see cref="CustodyBalance"/>.</summary>
    public static async Task<IReadOnlyList<(int HolderUserId, CustodyMovement Movement)>> MovementsAsync(
        ProjectsDbContext db, int projectId, int? holderUserId, CancellationToken cancellationToken)
    {
        var entries = await Entries(db, projectId, holderUserId).ToListAsync(cancellationToken);
        var expenses = await Expenses(db, projectId, holderUserId).ToListAsync(cancellationToken);

        return entries
            .Select(entry => (entry.HolderUserId, new CustodyMovement(
                entry.EntryDate, entry.Kind, entry.Kind.ToString(), entry.Amount, entry.Status == CustodyEntryStatus.Pending, entry.SourceType ?? nameof(CustodyEntry),
                entry.SourceId ?? entry.Id, entry.Notes ?? entry.Reference, null, entry.Status.ToString(), entry.CreatedBy, entry.CreatedAt)))
            .Concat(expenses.Select(expense => (expense.HolderUserId, new CustodyMovement(
                expense.Date, null, expense.Kind == ExpenseKind.Supply ? CustodyMovementTypes.Purchase : CustodyMovementTypes.Expense, expense.Amount, expense.Status != ExpenseStatus.Approved, nameof(ProjectExpense), expense.Id, expense.Description,
                expense.ProjectItemId, expense.Status.ToString(), expense.HolderUserId, expense.CreatedAt))))
            .ToList();
    }

    /// <summary>What one holder has in hand on a project now.</summary>
    public static async Task<decimal> BalanceAsync(ProjectsDbContext db, int projectId, int holderUserId, CancellationToken cancellationToken) =>
        CustodyBalance.Compute((await MovementsAsync(db, projectId, holderUserId, cancellationToken)).Select(row => row.Movement)).Balance;
}

/// <summary>One record paid from custody.</summary>
internal sealed record CustodyExpenseRow(
    int Id, int HolderUserId, DateOnly Date, ExpenseKind Kind, ExpenseStatus Status, decimal Amount, string? Description, int? ProjectItemId, DateTime CreatedAt);

/// <summary>
/// One custody's movements run one at a time: a purchase, a payroll outflow and a return each take this lock inside
/// their transaction, so two of them can't both pass the balance check.
/// </summary>
internal static class CustodyLock
{
    public static string Name(int projectId, int holderUserId) => $"AlMotaheda.Projects.Custody.{projectId}.{holderUserId}";

    public static Task<bool> TryAcquireAsync(DbContext db, int projectId, int holderUserId, CancellationToken cancellationToken) =>
        SqlAppLock.TryAcquireAsync(db, Name(projectId, holderUserId), cancellationToken);
}
