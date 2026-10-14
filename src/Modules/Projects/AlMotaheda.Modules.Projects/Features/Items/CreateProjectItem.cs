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
/// projects Edit. Names are unique within the project.
/// </summary>
internal static class CreateProjectItem
{
    internal sealed record Request(string? NameAr, string? NameEn, string? Category, string? Unit, string? Description, string? Notes)
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

        if (await fields!.NameTakenAsync(db, projectId, exceptItemId: null, cancellationToken))
        {
            return CoreProblems.ItemNameAlreadyUsed();
        }

        var item = new ProjectItem
        {
            ProjectId = projectId,
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

        log.ItemCreated(caller.UserId, projectId, item.Id);

        return TypedResults.Created($"/api/projects/{projectId}/items/{item.Id}", ItemRow.From(item, ItemFigures.Empty));
    }
}
