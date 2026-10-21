using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Office.Common;
using AlMotaheda.Modules.Office.Data;
using AlMotaheda.Modules.Office.Data.Configurations;
using AlMotaheda.Modules.Office.Features.Alerts;
using AlMotaheda.Modules.Office.Features.Attachments;
using AlMotaheda.Modules.Office.Features.Ledger;
using AlMotaheda.Modules.Office.Features.Periods;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Office.Features.Expenses;

/// <summary>
/// Office expenses paid from a custody ("تسجيل مصروف من العهدة"). The custodian records one with its receipt
/// photos; reviewers approve, return or reject it; only a fully approved expense whose price is cleared is posted to
/// the ledger, once, and only when the recorded cash covers it.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><c>POST /api/office/expenses</c> (office Create, the custodian): 201, or 200 with the first expense for a
///   retried form (same <c>idempotencyKey</c>). At least one Receipt photo is required.</item>
///   <item><c>PUT /api/office/expenses/{id}</c>: the custodian corrects a returned expense and sends it again (a new
///   review round: approvals of the previous round no longer count).</item>
///   <item><c>POST …/approve</c>, <c>…/return</c>, <c>…/reject</c>, <c>…/reverse</c> (office Approve): never by the
///   person who recorded it or holds the custody. A double tap on approve answers 200 with the same expense.</item>
/// </list>
/// </remarks>
internal static class OfficeExpenseEndpoints
{
    internal sealed record DecisionRequest(string? Note, string? RowVersion);

    internal sealed record ExpenseList(IReadOnlyList<OfficeExpenseRow> Items, int Total, int Page, int PageSize, decimal TotalAmount);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/office/expenses", ListAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("ListOfficeExpenses")
            .WithSummary("Office expenses the caller may see (custodyId, status, posting, category, from, to, vendor, search, reason, page, pageSize)");

        api.MapGet("/office/expenses/{expenseId:int}", GetAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.View)
            .WithName("GetOfficeExpense")
            .WithSummary("One office expense with its approvals, receipts and price comparison");

        api.MapPost("/office/expenses", CreateAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.Create)
            .WithName("CreateOfficeExpense")
            .WithSummary("Record an expense paid from the caller's custody, with its receipt photos (office Create)");

        api.MapPut("/office/expenses/{expenseId:int}", ResubmitAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.Create)
            .WithName("ResubmitOfficeExpense")
            .WithSummary("Correct a returned expense and send it for review again (its custodian)");

        api.MapPost("/office/expenses/{expenseId:int}/approve", ApproveAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.Approve)
            .WithName("ApproveOfficeExpense")
            .WithSummary("Approve an office expense; posts it once every approval is in (office Approve)");

        api.MapPost("/office/expenses/{expenseId:int}/return", ReturnAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.Approve)
            .WithName("ReturnOfficeExpense")
            .WithSummary("Send an office expense back to its custodian to correct (office Approve)");

        api.MapPost("/office/expenses/{expenseId:int}/reject", RejectAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.Approve)
            .WithName("RejectOfficeExpense")
            .WithSummary("Reject an office expense that is not posted (office Approve)");

