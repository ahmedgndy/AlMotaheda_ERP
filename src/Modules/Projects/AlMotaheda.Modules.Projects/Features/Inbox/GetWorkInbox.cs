using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Inbox;

/// <summary>
/// <c>GET /api/inbox</c>: what is waiting for the caller across their projects: records to review, records returned
/// to them, reports asked of them. The app polls it for the notification badge and shows it as "My work".
/// </summary>
/// <remarks>
/// <para>Computed from the records on every call (see <see cref="IWorkInboxSource"/>), so nothing is ever missed or
/// stale, and an item disappears as soon as someone acts on it. There is no separate notification table, read state
/// or push delivery; those need a decision on retention and a push provider (docs/qa).</para>
/// <para>Scope: the projects the caller may see and still change (completed projects only for an Executive). Each
/// module checks its own permission; amounts appear only with See money in that module.</para>
/// </remarks>
internal static class GetWorkInbox
{
    /// <summary>The most items each module returns; the answer says when a list was cut.</summary>
    public const int SourceLimit = 100;

    internal sealed record PersonRef(int UserId, string NameAr, string NameEn);

    internal sealed record Item(
        string Reason,
        string RecordType,
        int ProjectId,
        string ProjectCode,
        string ProjectNameAr,
        string ProjectNameEn,
        int RecordId,
        int? ParentId,
        DateOnly Date,
        DateTime Since,
        string Summary,
        decimal? Amount,
        PersonRef? From,
        string? Note);

    internal sealed record Counts(int Total, int Review, int Returned, int Requested);

    internal sealed record Response(IReadOnlyList<Item> Items, Counts Counts, bool Truncated);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/inbox", HandleAsync)
            .WithName("GetWorkInbox")
            .WithSummary("What is waiting for the caller: records to review, records returned to them, reports asked of them");

    internal static async Task<Results<Ok<Response>, ProblemHttpResult>> HandleAsync(
        HttpContext httpContext,
        ProjectAccess access,
        IEnumerable<IWorkInboxSource> sources,
        IUserDirectory users,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var projects = await access.VisibleProjects(caller).AsNoTracking()
            .Where(project => project.Status != ProjectStatus.Completed || caller.IsExecutive)
            .Select(project => new { project.Id, project.Code, project.NameAr, project.NameEn })
            .ToDictionaryAsync(project => project.Id, cancellationToken);

        var found = new List<WorkInboxItem>();
        var truncated = false;
        if (projects.Count > 0)
        {
            var query = new WorkInboxQuery(httpContext.User, caller.UserId, projects.Keys.ToList(), SourceLimit);
            foreach (var source in sources)
            {
                var part = await source.ListAsync(query, cancellationToken);
                truncated |= part.GroupBy(item => (item.Reason, item.RecordType)).Any(group => group.Count() >= SourceLimit);
                found.AddRange(part.Where(item => projects.ContainsKey(item.ProjectId)));
            }
        }

        var people = await users.FindAsync(
            found.Where(item => item.FromUserId is not null).Select(item => item.FromUserId!.Value).Distinct().ToList(), cancellationToken);

        var items = found
            .OrderByDescending(item => item.Since)
            .Select(item =>
            {
                var project = projects[item.ProjectId];
                var from = item.FromUserId is { } id && people.TryGetValue(id, out var person) ? new PersonRef(id, person.NameAr, person.NameEn) : null;
                return new Item(item.Reason, item.RecordType, item.ProjectId, project.Code, project.NameAr, project.NameEn, item.RecordId, item.ParentId,
                    item.Date, item.Since, item.Summary, item.Amount, from, item.Note);
            })
            .ToList();

        var counts = new Counts(
            items.Count,
            items.Count(item => item.Reason == WorkInboxReasons.Review),
            items.Count(item => item.Reason == WorkInboxReasons.Returned),
            items.Count(item => item.Reason == WorkInboxReasons.Requested));

        return TypedResults.Ok(new Response(items, counts, truncated));
    }
}
