using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace AlMotaheda.Modules.Projects.Features.Members;

/// <summary>
/// <c>GET /api/projects/assignable-users?responsibility=</c>: the active users who may hold a responsibility, for the
/// manager, site engineer and team pickers. Names and roles only.
/// </summary>
internal static class ListAssignableUsers
{
    internal sealed record Item(int UserId, string NameAr, string NameEn, string? JobTitleAr, string? JobTitleEn, string RoleCode);

    internal sealed record Response(IReadOnlyList<Item> Items);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/assignable-users", HandleAsync)
            .RequireExecutive()
            .WithName("ListAssignableUsers")
            .WithSummary("List active users who may be project manager, site engineer or team member (Executive only)");

    internal static async Task<Results<Ok<Response>, ValidationProblem>> HandleAsync(
        IUserDirectory users,
        CancellationToken cancellationToken,
        string? responsibility = null)
    {
        if (!Input.TryParseName<MemberResponsibility>(responsibility, out var parsed))
        {
            var errors = new ValidationErrors();
            errors.Add("responsibility", "responsibility must be ProjectManager, SiteEngineer or TeamMember.");
            return ProjectProblems.Validation(errors);
        }

        var people = await users.ListActiveAsync(ResponsibilityRoles.For(parsed).ToList(), cancellationToken);

        return TypedResults.Ok(new Response(people
            .Select(user => new Item(user.Id, user.NameAr, user.NameEn, user.JobTitleAr, user.JobTitleEn, user.RoleCode))
            .ToList()));
    }
}
