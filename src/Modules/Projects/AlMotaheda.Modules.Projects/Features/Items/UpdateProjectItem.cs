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
/// <c>PUT /api/projects/{pid}/items/{itemId}</c>: edit an item's names, category, unit and notes. Its figures are
/// sums of linked records, so there is nothing about money to edit here.
/// </summary>
internal static class UpdateProjectItem
{
    internal sealed record Request(string? NameAr, string? NameEn, string? Category, string? Unit, string? Description, string? Notes, string? RowVersion)
        : IItemFieldsRequest;

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPut("/{projectId:int}/items/{itemId:int}", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.Edit)
            .WithName("UpdateProjectItem")
            .WithSummary("Edit a project item (an Executive or the project's manager)");

    internal static async Task<Results<Ok<ItemRow>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int itemId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        ItemsLedgerSource source,
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

        if (ItemRules.ManageProblem(project) is { } refusal)
        {
            log.ActionRefused(caller.UserId, nameof(UpdateProjectItem), "not an Executive or the project's manager, or project completed");
            return refusal;
        }

        var item = await db.ProjectItems.SingleOrDefaultAsync(candidate => candidate.Id == itemId && candidate.ProjectId == projectId, cancellationToken);
        if (item is null)
        {
            return CoreProblems.ItemNotFound();
        }

        if ((fields!.NameAr != item.NameAr || fields.NameEn != item.NameEn)
            && await fields.NameTakenAsync(db, projectId, itemId, cancellationToken))
        {
            return CoreProblems.ItemNameAlreadyUsed();
        }

        fields.ApplyTo(item);
        item.UpdatedAt = clock.UtcNow;
        item.UpdatedBy = caller.UserId;

        db.Entry(item).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (ItemRules.IsNameRace(exception))
        {
            return CoreProblems.ItemNameAlreadyUsed();
        }

        log.ItemUpdated(caller.UserId, projectId, item.Id);

        var data = await source.LoadAsync(projectId, cancellationToken);
        return TypedResults.Ok(ItemRow.From(item, data.Ledger.Of(item.Id)));
    }
}
