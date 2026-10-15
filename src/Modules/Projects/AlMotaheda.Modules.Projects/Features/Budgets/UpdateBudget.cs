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
/// <c>PUT /api/projects/{pid}/budgets/{id}</c>: replace the title, the notes and every line of a draft. Only a
/// draft changes: an approved or superseded version never does, so a comparison made against it stays true.
/// </summary>
internal static class UpdateBudget
{
    /// <param name="Lines">The whole new list of lines (send <c>[]</c> for none). The old lines are replaced.</param>
    internal sealed record Request(string? Title, string? Notes, IReadOnlyList<BudgetLineRequest?>? Lines, string? RowVersion);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPut("/{projectId:int}/budgets/{budgetId:int}", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.Edit)
            .WithName("UpdateBudget")
            .WithSummary("Replace the title, notes and lines of a draft budget (an Executive or the project's manager)");

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
        var title = BudgetFields.Title(request.Title, errors);
        var notes = BudgetFields.Notes(request.Notes, errors);
        if (request.Lines is null)
        {
            errors.Add("lines", "lines is required. Send [] for a budget with no lines.");
        }

        var lines = BudgetFields.Lines(request.Lines ?? [], errors);
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

        if (BudgetRules.PrepareProblem(project) is { } refusal)
        {
            log.ActionRefused(caller.UserId, nameof(UpdateBudget), "not an Executive or the project's manager, or project completed");
            return refusal;
        }

        var budget = await db.Budgets
            .Include(candidate => candidate.Lines)
            .SingleOrDefaultAsync(candidate => candidate.Id == budgetId && candidate.ProjectId == projectId, cancellationToken);
        if (budget is null)
        {
            return PlanningProblems.BudgetNotFound();
        }

        if (budget.Status != BudgetStatus.Draft)
        {
            return PlanningProblems.NotADraft();
        }

        await BudgetFields.CheckItemsAsync(db, projectId, lines, errors, cancellationToken);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        budget.Title = title;
        budget.Notes = notes;
        budget.UpdatedAt = clock.UtcNow;
        budget.UpdatedBy = caller.UserId;
        db.Entry(budget).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        // The old lines go first, then the new ones are numbered from 1 again: two saves in one transaction, so
        // the unique (budget, line number) index never sees both at once.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.BudgetLines.RemoveRange(budget.Lines);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        db.BudgetLines.AddRange(lines.Select((line, index) =>
        {
            var row = line.ToLine(index + 1);
            row.BudgetId = budget.Id;
            return row;
        }));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        log.BudgetUpdated(caller.UserId, projectId, budget.Id, lines.Count);

        var view = await BudgetView.LoadAsync(db, users, projectId, budget.Id, cancellationToken);
        return view is null ? PlanningProblems.BudgetNotFound() : TypedResults.Ok(view);
    }
}
