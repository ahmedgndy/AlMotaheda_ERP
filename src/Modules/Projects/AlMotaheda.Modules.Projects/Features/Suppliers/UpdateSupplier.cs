using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Suppliers;

/// <summary>
/// <c>PUT /api/suppliers/{id}</c>: edit a supplier, or set it inactive so it can't be chosen for new records.
/// Its history on every project stays as it is.
/// </summary>
internal static class UpdateSupplier
{
    internal sealed record Request(
        string? NameAr,
        string? NameEn,
        string? Kind,
        string? Phone,
        string? TaxNumber,
        string? Notes,
        string? Status,
        string? RowVersion) : ISupplierFieldsRequest;

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/suppliers/{supplierId:int}", HandleAsync)
            .RequireAnyPermission((ProjectPermissions.Module, PermissionAction.Edit), (ProjectPermissions.ProcurementModule, PermissionAction.Edit))
            .WithName("UpdateSupplier")
            .WithSummary("Edit a supplier or set it inactive");

    internal static async Task<Results<Ok<SupplierView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int supplierId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        // The form shows the current status, so it is always sent on edit.
        var fields = SupplierFields.Validate(request, defaultStatus: null, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var supplier = await db.Suppliers.SingleOrDefaultAsync(candidate => candidate.Id == supplierId, cancellationToken);
        if (supplier is null)
        {
            return CoreProblems.SupplierNotFound();
        }

        if (fields!.NameEn != supplier.NameEn
            && await db.Suppliers.AnyAsync(other => other.Id != supplierId && other.NameEn == fields.NameEn, cancellationToken))
        {
            return CoreProblems.SupplierNameTaken();
        }

        var oldStatus = supplier.Status;
        fields.ApplyTo(supplier);
        supplier.UpdatedAt = clock.UtcNow;
        supplier.UpdatedBy = caller.UserId;

        // Saves only if nobody changed the supplier since the caller read it.
        db.Entry(supplier).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, SupplierConfiguration.NameIndex)
                                                   || SqlViolations.IsUnique(exception, SupplierConfiguration.NameArIndex))
        {
            return CoreProblems.SupplierNameTaken();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, SupplierConfiguration.TaxNumberIndex))
        {
            return CoreProblems.SupplierTaxNumberTaken();
        }

        log.SupplierUpdated(caller.UserId, supplier.Id, oldStatus.ToString(), supplier.Status.ToString());
        return TypedResults.Ok(SupplierView.From(supplier));
    }
}
