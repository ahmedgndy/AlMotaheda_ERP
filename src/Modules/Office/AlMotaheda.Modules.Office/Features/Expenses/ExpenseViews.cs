using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Office.Common;
using AlMotaheda.Modules.Office.Data;
using AlMotaheda.Modules.Office.Features.Attachments;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Office.Features.Expenses;

internal sealed record OfficeExpensePriceView(
    string? Result,
    int? RangeId,
    decimal? ReferencePrice,
    decimal? MinPrice,
    decimal? MaxPrice,
    decimal? VariancePerUnit,
    decimal? VarianceTotal,
    decimal? VariancePercent,
    string? Justification,
    int? ExceptionId,
    string? ExceptionStatus);

internal sealed record OfficeApprovalView(OfficePersonRef? Approver, string Role, int Round, DateTime ApprovedAt, string? Note);

/// <summary>What the caller may do with the expense now; the server checks again on every action.</summary>
internal sealed record OfficeExpenseActions(bool Approve, bool Return, bool Reject, bool Resubmit, bool Reverse);

internal sealed record OfficeExpenseView(
    int Id,
    int CustodyId,
    string CustodyNameAr,
    string CustodyNameEn,
    string Category,
    int? PriceItemId,
    string Description,
    string? Purpose,
    string? Vendor,
    string? InvoiceNo,
    decimal Quantity,
    string Unit,
    decimal UnitPrice,
    decimal Discount,
    decimal Tax,
    decimal Amount,
    DateOnly ExpenseDate,
    int? SpendRequestId,
    OfficeExpensePriceView? Price,
    int RequiredApprovals,
    IReadOnlyList<string> ReviewReasons,
    IReadOnlyList<int> DuplicateOf,
    int Round,
    string Status,
    string PostingStatus,
    bool WaitingForPrice,
    bool WaitingForFunds,
    OfficePersonRef? CreatedBy,
    DateTime CreatedAt,
    DateTime SubmittedAt,
    IReadOnlyList<OfficeApprovalView> Approvals,
    OfficePersonRef? DecidedBy,
    DateTime? DecidedAt,
    string? DecisionNote,
    DateTime? PostedAt,
    long? LedgerEntryId,
    OfficePersonRef? ReversedBy,
    DateTime? ReversedAt,
    string? ReversalReason,
    IReadOnlyList<OfficeAttachmentView> Attachments,
    OfficeExpenseActions Actions,
    string RowVersion);

internal sealed record OfficeExpenseRow(
    int Id,
    int CustodyId,
    string Category,
    string Description,
    string? Vendor,
    string? InvoiceNo,
    decimal Amount,
    DateOnly ExpenseDate,
    string Status,
    string PostingStatus,
    int RequiredApprovals,
    int ApprovalsThisRound,
    IReadOnlyList<string> ReviewReasons,
    bool HasDuplicates,
    string? PriceCheck,
    OfficePersonRef? CreatedBy,
    DateTime SubmittedAt);

internal static class ExpenseQueries
{
    /// <summary>The expenses the caller may see: every custody for reviewers and finance, otherwise the custodies they hold.</summary>
    public static IQueryable<OfficeExpense> Visible(OfficeDbContext db, OfficeCaller caller) =>
        caller.SeesAll
            ? db.Expenses.AsNoTracking()
            : db.Expenses.AsNoTracking().Where(expense => db.Custodies.Any(custody => custody.Id == expense.CustodyId && custody.HolderUserId == caller.UserId));

