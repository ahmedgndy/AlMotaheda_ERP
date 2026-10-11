using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Features.Members;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Projects;

/// <summary>
/// <c>POST /api/projects</c>: an Executive creates a project together with its manager, and optionally its site
/// engineers and team.
/// </summary>
/// <remarks>
/// Executive only (decision D2): creating a project names its manager, and membership decides who may open a
/// project, so it follows the rule that only an Executive changes access. The project and its first members are
/// saved together, so a project never exists without a manager.
/// </remarks>
internal static class CreateProject
{
    internal sealed record Request(
        string? Code,
        string? NameAr,
        string? NameEn,
        string? ClientNameAr,
        string? ClientNameEn,
        string? LocationAr,
        string? LocationEn,
        string? ContractNo,
        string? AddressAr,
        string? AddressEn,
        decimal? SiteLatitude,
        decimal? SiteLongitude,
        int? SiteRadiusMeters,
        string? StartDate,
        string? EndDate,
        string? Status,
        string? Notes,
        int? ManagerUserId,
        int[]? SiteEngineerUserIds,
        int[]? TeamUserIds) : IProjectFieldsRequest;

    /// <summary>The largest team that can be named when creating a project; more can be added afterwards.</summary>
    public const int MaxInitialMembers = 100;

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/", HandleAsync)
            .RequireExecutive()
            .WithName("CreateProject")
            .WithSummary("Create a project with its manager, site engineers and team (Executive only)");

    internal static async Task<Results<Created<ProjectDetails>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        ProjectAccess access,
        IUserDirectory users,
        ExecutiveGate gate,
        BusinessCalendar calendar,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = ProjectFields.Validate(request, ProjectStatus.Planning, errors);
        var team = InitialTeam.Validate(request.ManagerUserId, request.SiteEngineerUserIds, request.TeamUserIds, errors);

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await gate.CheckAsync(httpContext.User, nameof(CreateProject), cancellationToken) is { } refusal)
        {
            return refusal;
        }

        // Every named person must be an active user whose role fits the responsibility. Read fresh from Access.
        var people = await users.FindAsync(team!.UserIds, cancellationToken);
        team.CheckPeople(people, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await db.Projects.AnyAsync(project => project.Code == fields!.Code, cancellationToken))
        {
            return ProjectProblems.CodeTaken();
        }

        var now = calendar.UtcNow;
        var today = calendar.Today;

        var project = new Project
        {
            Code = fields!.Code,
            NameAr = fields.NameAr,
            NameEn = fields.NameEn,
            ClientNameEn = fields.ClientNameEn,
            CreatedAt = now,
            CreatedBy = caller.UserId,
        };
        fields.ApplyTo(project);

        foreach (var (userId, responsibility) in team.Members)
        {
            project.Members.Add(new ProjectMember
            {
                UserId = userId,
                Responsibility = responsibility,
                StartDate = today,
                AssignedAt = now,
                AssignedBy = caller.UserId,
            });
        }

        db.Projects.Add(project);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (UniqueViolations.IndexOf(exception) == ProjectConfiguration.CodeIndex)
        {
            return ProjectProblems.CodeTaken();
        }

        log.ProjectCreated(caller.UserId, project.Id, project.Code);
        foreach (var member in project.Members)
        {
            log.MemberAdded(caller.UserId, project.Id, member.UserId, member.Responsibility.ToString());
        }

        var details = await ProjectDetails.LoadAsync(db, access, users, caller, project.Id, cancellationToken);
        return TypedResults.Created($"/api/projects/{project.Id}", details!);
    }

    /// <summary>The manager, site engineers and team named on the create form.</summary>
    private sealed class InitialTeam
    {
        private InitialTeam(List<(int UserId, MemberResponsibility Responsibility)> members) => Members = members;

        public IReadOnlyList<(int UserId, MemberResponsibility Responsibility)> Members { get; }

        public IReadOnlyCollection<int> UserIds => Members.Select(member => member.UserId).ToList();

        public static InitialTeam? Validate(int? managerUserId, int[]? siteEngineerUserIds, int[]? teamUserIds, ValidationErrors errors)
        {
            if (managerUserId is null or < 1)
            {
                errors.Add("managerUserId", "Project manager is required.");
                return null;
            }

            var engineers = (siteEngineerUserIds ?? []).Distinct().ToList();
            var team = (teamUserIds ?? []).Distinct().ToList();

            if (engineers.Any(id => id < 1))
            {
                errors.Add("siteEngineerUserIds", "User ids must be positive numbers.");
            }

            if (team.Any(id => id < 1))
            {
                errors.Add("teamUserIds", "User ids must be positive numbers.");
            }

            // As in the prototype, the manager is always on the team; naming them again is not an error.
            engineers.Remove(managerUserId.Value);
            team.Remove(managerUserId.Value);

            if (engineers.Intersect(team).Any())
            {
                errors.Add("teamUserIds", "A person can be a site engineer or a team member, not both.");
            }

            if (engineers.Count + team.Count + 1 > MaxInitialMembers)
            {
                errors.Add("teamUserIds", $"At most {MaxInitialMembers} people can be named when creating a project.");
            }

            if (errors.Has("siteEngineerUserIds") || errors.Has("teamUserIds"))
            {
                return null;
            }

            var members = new List<(int, MemberResponsibility)> { (managerUserId.Value, MemberResponsibility.ProjectManager) };
            members.AddRange(engineers.Select(id => (id, MemberResponsibility.SiteEngineer)));
            members.AddRange(team.Select(id => (id, MemberResponsibility.TeamMember)));
            return new InitialTeam(members);
        }

        public void CheckPeople(IReadOnlyDictionary<int, UserSummary> people, ValidationErrors errors)
        {
            foreach (var (userId, responsibility) in Members)
            {
                var field = responsibility switch
                {
                    MemberResponsibility.ProjectManager => "managerUserId",
                    MemberResponsibility.SiteEngineer => "siteEngineerUserIds",
                    _ => "teamUserIds",
                };

                if (MemberAssignment.Problem(userId, responsibility, people) is { } problem)
                {
                    errors.Add(field, problem);
                }
            }
        }
    }
}
