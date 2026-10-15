using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace AlMotaheda.Modules.Projects.Features.Reports;

/// <summary>
/// <c>GET /api/projects/{pid}/reports/cost-summary?from=&amp;to=</c>: the cost of a project per cost group in a
/// period, by the business date of the records. Recognized, paid, outstanding and pending review are separate
/// columns; nothing pending is ever inside recognized.
/// </summary>
/// <remarks>
/// Commitments are not in this report: a signed contract has no date in a period. The overview shows them.
/// </remarks>
internal static class GetCostSummary
{
    internal sealed record Group(
        string CostGroup,
        bool HasSource,
        decimal Recognized,
        decimal Paid,
        decimal Outstanding,
        decimal PendingReview,
        int RecognizedRecords,
        int PendingRecords);

    internal sealed record Totals(
        decimal Recognized,
        decimal Paid,
        decimal Outstanding,
        decimal PendingReview,
        int RecognizedRecords,
        int PendingRecords);

    internal sealed record Response(DateOnly? From, DateOnly? To, IReadOnlyList<Group> Groups, Totals Totals);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/reports/cost-summary", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.SeeMoney)
            .WithName("GetProjectCostSummary")
            .WithSummary("Per cost group in a period: recognized, paid, outstanding, pending review and record counts");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        IProjectAccess access,
        ProjectCostLoader costs,
        CancellationToken cancellationToken,
        string? from = null,
        string? to = null)
    {
        if (ProjectCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var first = Input.OptionalDate(from, errors, "from", "from");
        var last = Input.OptionalDate(to, errors, "to", "to");
        if (first is not null && last is not null && first > last)
        {
            errors.Add("to", "to must be on or after from.");
        }

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var data = await costs.LoadAsync(projectId, first, last, cancellationToken);
        var totals = ProjectCosts.Total(data.Groups);

        var groups = data.Groups
            .Select(group => new Group(
                group.CostGroup,
                group.HasSource,
                group.Recognized,
                group.Paid,
                group.Outstanding,
                group.PendingReview,
                group.RecognizedRecords,
                group.PendingRecords))
            .ToList();

        return TypedResults.Ok(new Response(
            first,
            last,
            groups,
            new Totals(totals.Recognized, totals.Paid, totals.Outstanding, totals.PendingReview, totals.RecognizedRecords, totals.PendingRecords)));
    }
}