    public static async Task<OfficeExpenseView?> ViewAsync(
        OfficeDbContext db, IUserDirectory users, IPriceControl priceControl, OfficeCaller caller, int expenseId, CancellationToken cancellationToken)
    {
        var expense = await Visible(db, caller).SingleOrDefaultAsync(candidate => candidate.Id == expenseId, cancellationToken);
        if (expense is null)
        {
            return null;
        }

        var custody = await db.Custodies.AsNoTracking().SingleAsync(candidate => candidate.Id == expense.CustodyId, cancellationToken);
        var approvals = await db.ExpenseApprovals.AsNoTracking()
            .Where(approval => approval.ExpenseId == expenseId)
            .OrderBy(approval => approval.Round).ThenBy(approval => approval.ApprovedAt)
            .ToListAsync(cancellationToken);
        var attachments = await OfficeAttachmentView.OfAsync(db, OfficeAttachmentOwners.Expense, expenseId, cancellationToken);

        PriceExceptionState? state = null;
        if (expense.PriceExceptionId is not null)
        {
            var states = await priceControl.StatesAsync(PriceExceptionSources.OfficeExpense, [expense.Id], cancellationToken);
            state = states.GetValueOrDefault(expense.Id);
        }

        var people = await users.FindAsync(
            new int?[] { expense.CreatedBy, expense.DecidedBy, expense.ReversedBy }
                .Concat(approvals.Select(approval => (int?)approval.ApproverUserId))
                .Where(id => id is not null).Select(id => id!.Value).Distinct().ToList(),
            cancellationToken);

        var priceCleared = expense.PriceExceptionId is null || state?.IsApproved == true;
        var waitingForPrice = expense.Status != OfficeExpenseStatus.Rejected && expense.PostingStatus == OfficePostingStatus.NotPosted && !priceCleared;
        var isOwn = expense.CreatedBy == caller.UserId || custody.HolderUserId == caller.UserId;
        var approvedThisRound = approvals.Any(approval => approval.Round == expense.Round && approval.ApproverUserId == caller.UserId);
        var reviewer = caller.IsReviewer && !isOwn;

        return new OfficeExpenseView(
            expense.Id, expense.CustodyId, custody.NameAr, custody.NameEn, expense.Category, expense.PriceItemId, expense.Description, expense.Purpose,
            expense.Vendor, expense.InvoiceNo, expense.Quantity, expense.Unit, expense.UnitPrice, expense.Discount, expense.Tax, expense.Amount,
            expense.ExpenseDate, expense.SpendRequestId,
            expense.PriceItemId is null
                ? null
                : new OfficeExpensePriceView(expense.PriceCheck, expense.PriceRangeId, expense.ReferencePrice, expense.ReferenceMinPrice, expense.ReferenceMaxPrice,
                    expense.VariancePerUnit, expense.VarianceTotal, expense.VariancePercent, expense.PriceJustification, expense.PriceExceptionId, state?.Status),
            expense.RequiredApprovals, ExpensePolicy.Split(expense.ReviewReasons), DuplicateCheck.Parse(expense.DuplicateOf), expense.Round,
            expense.Status.ToString(), expense.PostingStatus.ToString(), waitingForPrice,
            expense.Status == OfficeExpenseStatus.Approved && expense.PostingStatus == OfficePostingStatus.NotPosted && priceCleared,
            OfficePersonRef.From(expense.CreatedBy, people), expense.CreatedAt, expense.SubmittedAt,
            approvals.Select(approval => new OfficeApprovalView(OfficePersonRef.From(approval.ApproverUserId, people), approval.ApproverRole, approval.Round,
                approval.ApprovedAt, approval.Note)).ToList(),
            OfficePersonRef.From(expense.DecidedBy, people), expense.DecidedAt, expense.DecisionNote, expense.PostedAt, expense.LedgerEntryId,
            OfficePersonRef.From(expense.ReversedBy, people), expense.ReversedAt, expense.ReversalReason, attachments,
            new OfficeExpenseActions(
                reviewer && expense.InReview && !approvedThisRound,
                reviewer && expense.InReview,
                reviewer && (expense.InReview || (expense.Status == OfficeExpenseStatus.Approved && expense.PostingStatus == OfficePostingStatus.NotPosted)),
                expense.CreatedBy == caller.UserId && expense.Status == OfficeExpenseStatus.Returned && custody.IsActive,
                reviewer && expense.PostingStatus == OfficePostingStatus.Posted),
            RowVersions.Format(expense.RowVersion));
    }

    public static async Task<IReadOnlyList<OfficeExpenseRow>> RowsAsync(
        OfficeDbContext db, IUserDirectory users, IQueryable<OfficeExpense> query, CancellationToken cancellationToken)
    {
        var rows = await query
            .Select(expense => new
            {
                Expense = expense,
                Approvals = db.ExpenseApprovals.Count(approval => approval.ExpenseId == expense.Id && approval.Round == expense.Round),
            })
            .ToListAsync(cancellationToken);
        var people = await users.FindAsync(rows.Select(row => row.Expense.CreatedBy).Distinct().ToList(), cancellationToken);
        return rows.Select(row => new OfficeExpenseRow(
                row.Expense.Id, row.Expense.CustodyId, row.Expense.Category, row.Expense.Description, row.Expense.Vendor, row.Expense.InvoiceNo,
                row.Expense.Amount, row.Expense.ExpenseDate, row.Expense.Status.ToString(), row.Expense.PostingStatus.ToString(),
                row.Expense.RequiredApprovals, row.Approvals, ExpensePolicy.Split(row.Expense.ReviewReasons), row.Expense.DuplicateOf is not null,
                row.Expense.PriceCheck, OfficePersonRef.From(row.Expense.CreatedBy, people), row.Expense.SubmittedAt))
            .ToList();
    }
}
