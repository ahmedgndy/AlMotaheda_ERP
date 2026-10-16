using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.HR.Common;
using AlMotaheda.Modules.HR.Data;
using AlMotaheda.Modules.HR.Data.Configurations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.HR.Features.Employees;

/// <summary>One employee document (metadata only; the files themselves are a Needs Confirmation item).</summary>
internal sealed record EmployeeDocumentView(
    int Id, int EmployeeId, string Kind, string? Number, DateOnly? IssueDate, DateOnly? ExpiryDate, string? Notes,
    DateTime CreatedAt, int CreatedBy, string RowVersion)
{
    public static EmployeeDocumentView From(EmployeeDocument document) => new(
        document.Id, document.EmployeeId, document.Kind.ToString(), document.Number, document.IssueDate, document.ExpiryDate,
        document.Notes, document.CreatedAt, document.CreatedBy, RowVersions.Format(document.RowVersion));
}

/// <summary>
/// <c>GET /api/hr/employees/{id}/documents</c> (hr View), <c>POST</c> on the same path (hr Create) and
/// <c>PUT /api/hr/employee-documents/{id}</c> (hr Edit).
/// </summary>
internal static class EmployeeDocuments
{
    internal sealed record Request(string? Kind, string? Number, string? IssueDate, string? ExpiryDate, string? Notes, string? RowVersion);

    private sealed record Fields(EmployeeDocumentKind Kind, string? Number, DateOnly? IssueDate, DateOnly? ExpiryDate, string? Notes);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/hr/employees/{employeeId:int}/documents", ListAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.View)
            .WithName("ListEmployeeDocuments")
            .WithSummary("List an employee's documents");

        api.MapPost("/hr/employees/{employeeId:int}/documents", AddAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.Create)
            .WithName("AddEmployeeDocument")
            .WithSummary("Add a document to an employee's file");

        api.MapPut("/hr/employee-documents/{documentId:int}", UpdateAsync)
            .RequirePermission(HrPermissions.Module, PermissionAction.Edit)
            .WithName("UpdateEmployeeDocument")
            .WithSummary("Edit an employee document");
    }

    internal static async Task<Results<Ok<IReadOnlyList<EmployeeDocumentView>>, ProblemHttpResult>> ListAsync(
        int employeeId, HrDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Employees.AnyAsync(employee => employee.Id == employeeId, cancellationToken))
        {
            return HrProblems.EmployeeNotFound();
        }

        var documents = await db.EmployeeDocuments.AsNoTracking()
            .Where(document => document.EmployeeId == employeeId)
            .OrderBy(document => document.Kind).ThenBy(document => document.Id)
            .ToListAsync(cancellationToken);
        return TypedResults.Ok<IReadOnlyList<EmployeeDocumentView>>(documents.Select(EmployeeDocumentView.From).ToList());
    }

    internal static async Task<Results<Created<EmployeeDocumentView>, ValidationProblem, ProblemHttpResult>> AddAsync(
        int employeeId, Request request, HttpContext httpContext, HrDbContext db, CompanyClock clock, HrLog log,
        CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = Validate(request, errors);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var employee = await db.Employees.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == employeeId, cancellationToken);
        if (employee is null)
        {
            return HrProblems.EmployeeNotFound();
        }

        if (employee.Status == EmployeeStatus.Ended)
        {
            return HrProblems.Ended();
        }

        var document = new EmployeeDocument { EmployeeId = employeeId, CreatedAt = clock.UtcNow, CreatedBy = caller.UserId };
        Apply(fields!, document);
        db.EmployeeDocuments.Add(document);
        await db.SaveChangesAsync(cancellationToken);

        log.DocumentAdded(caller.UserId, employeeId, document.Id);
        return TypedResults.Created($"/api/hr/employee-documents/{document.Id}", EmployeeDocumentView.From(document));
    }

    internal static async Task<Results<Ok<EmployeeDocumentView>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        int documentId, Request request, HttpContext httpContext, HrDbContext db, CompanyClock clock, HrLog log,
        CancellationToken cancellationToken)
    {
        if (HrCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = Validate(request, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return HrProblems.Validation(errors);
        }

        var document = await db.EmployeeDocuments.SingleOrDefaultAsync(candidate => candidate.Id == documentId, cancellationToken);
        if (document is null)
        {
            return HrProblems.DocumentNotFound();
        }

        Apply(fields!, document);
        document.UpdatedAt = clock.UtcNow;
        document.UpdatedBy = caller.UserId;
        db.Entry(document).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.DocumentUpdated(caller.UserId, document.Id);
        return TypedResults.Ok(EmployeeDocumentView.From(document));
    }

    private static Fields? Validate(Request request, ValidationErrors errors)
    {
        var kind = Input.RequiredName<EmployeeDocumentKind>(request.Kind, errors, "kind");
        var number = Input.OptionalText(request.Number, EmployeeDocumentConfiguration.NumberMaxLength, errors, "number", "The number");
        var issued = Input.OptionalDate(request.IssueDate, errors, "issueDate", "The issue date");
        var expires = Input.OptionalDate(request.ExpiryDate, errors, "expiryDate", "The expiry date");
        if (issued is not null && expires is not null && expires < issued)
        {
            errors.Add("expiryDate", "The expiry date can't be before the issue date.");
        }

        var notes = Input.OptionalText(request.Notes, EmployeeConfiguration.NotesMaxLength, errors, "notes", "Notes");
        return errors.HasErrors ? null : new Fields(kind!.Value, number, issued, expires, notes);
    }

    private static void Apply(Fields fields, EmployeeDocument document)
    {
        document.Kind = fields.Kind;
        document.Number = fields.Number;
        document.IssueDate = fields.IssueDate;
        document.ExpiryDate = fields.ExpiryDate;
        document.Notes = fields.Notes;
    }
}
