using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Workforce.Features.Sheets;

/// <summary>
/// <c>GET /api/projects/{pid}/labor/sheets/{id}</c>: one sheet with its lines, adjustments, allocations and pay
/// (frozen once approved, a preview with today's rates before).
/// </summary>
internal static class GetSheet
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/projects/{projectId:int}/labor/sheets/{sheetId:int}", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.View)
            .WithName("GetLaborSheet")
            .WithSummary("One labor sheet with its lines, adjustments, item allocations and pay (or pay preview)");

    internal static async Task<Results<Ok<LaborSheetView>, ProblemHttpResult>> HandleAsync(
        int projectId,
        int sheetId,
        HttpContext httpContext,
        WorkforceDbContext db,
        IProjectAccess access,
        IProjectItems projectItems,
        IUserDirectory users,
        IOptions<WorkforceOptions> options,
        CancellationToken cancellationToken)
    {
        if (WorkforceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return WorkforceProblems.ProjectNotFound();
        }

        var view = await LaborSheetView.LoadAsync(db, projectItems, users, caller, options.Value.PaySettings, projectId, sheetId, cancellationToken);
        return view is null ? WorkforceProblems.SheetNotFound() : TypedResults.Ok(view);
    }
}
