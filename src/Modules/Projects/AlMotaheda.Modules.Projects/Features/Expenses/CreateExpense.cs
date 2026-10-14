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

namespace AlMotaheda.Modules.Projects.Features.Expenses;

/// <summary>
/// <c>POST /api/projects/{pid}/expenses</c>: record a supplier delivery (<c>Supply</c>) or a site expense
/// (<c>Expense</c>). It is saved as <c>Recorded</c>: a cost only once someone else approves it.
/// </summary>
/// <remarks>
/// The server computes a supply's amount (quantity × unit price) and copies the approved catalog price of the day
/// when a catalog item is named, so a later price change never changes what was recorded.
/// </remarks>
internal static class CreateExpense
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
        string? Notes) : IExpenseFieldsRequest;

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/expenses", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.Create)
            .WithName("CreateProjectExpense")
            .WithSummary("Record a supply or a site expense on a project (saved as Recorded, waiting for review)");

    internal static async Task<Results<Created<ExpenseView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
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
        var fields = ExpenseFields.Validate(request, clock.Today, errors);
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

        var references = await ExpenseReferences.ResolveAsync(db, projectId, fields!, errors, cancellationToken);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (references.ItemStatus is ProjectItemStatus.Inactive)
        {
            return WorkspaceProblems.InactiveItem();
        }

        var now = clock.UtcNow;
        var expense = new ProjectExpense
        {
            ProjectId = projectId,
            Status = ExpenseStatus.Recorded,
            CatalogPriceAtRecording = references.CatalogPrice,
            CreatedAt = now,
            CreatedBy = caller.UserId,
        };
        fields!.ApplyTo(expense);

        if (fields.ProjectItemId is { } itemId)
        {
            expense.ProjectItemId = itemId;
            expense.ItemLinkedAt = now;
            expense.ItemLinkedBy = caller.UserId;
        }

        db.ProjectExpenses.Add(expense);
        await db.SaveChangesAsync(cancellationToken);
        log.ExpenseRecorded(caller.UserId, projectId, expense.Id, expense.Kind.ToString(), expense.Channel.ToString());

        var view = await ExpenseView.LoadAsync(db, users, projectId, expense.Id, cancellationToken);
        return TypedResults.Created($"/api/projects/{projectId}/expenses/{expense.Id}", view!);
    }
}
