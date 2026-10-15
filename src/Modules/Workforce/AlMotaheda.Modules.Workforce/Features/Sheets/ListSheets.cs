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
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Sheets;

/// <summary>One sheet in the list. <see cref="ApprovedNet"/> exists only on approved sheets, and only with workers See money.</summary>
internal sealed record LaborSheetSummaryView(
    int Id,
    DateOnly WorkDate,
    string Shift,
    string Status,
    int RecordedBy,
    string? RecordedByNameAr,
    string? RecordedByNameEn,
    DateTime? SubmittedAt,
    DateTime? ReviewedAt,
    int? ReviewedBy,
    int LineCount,
    decimal WorkerDays,
    decimal? ApprovedNet,
    string RowVersion);

/// <summary><c>GET /api/projects/{pid}/labor/sheets</c>: one page of a project's labor sheets, newest day first.</summary>
internal static class ListSheets
{
    internal sealed record Response(IReadOnlyList<LaborSheetSummaryView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/projects/{projectId:int}/labor/sheets", HandleAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.View)
            .WithName("ListLaborSheets")
            .WithSummary("List a project's labor sheets (status, from, to, shift, mine=true; paging)");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        WorkforceDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CancellationToken cancellationToken,
        string? status = null,
        string? from = null,
        string? to = null,
        string? shift = null,
        bool mine = false,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        if (WorkforceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        var statusFilter = ParseOptional<SheetStatus>(status, "status", errors);
        var shiftFilter = ParseOptional<Shift>(shift, "shift", errors);
        var fromDate = Input.OptionalDate(from, errors, "from", "from");
        var toDate = Input.OptionalDate(to, errors, "to", "to");
        if (fromDate is { } f && toDate is { } t && t < f)
        {
            errors.Add("to", "to must not be before from.");
        }

        if (errors.HasErrors)
        {
            return WorkforceProblems.Validation(errors);
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return WorkforceProblems.ProjectNotFound();
        }

        var filter = new SheetFilter(statusFilter, fromDate, toDate, shiftFilter, mine ? caller.UserId : null);
        var query = SheetQueries.List(db, projectId, filter);
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        var people = await users.FindAsync(rows.Select(row => row.RecordedBy).Distinct().ToArray(), cancellationToken);
        var items = rows
            .Select(row => new LaborSheetSummaryView(
                row.Id,
                row.WorkDate,
                row.Shift.ToString(),
                row.Status.ToString(),
                row.RecordedBy,
                people.GetValueOrDefault(row.RecordedBy)?.NameAr,
                people.GetValueOrDefault(row.RecordedBy)?.NameEn,
                row.SubmittedAt,
                row.ReviewedAt,
                row.ReviewedBy,
                row.LineCount,
                row.WorkerDays,
                caller.SeesMoney ? row.ApprovedNet : null,
                RowVersions.Format(row.RowVersion)))
            .ToList();

        return TypedResults.Ok(new Response(items, page, pageSize, totalCount));
    }

    private static TEnum? ParseOptional<TEnum>(string? value, string field, ValidationErrors errors)
        where TEnum : struct, Enum
    {
        if (Input.Clean(value) is not { } text)
        {
            return null;
        }

        if (Input.TryParseName<TEnum>(text, out var parsed))
        {
            return parsed;
        }

        errors.Add(field, $"{field} must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}.");
        return null;
    }
}