        api.MapPost("/office/expenses/{expenseId:int}/reverse", ReverseAsync)
            .RequirePermission(OfficePermissions.Module, PermissionAction.Approve)
            .WithName("ReverseOfficeExpense")
            .WithSummary("Reverse a posted office expense with a linked ledger entry (office Approve)");
    }

    internal static async Task<Results<Ok<ExpenseList>, ValidationProblem, ProblemHttpResult>> ListAsync(
        HttpContext httpContext,
        OfficeDbContext db,
        IUserDirectory users,
        CancellationToken cancellationToken,
        int? custodyId = null,
        string? status = null,
        string? posting = null,
        string? category = null,
        string? from = null,
        string? to = null,
        string? vendor = null,
        string? search = null,
        string? reason = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var statusFilter = Input.OptionalName<OfficeExpenseStatus>(status, errors, "status");
        var postingFilter = Input.OptionalName<OfficePostingStatus>(posting, errors, "posting");
        var fromDate = Input.OptionalDate(from, errors, "from", "from");
        var toDate = Input.OptionalDate(to, errors, "to", "to");
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        var query = ExpenseQueries.Visible(db, caller);
        if (custodyId is { } custody)
        {
            query = query.Where(expense => expense.CustodyId == custody);
        }

        if (statusFilter is { } wantedStatus)
        {
            query = query.Where(expense => expense.Status == wantedStatus);
        }

        if (postingFilter is { } wantedPosting)
        {
            query = query.Where(expense => expense.PostingStatus == wantedPosting);
        }

        if (Input.Clean(category) is { } wantedCategory)
        {
            query = query.Where(expense => expense.Category == wantedCategory);
        }

        if (fromDate is { } first)
        {
            query = query.Where(expense => expense.ExpenseDate >= first);
        }

        if (toDate is { } last)
        {
            query = query.Where(expense => expense.ExpenseDate <= last);
        }

        if (Input.Clean(vendor) is { } wantedVendor)
        {
            query = query.Where(expense => expense.Vendor != null && expense.Vendor.Contains(wantedVendor));
        }

        if (Input.Clean(search) is { } text)
        {
            query = query.Where(expense => expense.Description.Contains(text) || (expense.InvoiceNo != null && expense.InvoiceNo.Contains(text))
                                           || (expense.Vendor != null && expense.Vendor.Contains(text)));
        }

        if (Input.Clean(reason) is { } wantedReason)
        {
            query = query.Where(expense => expense.ReviewReasons != null && expense.ReviewReasons.Contains(wantedReason));
        }

        var total = await query.CountAsync(cancellationToken);
        var totalAmount = await query.SumAsync(expense => (decimal?)expense.Amount, cancellationToken) ?? 0m;
        var rows = await ExpenseQueries.RowsAsync(db, users,
            query.OrderByDescending(expense => expense.ExpenseDate).ThenByDescending(expense => expense.Id).Skip((page - 1) * pageSize).Take(pageSize),
            cancellationToken);

        return TypedResults.Ok(new ExpenseList(rows, total, page, pageSize, totalAmount));
    }

    internal static async Task<Results<Ok<OfficeExpenseView>, ProblemHttpResult>> GetAsync(
        int expenseId, HttpContext httpContext, OfficeDbContext db, IUserDirectory users, IPriceControl priceControl, CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        return await ExpenseQueries.ViewAsync(db, users, priceControl, caller, expenseId, cancellationToken) is { } view
            ? TypedResults.Ok(view)
            : OfficeProblems.ExpenseNotFound();
    }

    internal static async Task<Results<Created<OfficeExpenseView>, Ok<OfficeExpenseView>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        OfficeExpenseRequest request,
        HttpContext httpContext,
        OfficeDbContext db,
        IUserDirectory users,
        IPriceControl priceControl,
        OfficeAlerts alerts,
        IOptions<OfficeOptions> settings,
        CompanyClock clock,
        OfficeLog log,
        CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var options = settings.Value;
        var errors = new ValidationErrors();
        if (request.CustodyId is null)
        {
            errors.Add("custodyId", "Choose the custody the money was paid from.");
        }

        var key = OfficeInput.Key(request.IdempotencyKey, errors);
        var fields = ExpenseFields.Validate(request, clock.Today, options.MaxExpenseAgeDays, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        if (await FindRepeatAsync(db, users, priceControl, caller, key!.Value, request.CustodyId!.Value, fields!.Amount, cancellationToken) is { } repeat)
        {
            return repeat;
        }

        var (verdict, priceRefusal, priceErrors) = await ExpensePricing.CheckAsync(priceControl, fields, options.PriceCategories, cancellationToken);
        if (priceErrors is not null)
        {
            return OfficeProblems.Validation(priceErrors);
        }

        if (priceRefusal is not null)
        {
            return priceRefusal;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var custody = await db.Custodies.SingleOrDefaultAsync(candidate => candidate.Id == request.CustodyId, cancellationToken);
        if (custody is null || !caller.MaySee(custody))
        {
            return OfficeProblems.CustodyNotFound();
        }

        if (!caller.Holds(custody))
        {
            return OfficeProblems.NotTheHolder();
        }

        if (!custody.IsActive)
        {
            return OfficeProblems.Inactive(custody.Status.ToString());
        }

        if (!await CustodyLedger.LockAsync(db, custody.Id, cancellationToken))
        {
            return OfficeProblems.TryAgainLater();
        }

        if (await FindRepeatAsync(db, users, priceControl, caller, key.Value, custody.Id, fields.Amount, cancellationToken) is { } committed)
        {
            return committed;
        }

        var (attachments, attachmentRefusal) = await AttachmentBinding.LoadAsync(db, fields.AttachmentIds, caller.UserId, OfficeAttachmentOwners.Expense, null, cancellationToken);
        if (attachmentRefusal is not null)
        {
            return attachmentRefusal;
        }

        // The expense is deducted in the month of its date: the current month, or a finished month not settled yet.
        var (period, periodRefusal) = await CustodyPeriods.ForDateAsync(db, custody, fields.ExpenseDate, clock.Today, clock.UtcNow, alerts, log, cancellationToken);
        if (periodRefusal is not null)
        {
            return periodRefusal;
        }

        var expense = new OfficeExpense
        {
            CustodyId = custody.Id,
            PeriodId = period!.Id,
            Category = fields.Category,
            Description = fields.Description,
            Unit = fields.Unit,
            IdempotencyKey = key.Value,
            CreatedBy = caller.UserId,
            CreatedAt = clock.UtcNow,
            SubmittedAt = clock.UtcNow,
        };

        var refusal = await ExpenseSubmission.PrepareAsync(db, expense, fields, custody, period, attachments, verdict, options, caller.UserId, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        db.Expenses.Add(expense);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, OfficeExpenseConfiguration.KeyIndex))
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return await FindRepeatAsync(db, users, priceControl, caller, key.Value, custody.Id, fields.Amount, cancellationToken) ?? OfficeProblems.KeyUsedElsewhere();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, OfficeExpenseConfiguration.SpendIndex))
        {
            return OfficeProblems.BadSpendRequest("This repair or purchase already has its expense.");
        }

        await ExpenseSubmission.FinishAsync(db, expense, custody, attachments, verdict, priceControl, alerts, caller.UserId, clock.UtcNow, log, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var view = await ExpenseQueries.ViewAsync(db, users, priceControl, caller, expense.Id, cancellationToken);
        return TypedResults.Created($"/api/office/expenses/{expense.Id}", view!);
    }

    internal static async Task<Results<Ok<OfficeExpenseView>, ValidationProblem, ProblemHttpResult>> ResubmitAsync(
        int expenseId,
        OfficeExpenseRequest request,
        HttpContext httpContext,
        OfficeDbContext db,
        IUserDirectory users,
        IPriceControl priceControl,
        OfficeAlerts alerts,
        IOptions<OfficeOptions> settings,
        CompanyClock clock,
        OfficeLog log,
        CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var options = settings.Value;
        var errors = new ValidationErrors();
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        var fields = ExpenseFields.Validate(request, clock.Today, options.MaxExpenseAgeDays, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        var (verdict, priceRefusal, priceErrors) = await ExpensePricing.CheckAsync(priceControl, fields!, options.PriceCategories, cancellationToken);
        if (priceErrors is not null)
        {
            return OfficeProblems.Validation(priceErrors);
        }

        if (priceRefusal is not null)
        {
            return priceRefusal;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var expense = await db.Expenses.SingleOrDefaultAsync(candidate => candidate.Id == expenseId, cancellationToken);
        var custody = expense is null ? null : await db.Custodies.SingleAsync(candidate => candidate.Id == expense.CustodyId, cancellationToken);
        if (expense is null || custody is null || !caller.MaySee(custody))
        {
            return OfficeProblems.ExpenseNotFound();
        }

        if (expense.CreatedBy != caller.UserId)
        {
            return OfficeProblems.NotTheHolder();
        }

        if (!custody.IsActive)
        {
            return OfficeProblems.Inactive(custody.Status.ToString());
        }

        if (!await CustodyLedger.LockAsync(db, custody.Id, cancellationToken))
        {
            return OfficeProblems.TryAgainLater();
        }

        await db.Entry(expense).ReloadAsync(cancellationToken);
        if (!expense.RowVersion.SequenceEqual(rowVersion!))
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        if (expense.Status != OfficeExpenseStatus.Returned)
        {
            return OfficeProblems.WrongStatus($"Only a returned expense can be corrected. This one is {expense.Status}.");
        }

        if (fields!.SpendRequestId != expense.SpendRequestId)
        {
            var spendErrors = new ValidationErrors();
            spendErrors.Add("spendRequestId", "The repair or purchase request of an expense can't change. Reject it and record a new one.");
            return OfficeProblems.Validation(spendErrors);
        }

        var (added, attachmentRefusal) = await AttachmentBinding.LoadAsync(db, fields.AttachmentIds, caller.UserId, OfficeAttachmentOwners.Expense, expense.Id, cancellationToken);
        if (attachmentRefusal is not null)
        {
            return attachmentRefusal;
        }

        var existing = await db.Attachments.Where(attachment => attachment.OwnerType == OfficeAttachmentOwners.Expense && attachment.OwnerId == expense.Id)
            .ToListAsync(cancellationToken);
        var all = existing.Concat(added.Where(attachment => existing.All(old => old.Id != attachment.Id))).ToList();

        // A corrected price starts clean: the cases of the previous round are withdrawn, a still-high price opens a new one.
        await priceControl.WithdrawExceptionsAsync(PriceExceptionSources.OfficeExpense, expense.Id, caller.UserId, SharedTransaction.Of(db), cancellationToken);
        expense.PriceExceptionId = null;

        var (period, periodRefusal) = await CustodyPeriods.ForDateAsync(db, custody, fields.ExpenseDate, clock.Today, clock.UtcNow, alerts, log, cancellationToken);
        if (periodRefusal is not null)
        {
            return periodRefusal;
        }

        expense.PeriodId = period!.Id;
        var refusal = await ExpenseSubmission.PrepareAsync(db, expense, fields, custody, period, all, verdict, options, caller.UserId, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        expense.MoveTo(OfficeExpenseStatus.Submitted);
        expense.Round++;
        expense.SubmittedAt = clock.UtcNow;
        expense.DecidedBy = null;
        expense.DecidedAt = null;
        await db.SaveChangesAsync(cancellationToken);

        await ExpenseSubmission.FinishAsync(db, expense, custody, all, verdict, priceControl, alerts, caller.UserId, clock.UtcNow, log, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return TypedResults.Ok((await ExpenseQueries.ViewAsync(db, users, priceControl, caller, expense.Id, cancellationToken))!);
    }

    internal static async Task<Results<Ok<OfficeExpenseView>, ValidationProblem, ProblemHttpResult>> ApproveAsync(
        int expenseId,
        DecisionRequest request,
        HttpContext httpContext,
        OfficeDbContext db,
        IUserDirectory users,
        IPriceControl priceControl,
        OfficeAlerts alerts,
        CompanyClock clock,
        OfficeLog log,
        CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = Input.OptionalText(request.Note, OfficeLengths.Note, errors, "note", "The note");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (expense, custody, refused) = await LoadForReviewAsync(db, caller, expenseId, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        // A double tap: the first approval of this round already counts. Answer as if this one was it.
        var approvals = await db.ExpenseApprovals.Where(approval => approval.ExpenseId == expense!.Id && approval.Round == expense.Round).ToListAsync(cancellationToken);
        if (approvals.Any(approval => approval.ApproverUserId == caller.UserId))
        {
            await transaction.RollbackAsync(cancellationToken);
            return TypedResults.Ok((await ExpenseQueries.ViewAsync(db, users, priceControl, caller, expenseId, cancellationToken))!);
        }

        if (!expense!.RowVersion.SequenceEqual(rowVersion!))
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        if (!expense.InReview)
        {
            return OfficeProblems.WrongStatus($"Only an expense waiting for review can be approved. This one is {expense.Status}.");
        }

        var completes = approvals.Count + 1 >= expense.RequiredApprovals;
        if (expense.RequiredApprovals == 2 && completes
            && !caller.IsExecutive && !approvals.Any(approval => approval.ApproverRole == AccessRoles.Executive))
        {
            log.ActionRefused(caller.UserId, nameof(ApproveAsync), "second approval needs the Executive");
            return OfficeProblems.NeedsExecutive();
        }

        db.ExpenseApprovals.Add(new OfficeExpenseApproval
        {
            ExpenseId = expense.Id,
            Round = expense.Round,
            ApproverUserId = caller.UserId,
            ApproverRole = caller.RoleCode,
            Note = note,
            ApprovedAt = clock.UtcNow,
        });

        if (completes)
        {
            expense.MoveTo(OfficeExpenseStatus.Approved);
            expense.DecidedBy = caller.UserId;
            expense.DecidedAt = clock.UtcNow;
            expense.DecisionNote = note;
        }
        else
        {
            expense.MoveTo(OfficeExpenseStatus.PartlyApproved);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, OfficeExpenseApprovalConfiguration.OnceIndex))
        {
            return OfficeProblems.ApprovedAlready();
        }

        log.ExpenseMoved(caller.UserId, expense.Id, expense.Status.ToString());
        if (completes)
        {
            var cleared = await PriceClearedAsync(priceControl, [expense], cancellationToken);
            await CustodyLedger.PostReadyAsync(db, custody!, [expense], cleared.Contains, caller.UserId, clock.UtcNow, alerts, log, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return TypedResults.Ok((await ExpenseQueries.ViewAsync(db, users, priceControl, caller, expense.Id, cancellationToken))!);
    }

    internal static async Task<Results<Ok<OfficeExpenseView>, ValidationProblem, ProblemHttpResult>> ReturnAsync(
        int expenseId,
        DecisionRequest request,
        HttpContext httpContext,
        OfficeDbContext db,
        IUserDirectory users,
        IPriceControl priceControl,
        CompanyClock clock,
        OfficeLog log,
        CancellationToken cancellationToken) =>
        await DecideAsync(expenseId, request, OfficeExpenseStatus.Returned, httpContext, db, users, priceControl, clock, log, cancellationToken);

    internal static async Task<Results<Ok<OfficeExpenseView>, ValidationProblem, ProblemHttpResult>> RejectAsync(
        int expenseId,
        DecisionRequest request,
        HttpContext httpContext,
        OfficeDbContext db,
        IUserDirectory users,
        IPriceControl priceControl,
        CompanyClock clock,
        OfficeLog log,
        CancellationToken cancellationToken) =>
        await DecideAsync(expenseId, request, OfficeExpenseStatus.Rejected, httpContext, db, users, priceControl, clock, log, cancellationToken);

    private static async Task<Results<Ok<OfficeExpenseView>, ValidationProblem, ProblemHttpResult>> DecideAsync(
        int expenseId,
        DecisionRequest request,
        OfficeExpenseStatus target,
        HttpContext httpContext,
        OfficeDbContext db,
        IUserDirectory users,
        IPriceControl priceControl,
        CompanyClock clock,
        OfficeLog log,
        CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = OfficeInput.Reason(request.Note, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (expense, _, refused) = await LoadForReviewAsync(db, caller, expenseId, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        if (expense!.Status == target)
        {
            await transaction.RollbackAsync(cancellationToken);
            return TypedResults.Ok((await ExpenseQueries.ViewAsync(db, users, priceControl, caller, expenseId, cancellationToken))!);
        }

        if (!expense.RowVersion.SequenceEqual(rowVersion!))
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        if (expense.PostingStatus != OfficePostingStatus.NotPosted || !expense.CanMoveTo(target))
        {
            return OfficeProblems.WrongStatus(target == OfficeExpenseStatus.Returned
                ? $"Only an expense waiting for review can be returned. This one is {expense.Status}."
                : $"Only an expense that is not posted can be rejected. Reverse a posted one instead.");
        }

        expense.MoveTo(target);
        expense.DecidedBy = caller.UserId;
        expense.DecidedAt = clock.UtcNow;
        expense.DecisionNote = note;

        if (target == OfficeExpenseStatus.Rejected)
        {
            await priceControl.WithdrawExceptionsAsync(PriceExceptionSources.OfficeExpense, expense.Id, caller.UserId, SharedTransaction.Of(db), cancellationToken);

            // The repair or purchase waits for a corrected expense again.
            if (expense.SpendRequestId is { } spendId
                && await db.SpendRequests.SingleOrDefaultAsync(spend => spend.Id == spendId, cancellationToken) is { Status: OfficeSpendStatus.Completed } spend)
            {
                spend.MoveTo(OfficeSpendStatus.Approved);
                spend.ExpenseId = null;
                spend.CompletedAt = null;
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.ExpenseMoved(caller.UserId, expense.Id, target.ToString());
        return TypedResults.Ok((await ExpenseQueries.ViewAsync(db, users, priceControl, caller, expense.Id, cancellationToken))!);
    }

    internal static async Task<Results<Ok<OfficeExpenseView>, ValidationProblem, ProblemHttpResult>> ReverseAsync(
        int expenseId,
        DecisionRequest request,
        HttpContext httpContext,
        OfficeDbContext db,
        IUserDirectory users,
        IPriceControl priceControl,
        OfficeAlerts alerts,
        CompanyClock clock,
        OfficeLog log,
        CancellationToken cancellationToken)
    {
        if (OfficeCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var reason = OfficeInput.Reason(request.Note, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return OfficeProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var (expense, custody, refused) = await LoadForReviewAsync(db, caller, expenseId, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        if (expense!.PostingStatus == OfficePostingStatus.Reversed)
        {
            await transaction.RollbackAsync(cancellationToken);
            return TypedResults.Ok((await ExpenseQueries.ViewAsync(db, users, priceControl, caller, expenseId, cancellationToken))!);
        }

        if (!expense.RowVersion.SequenceEqual(rowVersion!))
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        if (expense.PostingStatus != OfficePostingStatus.Posted)
        {
            return OfficeProblems.WrongStatus("Only a posted expense can be reversed. Reject one that is not posted.");
        }

        var now = clock.UtcNow;

        // The money comes back to the expense's month; once that month is settled and closed, to the current one.
        var current = await CustodyPeriods.CurrentAsync(db, custody!, clock.Today, now, alerts, log, cancellationToken);
        var own = await db.Periods.SingleAsync(period => period.Id == expense.PeriodId, cancellationToken);
        var target = own.AcceptsRecords ? own : current;
        if (target is null)
        {
            return OfficeProblems.MonthClosed(own.Year, own.Month);
        }

        var entry = CustodyLedger.Add(db, custody.Id, target.Id, OfficeLedgerKind.Reversal, expense.Amount, clock.Today, OfficeLedgerSources.ExpenseReversal,
            expense.Id, caller.UserId, now, reason, expense.InvoiceNo, expense.LedgerEntryId);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, OfficeLedgerEntryConfiguration.SourceIndex)
                                                  || SqlViolations.IsUnique(exception, OfficeLedgerEntryConfiguration.ReversesIndex))
        {
            return OfficeProblems.WrongStatus("This expense was already reversed.");
        }

        expense.PostingStatus = OfficePostingStatus.Reversed;
        expense.ReversalEntryId = entry.Id;
        expense.ReversedBy = caller.UserId;
        expense.ReversedAt = now;
        expense.ReversalReason = reason;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await CustodyLedger.CheckLowBalanceAsync(db, custody, alerts, log, now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        log.ExpenseReversed(caller.UserId, expense.Id);
        log.EntryPosted(caller.UserId, custody.Id, entry.Id, entry.Kind.ToString());
        return TypedResults.Ok((await ExpenseQueries.ViewAsync(db, users, priceControl, caller, expense.Id, cancellationToken))!);
    }

    /// <summary>The ids of the expenses whose price case is approved (or that have none).</summary>
    internal static async Task<HashSet<OfficeExpense>> PriceClearedAsync(IPriceControl priceControl, IReadOnlyCollection<OfficeExpense> expenses, CancellationToken cancellationToken)
    {
        var withCase = expenses.Where(expense => expense.PriceExceptionId is not null).Select(expense => expense.Id).ToList();
        var states = withCase.Count == 0
            ? new Dictionary<int, PriceExceptionState>()
            : await priceControl.StatesAsync(PriceExceptionSources.OfficeExpense, withCase, cancellationToken);
        return expenses.Where(expense => expense.PriceExceptionId is null || states.GetValueOrDefault(expense.Id)?.IsApproved == true).ToHashSet();
    }

    /// <summary>Loads an expense for a reviewer, under its custody's lock. Refuses the person who recorded it or holds the custody.</summary>
    private static async Task<(OfficeExpense? Expense, OfficeCustody? Custody, ProblemHttpResult? Refused)> LoadForReviewAsync(
        OfficeDbContext db, OfficeCaller caller, int expenseId, CancellationToken cancellationToken)
    {
        var custodyId = await db.Expenses.Where(expense => expense.Id == expenseId).Select(expense => (int?)expense.CustodyId).SingleOrDefaultAsync(cancellationToken);
        if (custodyId is null)
        {
            return (null, null, OfficeProblems.ExpenseNotFound());
        }

        if (!await CustodyLedger.LockAsync(db, custodyId.Value, cancellationToken))
        {
            return (null, null, OfficeProblems.TryAgainLater());
        }

        var expense = await db.Expenses.SingleAsync(candidate => candidate.Id == expenseId, cancellationToken);
        var custody = await db.Custodies.SingleAsync(candidate => candidate.Id == custodyId, cancellationToken);
        if (!caller.MaySee(custody))
        {
            return (null, null, OfficeProblems.ExpenseNotFound());
        }

        if (expense.CreatedBy == caller.UserId || custody.HolderUserId == caller.UserId)
        {
            return (null, null, OfficeProblems.ReviewerIsSubmitter());
        }

        return (expense, custody, null);
    }

    private static async Task<Results<Created<OfficeExpenseView>, Ok<OfficeExpenseView>, ValidationProblem, ProblemHttpResult>?> FindRepeatAsync(
        OfficeDbContext db, IUserDirectory users, IPriceControl priceControl, OfficeCaller caller, Guid key, int custodyId, decimal amount,
        CancellationToken cancellationToken)
    {
        var existing = await db.Expenses.AsNoTracking()
            .Where(expense => expense.IdempotencyKey == key)
            .Select(expense => new { expense.Id, expense.CustodyId, expense.CreatedBy, expense.Amount, expense.Round })
            .SingleOrDefaultAsync(cancellationToken);
        if (existing is null)
        {
            return null;
        }

        // The first round is what the key made; a corrected expense keeps its key but has another total.
        if (existing.CreatedBy != caller.UserId || existing.CustodyId != custodyId || (existing.Round == 1 && existing.Amount != amount))
        {
            return OfficeProblems.KeyUsedElsewhere();
        }

        return TypedResults.Ok((await ExpenseQueries.ViewAsync(db, users, priceControl, caller, existing.Id, cancellationToken))!);
    }
}

/// <summary>The part of create and resubmit that decides review and opens cases, under the custody lock.</summary>
internal static class ExpenseSubmission
{
    /// <summary>
    /// Applies the fields and the policy to the expense (risks, approvals needed, duplicates), after checking the
    /// receipts and the repair or purchase request it completes. Saves nothing.
    /// </summary>
    public static async Task<ProblemHttpResult?> PrepareAsync(
        OfficeDbContext db,
        OfficeExpense expense,
        ExpenseFields fields,
        OfficeCustody custody,
        OfficeCustodyPeriod period,
        IReadOnlyList<OfficeAttachment> attachments,
        PriceVerdict? verdict,
        OfficeOptions options,
        int userId,
        CancellationToken cancellationToken)
    {
        if (!attachments.Any(attachment => attachment.Purpose == OfficeAttachmentPurpose.Receipt))
        {
            return OfficeProblems.NoReceipt();
        }

        var reasons = ExpensePricing.Reasons(verdict).ToList();
        if (fields.Amount > options.RoutineApprovalLimit)
        {
            reasons.Add(OfficeReviewReasons.AboveLimit);
        }

        if (fields.SpendRequestId is { } spendId)
        {
            var spend = await db.SpendRequests.SingleOrDefaultAsync(candidate => candidate.Id == spendId, cancellationToken);
            if (spend is null || spend.CustodyId != custody.Id)
            {
                return OfficeProblems.SpendNotFound();
            }

            var ready = spend.Status == OfficeSpendStatus.Approved
                        || (spend.Status == OfficeSpendStatus.Submitted && spend.IsEmergency)
                        || (spend.Status == OfficeSpendStatus.Completed && spend.ExpenseId == expense.Id && expense.Id != 0);
            if (!ready)
            {
                return OfficeProblems.BadSpendRequest(spend.Status switch
                {
                    OfficeSpendStatus.Submitted => "The request is not approved yet. Wait for the approval (only an emergency repair can be recorded before).",
                    OfficeSpendStatus.Completed => "This repair or purchase already has its expense.",
                    _ => $"The request is {spend.Status}. Nothing more can be recorded on it.",
                });
            }

            if (spend.Kind == OfficeSpendKind.Repair)
            {
                reasons.Add(OfficeReviewReasons.Repair);
                var hasAfter = attachments.Any(attachment => attachment.Purpose == OfficeAttachmentPurpose.AfterRepair)
                               || await db.Attachments.AnyAsync(attachment => attachment.OwnerType == OfficeAttachmentOwners.SpendRequest
                                                                             && attachment.OwnerId == spend.Id
                                                                             && attachment.Purpose == OfficeAttachmentPurpose.AfterRepair, cancellationToken);
                if (!hasAfter && spend.AfterEvidenceWaivedReason is null)
                {
                    return OfficeProblems.NoEvidence("Take a photo of the finished repair (after the repair) before recording its cost, or ask a reviewer to waive it.");
                }
            }

            if (spend.IsEmergency)
            {
                reasons.Add(OfficeReviewReasons.Emergency);
            }

            if (spend.EstimatedAmount is { } estimate && fields.Amount > estimate)
            {
                reasons.Add(OfficeReviewReasons.AboveEstimate);
            }
        }

        var available = await CustodyLedger.AvailableAsync(db, period, options.ReservePendingExpenses, cancellationToken);
        if (fields.Amount > available)
        {
            reasons.Add(OfficeReviewReasons.AboveAvailable);
        }

        var hashes = attachments.Where(attachment => attachment.Purpose == OfficeAttachmentPurpose.Receipt).Select(attachment => attachment.Sha256).ToList();
        var duplicates = await DuplicateCheck.FindAsync(db, expense.Id == 0 ? null : expense.Id, fields, hashes, cancellationToken);
        if (duplicates.Count > 0)
        {
            reasons.Add(OfficeReviewReasons.DuplicateRisk);
        }

        fields.ApplyTo(expense);
        ExpensePricing.Apply(expense, verdict);
        expense.ReviewReasons = ExpensePolicy.Join(reasons);
        expense.RequiredApprovals = ExpensePolicy.RequiredApprovals(reasons);
        expense.DuplicateOf = DuplicateCheck.Format(duplicates);
        return null;
    }

    /// <summary>
    /// After the expense has its id: binds its photos, completes its repair or purchase request, opens the central
    /// price case when the price is above its range, and alerts the reviewers. Saves; the caller commits.
    /// </summary>
    public static async Task FinishAsync(
        OfficeDbContext db,
        OfficeExpense expense,
        OfficeCustody custody,
        IReadOnlyList<OfficeAttachment> attachments,
        PriceVerdict? verdict,
        IPriceControl priceControl,
        OfficeAlerts alerts,
        int userId,
        DateTime now,
        OfficeLog log,
        CancellationToken cancellationToken)
    {
        AttachmentBinding.Bind(attachments, OfficeAttachmentOwners.Expense, expense.Id, now);

        if (expense.SpendRequestId is { } spendId
            && await db.SpendRequests.SingleAsync(spend => spend.Id == spendId, cancellationToken) is { } spend
            && spend.Status != OfficeSpendStatus.Completed)
        {
            spend.MoveTo(OfficeSpendStatus.Completed);
            spend.ExpenseId = expense.Id;
            spend.CompletedAt = now;
        }

        if (verdict is { IsAboveRange: true, Gates: true })
        {
            await db.SaveChangesAsync(cancellationToken);
            expense.PriceExceptionId = await priceControl.OpenExceptionAsync(
                new PriceExceptionDraft(
                    PriceExceptionSources.OfficeExpense, expense.Id, null, null, expense.PriceItemId!.Value, verdict, expense.Unit, expense.Quantity,
                    expense.UnitPrice, expense.PriceJustification, $"{expense.Description} · {expense.Vendor ?? "-"} {expense.InvoiceNo ?? ""}".Trim(),
                    expense.ExpenseDate, userId),
                SharedTransaction.Of(db),
                cancellationToken);
            log.PriceCaseOpened(expense.Id, expense.PriceExceptionId.Value);
        }

        alerts.Add<OfficeExpenseSubmittedAlert>(db, await alerts.ReviewersAsync(userId, cancellationToken), custody.Id, OfficeAttachmentOwners.Expense, expense.Id, now);
        await db.SaveChangesAsync(cancellationToken);
        log.ExpenseSubmitted(userId, expense.Id, expense.Round, expense.RequiredApprovals);
    }
}
