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
/// <c>PUT /api/equipment/{id}</c>: edit a machine of the register.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>An owned machine that becomes rented needs its first rental terms in <c>rental</c>.</item>
///   <item>A rented machine that becomes owned has its open terms ended today; they stay as history.</item>
///   <item>Changing the terms of a rented machine goes through <c>POST /api/equipment/{id}/rentals</c>.</item>
///   <item>A machine on a project can't be retired: end its assignment first (409 <c>equipment_assigned</c>).</item>
/// </list>
/// Runs under the machine's lock, so it can't cross an assignment or new terms of the same machine.
/// </remarks>
internal static class UpdateEquipment
{
    internal sealed record Request(
        string? AssetNo,
        string? NameAr,
        string? NameEn,
        string? Category,
        string? TypeName,
        string? SerialNo,
        string? Ownership,
        string? Condition,
        string? Status,
        string? Notes,
        RentalTermsRequest? Rental,
        string? RowVersion) : IEquipmentFieldsRequest;

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/equipment/{equipmentId:int}", HandleAsync)
            .RequirePermission(FleetPermissions.Module, PermissionAction.Edit)
            .WithName("UpdateEquipment")
            .WithSummary("Edit a machine of the register (rowVersion). 409 asset_no_taken, equipment_assigned");

    internal static async Task<Results<Ok<EquipmentDetails>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int equipmentId,
        Request request,
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
        var fields = EquipmentFields.Validate(request, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
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

        var equipment = await db.Equipment.SingleOrDefaultAsync(candidate => candidate.Id == equipmentId, cancellationToken);
        if (equipment is null)
        {
            return FleetProblems.EquipmentNotFound();
        }

        var becomesRented = equipment.Ownership == EquipmentOwnership.Owned && fields!.Ownership == EquipmentOwnership.Rented;
        var becomesOwned = equipment.Ownership == EquipmentOwnership.Rented && fields!.Ownership == EquipmentOwnership.Owned;

        if (becomesRented && fields!.Rental is null)
        {
            errors.Add("rental", "A machine that becomes rented needs its rental terms: lessor, rate basis, rate and start date.");
        }
        else if (!becomesRented && fields!.Rental is not null)
        {
            errors.Add("rental", "Change the terms of a rented machine with POST /api/equipment/{id}/rentals.");
        }

        var terms = await db.EquipmentRentals
            .Where(rental => rental.EquipmentId == equipmentId)
            .ToListAsync(cancellationToken);

        if (becomesRented && fields!.Rental is { } newTerms)
        {
            var lessors = await suppliers.FindAsync([newTerms.SupplierId], cancellationToken);
            SupplierChoice.Check(newTerms.SupplierId, lessors, errors, "rental.supplierId");

            if (RentalTimeline.EarliestNextStart(terms.Select(term => (term.StartDate, term.EndDate)).ToList()) is { } earliest
                && newTerms.StartDate < earliest)
            {
                errors.Add("rental.startDate", $"New terms must start on or after {earliest:yyyy-MM-dd}, after the earlier terms.");
            }
        }

        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        if (fields!.Status == EquipmentStatus.Retired && equipment.Status != EquipmentStatus.Retired
            && await db.EquipmentAssignments.AnyAsync(
                assignment => assignment.EquipmentId == equipmentId && assignment.EndDate == null, cancellationToken))
        {
            return FleetProblems.AlreadyAssigned();
        }

        if (fields.AssetNo != equipment.AssetNo
            && await db.Equipment.AnyAsync(other => other.AssetNo == fields.AssetNo && other.Id != equipmentId, cancellationToken))
        {
            return FleetProblems.AssetNoAlreadyUsed();
        }

        var now = clock.UtcNow;
        var oldStatus = equipment.Status;
        var changed = fields.ChangedFields(equipment);

        fields.ApplyTo(equipment);
        equipment.UpdatedAt = now;
        equipment.UpdatedBy = caller.UserId;
        db.Entry(equipment).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        var closed = becomesOwned ? terms.SingleOrDefault(term => term.EndDate is null) : null;
        if (closed is not null)
        {
            closed.EndDate = RentalTimeline.CloseOn(closed.StartDate, clock.Today);
        }

        EquipmentRental? added = null;
        if (becomesRented)
        {
            added = fields.Rental!.ToRow(equipmentId, now, caller.UserId);
            db.EquipmentRentals.Add(added);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, EquipmentConfiguration.AssetNoIndex))
        {
            return FleetProblems.AssetNoAlreadyUsed();
        }

        await transaction.CommitAsync(cancellationToken);
        log.EquipmentUpdated(caller.UserId, equipmentId, changed, oldStatus.ToString(), equipment.Status.ToString());
        if (closed is not null)
        {
            log.RentalTermsClosed(caller.UserId, equipmentId, closed.Id);
        }

        if (added is not null)
        {
            log.RentalTermsSet(caller.UserId, equipmentId, added.Id, added.SupplierId, null);
        }

        var details = await EquipmentDetailsLoader.LoadAsync(db, access, users, suppliers, httpContext.User, equipmentId, cancellationToken);
        return details is null ? FleetProblems.EquipmentNotFound() : TypedResults.Ok(details);
    }
}
