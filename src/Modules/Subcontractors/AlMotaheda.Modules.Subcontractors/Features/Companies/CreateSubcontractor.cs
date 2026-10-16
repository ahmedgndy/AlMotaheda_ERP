using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Subcontractors.Common;
using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Data.Configurations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Features.Companies;

/// <summary><c>POST /api/subcontractors</c>: add a company to the list. The English name must be new.</summary>
internal static class CreateSubcontractor
{
    internal sealed record Request(
        string? NameAr,
        string? NameEn,
        string? Specialty,
        string? ContactName,
        string? Phone,
        string? Email,
        string? Address,
        string? Notes,
        string? Status) : ISubcontractorFieldsRequest;

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/subcontractors", HandleAsync)
            .RequirePermission(SubPermissions.Module, PermissionAction.Create)
            .WithName("CreateSubcontractor")
            .WithSummary("Add a subcontractor company to the company list");

    internal static async Task<Results<Created<SubcontractorView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        Request request,
        HttpContext httpContext,
        SubcontractorsDbContext db,
        CompanyClock clock,
        SubcontractorsLog log,
        CancellationToken cancellationToken)
    {
        if (SubCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = SubcontractorFields.Validate(request, defaultStatus: SubcontractorStatus.Active, errors);
        if (errors.HasErrors)
        {
            return SubcontractorsProblems.Validation(errors);
        }

        if (await db.Subcontractors.AnyAsync(other => other.NameEn == fields!.NameEn, cancellationToken))
        {
            return SubcontractorsProblems.NameTaken();
        }

        var company = new Subcontractor { CreatedAt = clock.UtcNow, CreatedBy = caller.UserId };
        fields!.ApplyTo(company);
        db.Subcontractors.Add(company);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, SubcontractorConfiguration.NameIndex)
                                                   || SqlViolations.IsUnique(exception, SubcontractorConfiguration.NameArIndex))
        {
            return SubcontractorsProblems.NameTaken();
        }

        log.SubcontractorCreated(caller.UserId, company.Id);

        return TypedResults.Created($"/api/subcontractors/{company.Id}", SubcontractorView.From(company));
    }
}

/// <summary><c>PUT /api/subcontractors/{id}</c>: edit a company, or deactivate and reactivate it (<c>status</c>).</summary>
/// <remarks>An inactive company keeps its contracts and their records; it just can't get a new contract.</remarks>
internal static class UpdateSubcontractor
{
    internal sealed record Request(
        string? NameAr,
        string? NameEn,
        string? Specialty,
        string? ContactName,
        string? Phone,
        string? Email,
        string? Address,
        string? Notes,
        string? Status,
        string? RowVersion) : ISubcontractorFieldsRequest;

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/subcontractors/{subcontractorId:int}", HandleAsync)
            .RequirePermission(SubPermissions.Module, PermissionAction.Edit)
            .WithName("UpdateSubcontractor")
            .WithSummary("Edit a subcontractor company, or deactivate or reactivate it");

    internal static async Task<Results<Ok<SubcontractorView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int subcontractorId,
        Request request,
        HttpContext httpContext,
        SubcontractorsDbContext db,
        CompanyClock clock,
        SubcontractorsLog log,
        CancellationToken cancellationToken)
    {
        if (SubCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = SubcontractorFields.Validate(request, defaultStatus: null, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return SubcontractorsProblems.Validation(errors);
        }

        var company = await db.Subcontractors.SingleOrDefaultAsync(candidate => candidate.Id == subcontractorId, cancellationToken);
        if (company is null)
        {
            return SubcontractorsProblems.SubcontractorNotFound();
        }

        if (fields!.NameEn != company.NameEn
            && await db.Subcontractors.AnyAsync(other => other.Id != subcontractorId && other.NameEn == fields.NameEn, cancellationToken))
        {
            return SubcontractorsProblems.NameTaken();
        }

        var oldStatus = company.Status;
        fields.ApplyTo(company);
        company.UpdatedAt = clock.UtcNow;
        company.UpdatedBy = caller.UserId;
        db.Entry(company).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, SubcontractorConfiguration.NameIndex)
                                                   || SqlViolations.IsUnique(exception, SubcontractorConfiguration.NameArIndex))
        {
            return SubcontractorsProblems.NameTaken();
        }

        log.SubcontractorUpdated(caller.UserId, company.Id, oldStatus.ToString(), company.Status.ToString());

        return TypedResults.Ok(SubcontractorView.From(company));
    }
}
