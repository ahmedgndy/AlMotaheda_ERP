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
/// <c>PUT /api/projects/{pid}/expenses/{id}</c>: the recorder fixes a record and resubmits it. Only while it is
/// <c>Recorded</c> or <c>Returned</c>; it goes back to <c>Recorded</c> and the review note is cleared. An approved
/// record is frozen: only its item link can change, through <c>PUT …/expenses/{id}/item</c>.
/// </summary>
internal static class UpdateExpense
{
    internal sealed record Request(
        string? Kind,
        string? ExpenseDate,
        int? SupplierId,
        string? ItemName,
        string? Unit,
        decimal? Quantity,
        decimal? UnitPrice,
        string? Category,
        string? Description,
        decimal? Amount,
        string? Channel,
        string? InvoiceNo,
        string? DeliveryNoteNo,
        int? CatalogItemId,
        int? ProjectItemId,
        string? Notes,
        string? RowVersion) : IExpenseFieldsRequest;

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPut("/{projectId:int}/expenses/{expenseId:int}", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.Create)
            .WithName("UpdateProjectExpense")
            .WithSummary("Fix and resubmit a recorded or returned record (the recorder only)");

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

        var errors = new ValidationErrors();
        var fields = ExpenseFields.Validate(request, clock.Today, errors);
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

        switch (ExpenseRules.Resubmit(expense.Status, expense.CreatedBy, caller.UserId))
        {
            case ExpenseDecision.NotRecorder:
                log.ActionRefused(caller.UserId, nameof(UpdateExpense), "not the recorder");
                return CoreProblems.NotTheRecorder();
            case ExpenseDecision.WrongStatus:
                return WorkspaceProblems.WrongStatus("Only a recorded or returned record can be changed. An approved record is frozen.");
        }

        var references = await ExpenseReferences.ResolveAsync(db, projectId, fields!, errors, cancellationToken);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (references.IsEquipmentCost)
        {
            return CoreProblems.UseEquipmentCosts();
        }

        // Linking to an inactive item is refused, unless the record is already on it (nothing changes then).
        if (references.ItemStatus is ProjectItemStatus.Inactive && fields!.ProjectItemId != expense.ProjectItemId)
        {
            return WorkspaceProblems.InactiveItem();
        }

        var now = clock.UtcNow;
        var oldStatus = expense.Status;

        fields!.ApplyTo(expense);
        expense.CatalogPriceAtRecording = references.CatalogPrice;
        expense.Resubmit(caller.UserId, now);

        if (fields.ProjectItemId != expense.ProjectItemId)
        {
            expense.ProjectItemId = fields.ProjectItemId;
            expense.ItemLinkedAt = fields.ProjectItemId is null ? null : now;
            expense.ItemLinkedBy = fields.ProjectItemId is null ? null : caller.UserId;
        }

        db.Entry(expense).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (expense.ProjectItemId is not null && SqlViolations.IsReferenceOrCheck(exception))
        {
            // The item was deleted by someone else after our check: a lost race is a 409, never a 500.
            return CoreProblems.ItemRemoved();
        }

        log.ExpenseResubmitted(caller.UserId, projectId, expense.Id, oldStatus.ToString());

        var view = await ExpenseView.LoadAsync(db, users, projectId, expense.Id, cancellationToken);
        return view is null ? CoreProblems.ExpenseNotFound() : TypedResults.Ok(view);
    }
}
