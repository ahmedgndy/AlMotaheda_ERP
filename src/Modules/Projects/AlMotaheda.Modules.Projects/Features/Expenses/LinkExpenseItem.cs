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

namespace AlMotaheda.Modules.Projects.Features.Expenses;

/// <summary>
/// <c>PUT /api/projects/{pid}/expenses/{id}/item</c>: link a record to a project item, move it to another item, or
/// remove the link (<c>itemId: null</c>). It changes only the link: never an amount, and it never creates a row.
/// The one change still allowed on an approved record.
/// </summary>
/// <remarks>
/// Needs projects Edit or projects Approve. The endpoint takes the signed-in policy and checks the two
/// permissions itself, because <c>RequirePermission</c> asks for exactly one action.
/// </remarks>
internal static class LinkExpenseItem
{
    internal sealed record Request(int? ItemId, string? RowVersion);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPut("/{projectId:int}/expenses/{expenseId:int}/item", HandleAsync)
            .WithName("LinkExpenseToItem")
            .WithSummary("Link a record to a project item, move it, or remove the link (projects Edit or Approve)");

    internal static async Task<Results<Ok<ExpenseView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int expenseId,
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

        if (!caller.Can(PermissionAction.Edit) && !caller.Can(PermissionAction.Approve))
        {
            return CoreProblems.LinkNeedsEditOrApprove();
        }

        var errors = new ValidationErrors();
        if (request.ItemId is < 1)
        {
            errors.Add("itemId", "itemId is not valid.");
        }

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

        var expense = await db.ProjectExpenses
            .SingleOrDefaultAsync(candidate => candidate.Id == expenseId && candidate.ProjectId == projectId, cancellationToken);
        if (expense is null)
        {
            return CoreProblems.ExpenseNotFound();
        }

        if (!ExpenseRules.CanLink(expense.Status))
        {
            return WorkspaceProblems.WrongStatus("A rejected record is never a cost, so it can't be linked to an item.");
        }

        if (request.ItemId is { } itemId && itemId != expense.ProjectItemId)
        {
            var status = await db.ProjectItems.AsNoTracking()
                .Where(item => item.Id == itemId && item.ProjectId == projectId)
                .Select(item => (ProjectItemStatus?)item.Status)
                .FirstOrDefaultAsync(cancellationToken);

            if (status is null)
            {
                errors.Add("itemId", "The project item was not found on this project.");
                return ProjectProblems.Validation(errors);
            }

            if (status == ProjectItemStatus.Inactive)
            {
                return WorkspaceProblems.InactiveItem();
            }
        }

        var previousItemId = expense.ProjectItemId;
        var now = clock.UtcNow;

        expense.ProjectItemId = request.ItemId;
        expense.ItemLinkedAt = request.ItemId is null ? null : now;
        expense.ItemLinkedBy = request.ItemId is null ? null : caller.UserId;
        expense.UpdatedAt = now;
        expense.UpdatedBy = caller.UserId;

        db.Entry(expense).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (request.ItemId is not null && SqlViolations.IsReferenceOrCheck(exception))
        {
            // The item was deleted by someone else after our check: a lost race is a 409, never a 500.
            return CoreProblems.ItemRemoved();
        }

        log.ExpenseLinked(caller.UserId, projectId, expense.Id, expense.ProjectItemId, previousItemId);

        var view = await ExpenseView.LoadAsync(db, users, projectId, expense.Id, cancellationToken);
        return view is null ? CoreProblems.ExpenseNotFound() : TypedResults.Ok(view);
    }
}
