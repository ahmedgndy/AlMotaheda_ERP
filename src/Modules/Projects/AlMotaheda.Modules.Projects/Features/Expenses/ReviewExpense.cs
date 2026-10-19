using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
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
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Projects.Features.Expenses;

/// <summary>The body of approve, return and reject. The note is required for return and reject.</summary>
internal sealed record ReviewRequest(string? Note, string? RowVersion);

/// <summary><c>POST /api/projects/{pid}/expenses/{id}/approve</c>: the record becomes a recognized cost and is frozen.</summary>
internal static class ApproveExpense
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/expenses/{expenseId:int}/approve", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.Approve)
            .WithName("ApproveExpense")
            .WithSummary("Approve a recorded supply or site expense (not your own): it becomes a cost and is frozen");

    internal static Task<Results<Ok<ExpenseView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int expenseId, ReviewRequest request, HttpContext httpContext, ProjectsDbContext db, IProjectAccess access,
        IUserDirectory users, IOptions<ProjectsOptions> options, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
        ExpenseReview.HandleAsync(ExpenseStatus.Approved, projectId, expenseId, request, httpContext, db, access, users, options, clock, log, cancellationToken);
}

/// <summary><c>POST /api/projects/{pid}/expenses/{id}/return</c>: back to the recorder to fix, with a note.</summary>
internal static class ReturnExpense
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/expenses/{expenseId:int}/return", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.Approve)
            .WithName("ReturnExpense")
            .WithSummary("Return a recorded record to its recorder to fix and resubmit (a note is required)");

    internal static Task<Results<Ok<ExpenseView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int expenseId, ReviewRequest request, HttpContext httpContext, ProjectsDbContext db, IProjectAccess access,
        IUserDirectory users, IOptions<ProjectsOptions> options, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
        ExpenseReview.HandleAsync(ExpenseStatus.Returned, projectId, expenseId, request, httpContext, db, access, users, options, clock, log, cancellationToken);
}

/// <summary><c>POST /api/projects/{pid}/expenses/{id}/reject</c>: final; the record is never a cost.</summary>
internal static class RejectExpense
{
    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/expenses/{expenseId:int}/reject", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.Approve)
            .WithName("RejectExpense")
            .WithSummary("Reject a recorded record for good (a note is required)");

    internal static Task<Results<Ok<ExpenseView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId, int expenseId, ReviewRequest request, HttpContext httpContext, ProjectsDbContext db, IProjectAccess access,
        IUserDirectory users, IOptions<ProjectsOptions> options, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
        ExpenseReview.HandleAsync(ExpenseStatus.Rejected, projectId, expenseId, request, httpContext, db, access, users, options, clock, log, cancellationToken);
}

/// <summary>
/// The one review flow behind approve, return and reject: projects Approve, not the recorder, only from
/// <c>Recorded</c>. It runs under the project lock so two reviewers can't both decide the same record, and a
/// resubmit by the recorder can't land in between.
/// </summary>
internal static class ExpenseReview
{
    public static async Task<Results<Ok<ExpenseView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        ExpenseStatus target,
        int projectId,
        int expenseId,
        ReviewRequest request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        IOptions<ProjectsOptions> options,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = target == ExpenseStatus.Approved
            ? Input.OptionalText(request.Note, ProjectExpenseConfiguration.ReviewNoteMaxLength, errors, "note", "The note")
            : Input.RequiredText(request.Note, ProjectExpenseConfiguration.ReviewNoteMaxLength, errors, "note", "The note");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
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

        var expense = await db.ProjectExpenses
            .SingleOrDefaultAsync(candidate => candidate.Id == expenseId && candidate.ProjectId == projectId, cancellationToken);
        if (expense is null)
        {
            return CoreProblems.ExpenseNotFound();
        }

        switch (ExpenseRules.Review(expense.Status, expense.CreatedBy, caller.UserId))
        {
            case ExpenseDecision.OwnRecord:
                log.ActionRefused(caller.UserId, $"Review{target}", "reviewing own record");
                return WorkspaceProblems.ReviewerIsRecorder();
            case ExpenseDecision.WrongStatus:
                return WorkspaceProblems.WrongStatus($"Only a recorded record can be reviewed. This one is {expense.Status}.");
        }

        // A purchase above its reference range, under the ExecutiveApproval policy: only an Executive approves it.
        // Returning or rejecting it stays with any reviewer. The comparison is the one copied at recording.
        if (target == ExpenseStatus.Approved && !caller.IsExecutive
            && Prices.PriceCheck.NeedsExecutive(expense.PriceCheck, options.Value.PriceCheckPolicy))
        {
            log.ActionRefused(caller.UserId, "ReviewApproved", "price above range needs an Executive");
            return PriceProblems.ApprovedByExecutive();
        }

        expense.Review(target, caller.UserId, clock.UtcNow, note);

        db.Entry(expense).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.ExpenseReviewed(caller.UserId, projectId, expense.Id, target.ToString());

        var view = await ExpenseView.LoadAsync(db, users, projectId, expense.Id, caller.Can(PermissionAction.SeeMoney), cancellationToken);
        return view is null ? CoreProblems.ExpenseNotFound() : TypedResults.Ok(view);
    }
}
