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

/// <summary><c>POST /api/suppliers</c>: add a supplier to the company list. The English name must be new.</summary>
internal static class CreateSupplier
{
    internal sealed record Request(
        string? NameAr,
        string? NameEn,
        string? Kind,
        string? Phone,
        string? TaxNumber,
        string? Notes,
        string? Status) : ISupplierFieldsRequest;

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/suppliers", HandleAsync)
            // One supplier list for the project screens and for Purchasing (12-procurement-design.md §4).
            .RequireAnyPermission((ProjectPermissions.Module, PermissionAction.Create), (ProjectPermissions.ProcurementModule, PermissionAction.Edit))
            .WithName("CreateSupplier")
            .WithSummary("Add a supplier to the company list");

    internal static async Task<Results<Created<SupplierView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
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
        var fields = SupplierFields.Validate(request, defaultStatus: SupplierStatus.Active, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await db.Suppliers.AnyAsync(other => other.NameEn == fields!.NameEn, cancellationToken))
        {
            return CoreProblems.SupplierNameTaken();
        }

        var supplier = new Supplier
        {
            NameAr = fields!.NameAr,
            NameEn = fields.NameEn,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };
        fields.ApplyTo(supplier);
        db.Suppliers.Add(supplier);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
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

        log.SupplierCreated(caller.UserId, supplier.Id);

        // Suppliers are read through the list, so there is no single-supplier location.
        return TypedResults.Created((string?)null, SupplierView.From(supplier));
    }
}
