using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Data.Configurations;
using AlMotaheda.Modules.Fleet.Security;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Features.Register;

/// <summary>
/// <c>POST /api/equipment/{id}/rentals</c>: new rental terms for a rented machine (a new lessor or a new rate).
/// The open terms end the day before the new ones start; nothing is overwritten, so the history stays, and costs
/// already recorded keep the rate they copied.
/// </summary>
internal static class AddEquipmentRental
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/equipment/{equipmentId:int}/rentals", HandleAsync)
            .RequirePermission(FleetPermissions.Module, PermissionAction.Edit)
            .WithName("AddEquipmentRental")
            .WithSummary("Set new rental terms for a rented machine; the open terms end the day before");

    internal static async Task<Results<Created<EquipmentDetails>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int equipmentId,
        RentalTermsRequest request,
        HttpContext httpContext,
        FleetDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        ISupplierDirectory suppliers,
        CompanyClock clock,
        FleetLog log,
        CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = RentalTermsFields.Validate(request, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        if (!await db.Equipment.AnyAsync(candidate => candidate.Id == equipmentId, cancellationToken))
        {
            return FleetProblems.EquipmentNotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await AppLock.TryAcquireAsync(db, AppLock.Equipment(equipmentId), cancellationToken))
        {
            return FleetProblems.TryAgainLater();
        }

        var equipment = await db.Equipment.AsNoTracking()
            .Where(candidate => candidate.Id == equipmentId)
            .Select(candidate => new { candidate.Ownership })
            .SingleOrDefaultAsync(cancellationToken);
        if (equipment is null)
        {
            return FleetProblems.EquipmentNotFound();
        }

        if (equipment.Ownership != EquipmentOwnership.Rented)
        {
            return FleetProblems.WrongStatus("This machine is owned. Rental terms apply to rented equipment only.");
        }

        var lessors = await suppliers.FindAsync([fields!.SupplierId], cancellationToken);
        SupplierChoice.Check(fields.SupplierId, lessors, errors, "supplierId");

        var terms = await db.EquipmentRentals.Where(rental => rental.EquipmentId == equipmentId).ToListAsync(cancellationToken);
        if (RentalTimeline.EarliestNextStart(terms.Select(term => (term.StartDate, term.EndDate)).ToList()) is { } earliest
            && fields.StartDate < earliest)
        {
            errors.Add("startDate", $"New terms must start on or after {earliest:yyyy-MM-dd}, after the current terms.");
        }

        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        // Close the open terms first, in their own statement: the filtered unique index allows one open row.
        var open = terms.SingleOrDefault(term => term.EndDate is null);
        if (open is not null)
        {
            open.EndDate = RentalTimeline.CloseBefore(fields.StartDate);
            await db.SaveChangesAsync(cancellationToken);
        }

        var added = fields.ToRow(equipmentId, clock.UtcNow, caller.UserId);
        db.EquipmentRentals.Add(added);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, EquipmentRentalConfiguration.OpenTermsIndex))
        {
            // Not expected under the machine's lock; a lost race is still a conflict, not a server error.
            return FleetProblems.Conflict(ApiProblem.ConcurrencyConflict, "The rental terms changed at the same time. Reload and try again.");
        }

        await transaction.CommitAsync(cancellationToken);
        log.RentalTermsSet(caller.UserId, equipmentId, added.Id, added.SupplierId, open?.Id);

        var details = await EquipmentDetailsLoader.LoadAsync(db, access, users, suppliers, httpContext.User, equipmentId, cancellationToken);
        return TypedResults.Created($"/api/equipment/{equipmentId}", details!);
    }
}
