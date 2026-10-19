using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Items;

/// <summary>
/// <c>POST /api/projects/{pid}/items</c>: add a project item ("بند"). An Executive or the project's manager, with
/// projects Edit. With <c>parentItemId</c> it is a sub-item of that main item (same project, a main item, active).
/// Names are unique among siblings.
/// </summary>
internal static class CreateProjectItem
{
    internal sealed record Request(
        string? NameAr, string? NameEn, string? Category, string? Unit, string? Description, string? Notes, int? ParentItemId = null)
        : IItemFieldsRequest;

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/items", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.Edit)
            .WithName("CreateProjectItem")
            .WithSummary("Add a project item (an Executive or the project's manager)");

    internal static async Task<Results<Created<ItemRow>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = ItemFields.Validate(request, errors);
        if (request.ParentItemId is < 1)
        {
            errors.Add("parentItemId", "parentItemId is not valid.");
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

        if (ItemRules.ManageProblem(project) is { } refusal)
        {
            log.ActionRefused(caller.UserId, nameof(CreateProjectItem), "not an Executive or the project's manager, or project completed");
            return refusal;
        }

        // A sub-item checks its parent under the project lock, so the parent can't be moved under another item or be
        // deactivated in between. The lock is the same one every hierarchy change takes.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await ProjectLock.TryAcquireAsync(db, projectId, cancellationToken))
        {
            return ProjectProblems.TryAgainLater();
        }

        if (request.ParentItemId is { } parentItemId)
        {
            var parentProblem = await ItemRules.ParentProblemAsync(db, projectId, parentItemId, errors, cancellationToken);
            if (errors.HasErrors)
            {
                return ProjectProblems.Validation(errors);
            }

            if (parentProblem is not null)
            {
                return parentProblem;
            }
        }

        if (await fields!.NameTakenAsync(db, projectId, request.ParentItemId, exceptItemId: null, cancellationToken))
        {
            return CoreProblems.ItemNameAlreadyUsed();
        }

        var item = new ProjectItem
        {
            ProjectId = projectId,
            ParentItemId = request.ParentItemId,
            NameAr = fields.NameAr,
            NameEn = fields.NameEn,
            Category = fields.Category,
            Unit = fields.Unit,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };
        fields.ApplyTo(item);
        db.ProjectItems.Add(item);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (ItemRules.IsNameRace(exception))
        {
            return CoreProblems.ItemNameAlreadyUsed();
        }

        await transaction.CommitAsync(cancellationToken);
        log.ItemCreated(caller.UserId, projectId, item.Id);

        return TypedResults.Created($"/api/projects/{projectId}/items/{item.Id}", ItemRow.New(item));
    }
}
