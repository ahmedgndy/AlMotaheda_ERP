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
/// <c>POST /api/equipment</c>: add a machine to the company register, once for its whole life. A rented machine is
/// registered with its first rental terms (lessor, rate and start date).
/// </summary>
internal static class CreateEquipment
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
        RentalTermsRequest? Rental) : IEquipmentFieldsRequest;

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/equipment", HandleAsync)
            .RequirePermission(FleetPermissions.Module, PermissionAction.Create)
            .WithName("CreateEquipment")
            .WithSummary("Register a machine (a rented one with its first rental terms). 409 asset_no_taken");

    internal static async Task<Results<Created<EquipmentDetails>, ValidationProblem, ProblemHttpResult>> HandleAsync(
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
        if (Input.TryParseName<EquipmentOwnership>(request.Ownership, out var ownership)
            && ownership == EquipmentOwnership.Rented && request.Rental is null)
        {
            errors.Add("rental", "A rented machine needs its rental terms: lessor, rate basis, rate and start date.");
        }

        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        if (fields!.Rental is { } terms)
        {
            var lessors = await suppliers.FindAsync([terms.SupplierId], cancellationToken);
            SupplierChoice.Check(terms.SupplierId, lessors, errors, "rental.supplierId");
            if (errors.HasErrors)
            {
                return FleetProblems.Validation(errors);
            }
        }

        if (await db.Equipment.AnyAsync(equipment => equipment.AssetNo == fields.AssetNo, cancellationToken))
        {
            return FleetProblems.AssetNoAlreadyUsed();
        }

        var now = clock.UtcNow;
        var equipment = new Equipment { CreatedAt = now, CreatedBy = caller.UserId };
        fields.ApplyTo(equipment);
        db.Equipment.Add(equipment);

        EquipmentRental? rental = null;
        if (fields.Rental is { } firstTerms)
        {
            rental = firstTerms.ToRow(0, now, caller.UserId);
            rental.Equipment = equipment;
            db.EquipmentRentals.Add(rental);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, EquipmentConfiguration.AssetNoIndex))
        {
            return FleetProblems.AssetNoAlreadyUsed();
        }

        log.EquipmentCreated(caller.UserId, equipment.Id, equipment.Ownership.ToString());
        if (rental is not null)
        {
            log.RentalTermsSet(caller.UserId, equipment.Id, rental.Id, rental.SupplierId, null);
        }

        var details = await EquipmentDetailsLoader.LoadAsync(db, access, users, suppliers, httpContext.User, equipment.Id, cancellationToken);
        return TypedResults.Created($"/api/equipment/{equipment.Id}", details!);
    }
}
