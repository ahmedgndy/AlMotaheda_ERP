using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features.Assignments;
using AlMotaheda.Modules.Fleet.Security;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Features.Register;

/// <summary>
/// <c>GET /api/equipment/{id}</c>: one machine with its current rental terms, where it is now, and its assignment
/// history. Only stays on projects the caller may see are shown; the others are only counted.
/// </summary>
internal static class GetEquipment
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/equipment/{equipmentId:int}", HandleAsync)
            .RequirePermission(FleetPermissions.Module, PermissionAction.View)
            .WithName("GetEquipment")
            .WithSummary("One machine of the register, with its rental terms and its history on the projects you may see");

    internal static async Task<Results<Ok<EquipmentDetails>, ProblemHttpResult>> HandleAsync(
        int equipmentId,
        HttpContext httpContext,
        FleetDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        ISupplierDirectory suppliers,
        CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var details = await EquipmentDetailsLoader.LoadAsync(db, access, users, suppliers, httpContext.User, equipmentId, cancellationToken);
        return details is null ? FleetProblems.EquipmentNotFound() : TypedResults.Ok(details);
    }
}

/// <summary>Builds <see cref="EquipmentDetails"/>: after a read and after every write to the register.</summary>
internal static class EquipmentDetailsLoader
{
    public static async Task<EquipmentDetails?> LoadAsync(
        FleetDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        ISupplierDirectory suppliers,
        ClaimsPrincipal principal,
        int equipmentId,
        CancellationToken cancellationToken)
    {
        var row = await EquipmentQueries.One(db, equipmentId).SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var seesMoney = principal.HasPermission(FleetPermissions.Module, PermissionAction.SeeMoney);
        var rentals = await EquipmentQueries.Rentals(db, equipmentId).ToListAsync(cancellationToken);
        var history = await AssignmentQueries.OfEquipment(db, equipmentId).ToListAsync(cancellationToken);

        // The Projects contract answers one project at a time. A machine visits few projects in its life, so this
        // is a handful of calls; a batch question would be better (see the module README, known limits).
        var visible = new Dictionary<int, ProjectAccessInfo>();
        foreach (var projectId in history.Select(assignment => assignment.ProjectId).Distinct())
        {
            if (await access.FindVisibleAsync(principal, projectId, cancellationToken) is { } project)
            {
                visible[projectId] = project;
            }
        }

        var shown = history.Where(assignment => visible.ContainsKey(assignment.ProjectId)).ToList();
        var people = await Lookups.UsersAsync(
            users,
            shown.SelectMany(assignment => new[] { assignment.ResponsibleUserId, assignment.AssignedBy, assignment.EndedBy })
                .Append(row.CreatedBy)
                .Append(row.UpdatedBy),
            cancellationToken);
        var lessors = await Lookups.SuppliersAsync(suppliers, rentals.Select(rental => (int?)rental.SupplierId), cancellationToken);

        var views = shown.Select(assignment => EquipmentAssignmentView.From(assignment, visible[assignment.ProjectId], people)).ToList();
        var rentalViews = rentals.Select(rental => EquipmentRentalView.From(rental, lessors, seesMoney)).ToList();

        return new EquipmentDetails(
            row.Id,
            row.AssetNo,
            row.NameAr,
            row.NameEn,
            row.Category.ToString(),
            row.TypeName,
            row.SerialNo,
            row.Ownership.ToString(),
            row.Condition.ToString(),
            row.Status.ToString(),
            row.Notes,
            row.IsAvailable,
            row.CurrentAssignment is not null,
            rentalViews.FirstOrDefault(rental => rental.IsCurrent),
            rentalViews,
            views.FirstOrDefault(assignment => assignment.IsCurrent),
            views,
            history.Count - shown.Count,
            row.CreatedAt,
            FleetPerson.From(row.CreatedBy, people),
            row.UpdatedAt,
            FleetPerson.FromOptional(row.UpdatedBy, people),
            RowVersions.Format(row.RowVersion));
    }
}
