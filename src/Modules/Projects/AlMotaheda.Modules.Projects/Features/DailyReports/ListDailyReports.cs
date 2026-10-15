using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.DailyReports;

/// <summary><c>GET /api/projects/{pid}/daily-reports</c>: one page of the project's daily site reports, newest day first.</summary>
internal static class ListDailyReports
{
    internal sealed record Response(IReadOnlyList<DailyReportListItem> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/daily-reports", HandleAsync)
            .RequirePermission(SitePermissions.Module, PermissionAction.View)
            .WithName("ListDailyReports")
            .WithSummary("List a project's daily site reports, newest first (filters: status, from, to, authorUserId)");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CancellationToken cancellationToken,
        string? status = null,
        string? from = null,
        string? to = null,
        int? authorUserId = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);

        DailyReportStatus? statusFilter = null;
        if (Input.Clean(status) is { } statusText)
        {
            if (Input.TryParseName<DailyReportStatus>(statusText, out var parsed))
            {
                statusFilter = parsed;
            }
            else
            {
                errors.Add("status", $"status must be one of: {string.Join(", ", Enum.GetNames<DailyReportStatus>())}.");
            }
        }

        var fromDate = Input.OptionalDate(from, errors, "from", "from");
        var toDate = Input.OptionalDate(to, errors, "to", "to");
        if (fromDate is { } f && toDate is { } t && t < f)
        {
            errors.Add("to", "to must not be before from.");
        }

        if (authorUserId is < 1)
        {
            errors.Add("authorUserId", "authorUserId is not valid.");
        }

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var query = DailyReportQueries.Filtered(db, projectId, new DailyReportFilter(statusFilter, fromDate, toDate, authorUserId));
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        var people = await PlanningPeople.FindAsync(
            users,
            rows.Select(row => row.AuthorUserId).Concat(rows.Where(row => row.ReviewedBy is not null).Select(row => row.ReviewedBy!.Value)),
            cancellationToken);

        return TypedResults.Ok(new Response(rows.Select(row => DailyReportListItem.From(row, people)).ToList(), page, pageSize, totalCount));
    }
}
