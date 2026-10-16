using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Expenses;

/// <summary>
/// The correction rules of an approved record (refactor plan M6), with no database access: the corrected amount must
/// stay at or above zero and at or above what is paid or held by open payment requests.
/// </summary>
internal static class AdjustmentRules
{
    /// <summary>The lowest the adjusted amount may go: what is paid plus what open requests hold (never below zero).</summary>
    public static decimal Floor(decimal paid, decimal reserved) => Math.Max(0, paid + reserved);

    public static bool Fits(decimal adjustedAmount, decimal change, decimal paid, decimal reserved) =>
        adjustedAmount + change >= Floor(paid, reserved);
}

/// <summary>
/// <c>POST /api/projects/{pid}/expenses/{id}/adjustments</c>: ask to correct an approved supply or expense by a signed
/// amount, with a reason. Its money stays frozen; the adjustment waits for an Executive.
/// </summary>
/// <remarks>Needs projects Create and the project in scope; one open adjustment per record (409 <c>adjustment_open</c>).</remarks>
internal static class RequestExpenseAdjustment
{
    internal sealed record Request(decimal? Amount, string? Reason);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/expenses/{expenseId:int}/adjustments", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.Create)
            .WithName(nameof(RequestExpenseAdjustment))
            .WithSummary("Ask to correct an approved supply or expense by a signed amount, with a reason; an Executive decides");

    internal static async Task<Results<Created<ExpenseView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int expenseId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IPaymentRequests payments,
        IUserDirectory users,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var amount = request.Amount;
        if (amount is null or 0)
        {
            errors.Add("amount", "The amount is required and can't be zero: negative lowers the record, positive raises it.");
        }
        else if (Math.Abs(amount.Value) > NumberInput.MaxMoney || decimal.Round(amount.Value, 2) != amount.Value)
        {
            errors.Add("amount", $"The amount must have at most two decimals and be at most {NumberInput.MaxMoney:0.00} either way.");
        }

        var reason = Input.RequiredText(request.Reason, ExpenseAdjustmentConfiguration.ReasonMaxLength, errors, "reason", "The reason");
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return ProjectProblems.CompletedProject();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await ProjectLock.TryAcquireAsync(db, projectId, cancellationToken))
        {
            return ProjectProblems.TryAgainLater();
        }

        if (await AdjustmentChecks.CheckAsync(db, payments, projectId, expenseId, amount!.Value, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        if (await db.ExpenseAdjustments.AnyAsync(
                adjustment => adjustment.ExpenseId == expenseId && adjustment.Status == AdjustmentStatus.Requested, cancellationToken))
        {
            return CoreProblems.AdjustmentAlreadyOpen();
        }

        var adjustment = new ExpenseAdjustment
        {
            ExpenseId = expenseId,
            Amount = amount.Value,
            Reason = reason!,
            RequestedAt = clock.UtcNow,
            RequestedBy = caller.UserId,
        };
        db.ExpenseAdjustments.Add(adjustment);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, ExpenseAdjustmentConfiguration.OneOpenIndex))
        {
            return CoreProblems.AdjustmentAlreadyOpen();
        }

        await transaction.CommitAsync(cancellationToken);
        log.ExpenseAdjustmentRequested(caller.UserId, projectId, expenseId, adjustment.Id);

        var view = await ExpenseView.LoadAsync(db, users, projectId, expenseId, cancellationToken);
        return view is null ? CoreProblems.ExpenseNotFound() : TypedResults.Created($"/api/projects/{projectId}/expenses/{expenseId}", view);
    }
}

/// <summary>
/// <c>POST /api/projects/{pid}/expense-adjustments/{id}/approve</c> and <c>…/reject</c>: an Executive decides a
/// correction (never the person who asked for it). Approval re-checks, under the project lock, that the corrected
/// amount still covers what is paid and requested.
/// </summary>
internal static class DecideExpenseAdjustment
{
    internal sealed record Request(string? Note, string? RowVersion);

    public static void Map(IEndpointRouteBuilder projects)
    {
        projects.MapPost("/{projectId:int}/expense-adjustments/{adjustmentId:int}/approve", ApproveAsync)
            .RequireExecutive()
            .WithName("ApproveExpenseAdjustment")
            .WithSummary("Approve a correction of a record (Executive, not its requester): every figure then uses the corrected amount");

        projects.MapPost("/{projectId:int}/expense-adjustments/{adjustmentId:int}/reject", RejectAsync)
            .RequireExecutive()
            .WithName("RejectExpenseAdjustment")
            .WithSummary("Reject a correction of a record with a note (Executive, not its requester)");
    }

