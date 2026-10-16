using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Budgets;

/// <summary>
/// <c>POST /api/projects/{pid}/budgets/{id}/approve</c>: the draft becomes the budget in force. The previous
/// approved version becomes <c>Superseded</c> in the same transaction, under the project lock, so the project
/// always has at most one approved budget. Changing a budget never touches any transaction.
/// </summary>
internal static class ApproveBudget
{
    internal sealed record Request(string? RowVersion);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/budgets/{budgetId:int}/approve", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.Approve)
            .WithName("ApproveBudget")
            .WithSummary("Approve a draft budget; the previous approved version becomes Superseded (Executive only)");

    internal static async Task<Results<Ok<BudgetView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int budgetId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
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

        if (BudgetRules.ApproveProblem(project) is { } refusal)
        {
            log.ActionRefused(caller.UserId, nameof(ApproveBudget), "not an Executive");
            return refusal;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await ProjectLock.TryAcquireAsync(db, projectId, cancellationToken))
        {
            return ProjectProblems.TryAgainLater();
        }

        var budget = await db.Budgets
            .SingleOrDefaultAsync(candidate => candidate.Id == budgetId && candidate.ProjectId == projectId, cancellationToken);
        if (budget is null)
        {
            return PlanningProblems.BudgetNotFound();
        }

        if (budget.Status != BudgetStatus.Draft)
        {
            return PlanningProblems.NotADraft();
        }

        if (!await db.BudgetLines.AnyAsync(line => line.BudgetId == budget.Id, cancellationToken))
        {
            return PlanningProblems.NoLines();
        }

        var now = clock.UtcNow;

        // The old approved version leaves first: the filtered unique index allows one approved budget per project.
        var previous = await db.Budgets
            .SingleOrDefaultAsync(candidate => candidate.ProjectId == projectId && candidate.Status == BudgetStatus.Approved, cancellationToken);
        if (previous is not null)
        {
            previous.Supersede(caller.UserId, now);
            await db.SaveChangesAsync(cancellationToken);
        }

        // A stale rowVersion rolls the whole transaction back, the supersede included.
        db.Entry(budget).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        budget.Approve(caller.UserId, now);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.BudgetApproved(caller.UserId, projectId, budget.Id, budget.Version, previous?.Id);

        var view = await BudgetView.LoadAsync(db, users, projectId, budget.Id, cancellationToken);
        return view is null ? PlanningProblems.BudgetNotFound() : TypedResults.Ok(view);
    }
}
