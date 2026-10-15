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

namespace AlMotaheda.Modules.Projects.Features.Budgets;

/// <summary>
/// <c>POST /api/projects/{pid}/budgets</c>: start a new draft version of the project's budget, empty, with the
/// given lines, or as a copy of another version's lines. A revision is always a new version: nothing approved is
/// ever overwritten.
/// </summary>
/// <remarks>
/// It runs under the project lock, so two people can't take the same version number, and a project has at most one
/// draft (409 <c>budget_draft_exists</c>; the filtered unique index is the last guard).
/// </remarks>
internal static class CreateBudget
{
    internal sealed record Request(string? Title, string? Notes, int? CopyFromBudgetId, IReadOnlyList<BudgetLineRequest?>? Lines);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/budgets", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.Edit)
            .WithName("CreateBudget")
            .WithSummary("Start a new draft budget version, with lines or as a copy of another version (an Executive or the project's manager)");

    internal static async Task<Results<Created<BudgetView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
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
        var lines = BudgetFields.Lines(request.Lines ?? [], errors);

        if (request.CopyFromBudgetId is < 1)
        {
            errors.Add("copyFromBudgetId", "copyFromBudgetId is not valid.");
        }
        else if (request.CopyFromBudgetId is not null && request.Lines is { Count: > 0 })
        {
            errors.Add("copyFromBudgetId", "Send lines or copyFromBudgetId, not both.");
        }

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
            log.ActionRefused(caller.UserId, nameof(CreateBudget), "not an Executive or the project's manager, or project completed");
            return refusal;
        }

        await BudgetFields.CheckItemsAsync(db, projectId, lines, errors, cancellationToken);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await ProjectLock.TryAcquireAsync(db, projectId, cancellationToken))
        {
            return ProjectProblems.TryAgainLater();
        }

        if (request.CopyFromBudgetId is { } sourceId)
        {
            var source = await db.BudgetLines.AsNoTracking()
                .Where(line => line.BudgetId == sourceId && line.Budget.ProjectId == projectId)
                .OrderBy(line => line.LineNo)
                .Select(line => new BudgetLineInput(
                    line.CostGroup, line.ProjectItemId, line.Description, line.Unit, line.Quantity, line.UnitPrice, line.Amount))
                .ToListAsync(cancellationToken);

            if (source.Count == 0 && !await db.Budgets.AnyAsync(budget => budget.Id == sourceId && budget.ProjectId == projectId, cancellationToken))
            {
                errors.Add("copyFromBudgetId", "This budget was not found on this project.");
                return ProjectProblems.Validation(errors);
            }

            lines = source;
        }

        if (await db.Budgets.AnyAsync(budget => budget.ProjectId == projectId && budget.Status == BudgetStatus.Draft, cancellationToken))
        {
            return PlanningProblems.DraftAlreadyExists();
        }

        var lastVersion = await db.Budgets
            .Where(budget => budget.ProjectId == projectId)
            .MaxAsync(budget => (int?)budget.Version, cancellationToken) ?? 0;

        var budget = new Budget
        {
            ProjectId = projectId,
            Version = lastVersion + 1,
            Status = BudgetStatus.Draft,
            Title = title,
            Notes = notes,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };
        budget.Lines.AddRange(lines.Select((line, index) => line.ToLine(index + 1)));
        db.Budgets.Add(budget);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (
            SqlViolations.IsUnique(exception, BudgetConfiguration.DraftIndex) || SqlViolations.IsUnique(exception, BudgetConfiguration.VersionIndex))
        {
            return PlanningProblems.DraftAlreadyExists();
        }

        await transaction.CommitAsync(cancellationToken);
        log.BudgetCreated(caller.UserId, projectId, budget.Id, budget.Version, request.CopyFromBudgetId);

        var view = await BudgetView.LoadAsync(db, users, projectId, budget.Id, cancellationToken);
        return TypedResults.Created($"/api/projects/{projectId}/budgets/{budget.Id}", view!);
    }
}