    internal static Task<Results<Ok<ExpenseView>, ValidationProblem, ProblemHttpResult>> ApproveAsync(
        int projectId, int adjustmentId, Request request, HttpContext httpContext, ProjectsDbContext db, IProjectAccess access,
        IPaymentRequests payments, ExecutiveGate gate, IUserDirectory users, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
        DecideAsync(AdjustmentStatus.Approved, projectId, adjustmentId, request, httpContext, db, access, payments, gate, users, clock, log, cancellationToken);

    internal static Task<Results<Ok<ExpenseView>, ValidationProblem, ProblemHttpResult>> RejectAsync(
        int projectId, int adjustmentId, Request request, HttpContext httpContext, ProjectsDbContext db, IProjectAccess access,
        IPaymentRequests payments, ExecutiveGate gate, IUserDirectory users, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
        DecideAsync(AdjustmentStatus.Rejected, projectId, adjustmentId, request, httpContext, db, access, payments, gate, users, clock, log, cancellationToken);

    private static async Task<Results<Ok<ExpenseView>, ValidationProblem, ProblemHttpResult>> DecideAsync(
        AdjustmentStatus outcome,
        int projectId,
        int adjustmentId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IPaymentRequests payments,
        ExecutiveGate gate,
        IUserDirectory users,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = outcome == AdjustmentStatus.Rejected
            ? Input.RequiredText(request.Note, ProjectExpenseConfiguration.ReviewNoteMaxLength, errors, "note", "The note")
            : Input.OptionalText(request.Note, ProjectExpenseConfiguration.ReviewNoteMaxLength, errors, "note", "The note");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        // The money a correction moves is a management decision: re-checked in the database, like membership changes.
        if (await gate.CheckAsync(httpContext.User, $"Decide{nameof(ExpenseAdjustment)}", cancellationToken) is { } notExecutive)
        {
            return notExecutive;
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await ProjectLock.TryAcquireAsync(db, projectId, cancellationToken))
        {
            return ProjectProblems.TryAgainLater();
        }

        var adjustment = await db.ExpenseAdjustments.SingleOrDefaultAsync(
            candidate => candidate.Id == adjustmentId
                         && db.ProjectExpenses.Any(expense => expense.Id == candidate.ExpenseId && expense.ProjectId == projectId),
            cancellationToken);
        if (adjustment is null)
        {
            return CoreProblems.AdjustmentNotFound();
        }

        if (adjustment.RequestedBy == caller.UserId)
        {
            log.ActionRefused(caller.UserId, $"Decide{nameof(ExpenseAdjustment)}", "deciding own adjustment");
            return WorkspaceProblems.ReviewerIsRecorder();
        }

        if (adjustment.Status != AdjustmentStatus.Requested)
        {
            return WorkspaceProblems.WrongStatus($"This correction was already decided: {adjustment.Status}.");
        }

        if (outcome == AdjustmentStatus.Approved
            && await AdjustmentChecks.CheckAsync(db, payments, projectId, adjustment.ExpenseId, adjustment.Amount, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        adjustment.Decide(outcome, caller.UserId, clock.UtcNow, note);
        db.Entry(adjustment).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.ExpenseAdjustmentDecided(caller.UserId, projectId, adjustment.ExpenseId, adjustment.Id, outcome.ToString());

        var view = await ExpenseView.LoadAsync(db, users, projectId, adjustment.ExpenseId, cancellationToken);
        return view is null ? CoreProblems.ExpenseNotFound() : TypedResults.Ok(view);
    }
}

/// <summary>The checks a correction shares between asking and approving: an approved record, and the amount floor.</summary>
internal static class AdjustmentChecks
{
    /// <summary>Returns the problem to answer with, or <c>null</c>. Runs inside the caller's transaction, under the project lock.</summary>
    public static async Task<ProblemHttpResult?> CheckAsync(
        ProjectsDbContext db, IPaymentRequests payments, int projectId, int expenseId, decimal change, CancellationToken cancellationToken)
    {
        var row = await ExpenseQueries.SettlementRows(db, projectId, [expenseId]).SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return CoreProblems.ExpenseNotFound();
        }

        if (row.Status != ExpenseStatus.Approved)
        {
            return WorkspaceProblems.WrongStatus($"Only an approved record is corrected by an adjustment. This one is {row.Status}; edit it instead.");
        }

        // Custody records are paid on site at approval: their paid amount follows the corrected amount.
        var paid = row.Channel == PaymentChannel.Credit ? row.Allocated : 0;
        var reserved = row.Channel == PaymentChannel.Credit
            ? (await payments.ReservedAsync(PaidRecordTypes.ProjectExpense, [expenseId], SharedTransaction.Of(db), cancellationToken)).GetValueOrDefault(expenseId)
            : 0;

        return AdjustmentRules.Fits(row.Amount, change, paid, reserved)
            ? null
            : CoreProblems.AdjustmentBelowPaidAmount(AdjustmentRules.Floor(paid, reserved));
    }
}
