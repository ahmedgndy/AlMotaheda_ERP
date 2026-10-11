using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Members;

/// <summary>
/// <c>POST /api/projects/{id}/members</c>: an Executive adds a site engineer or a team member. The person can open
/// the project from this moment. The manager is set with <c>PUT /api/projects/{id}/manager</c>.
/// </summary>
internal static class AddProjectMember
{
    internal sealed record Request(int? UserId, string? Responsibility, string? StartDate);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/members", HandleAsync)
            .RequireExecutive()
            .WithName("AddProjectMember")
            .WithSummary("Add a site engineer or team member to a project (Executive only)");

    internal static async Task<Results<Created<MemberView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
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

        var today = calendar.Today;
        var errors = new ValidationErrors();

        if (request.UserId is null or < 1)
        {
            errors.Add("userId", "userId is required.");
        }

        if (!Input.TryParseName<MemberResponsibility>(request.Responsibility, out var responsibility))
        {
            errors.Add("responsibility", "responsibility must be SiteEngineer or TeamMember.");
        }
        else if (responsibility == MemberResponsibility.ProjectManager)
        {
            errors.Add("responsibility", "Set the project manager with PUT /api/projects/{id}/manager.");
        }

        var startDate = MemberDates.Start(request.StartDate, today, errors);

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await gate.CheckAsync(httpContext.User, nameof(AddProjectMember), cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var userId = request.UserId!.Value;
        var people = await users.FindAsync([userId], cancellationToken);
        if (MemberAssignment.Problem(userId, responsibility, people) is { } problem)
        {
            return ProjectProblems.NotAssignable(problem);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await ProjectLock.TryAcquireAsync(db, projectId, cancellationToken))
        {
            return ProjectProblems.TryAgainLater();
        }

        if (!await db.Projects.AnyAsync(project => project.Id == projectId, cancellationToken))
        {
            return ProjectProblems.ProjectNotFound();
        }

        if (await db.ProjectMembers.AnyAsync(
                member => member.ProjectId == projectId && member.UserId == userId && member.EndDate == null,
                cancellationToken))
        {
            return ProjectProblems.AlreadyOnProject();
        }

        var member = new ProjectMember
        {
            ProjectId = projectId,
            UserId = userId,
            Responsibility = responsibility,
            StartDate = startDate,
            AssignedAt = calendar.UtcNow,
            AssignedBy = caller.UserId,
        };
        db.ProjectMembers.Add(member);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (UniqueViolations.IndexOf(exception) is not null)
        {
            return ProjectProblems.AlreadyOnProject();
        }

        await transaction.CommitAsync(cancellationToken);
        log.MemberAdded(caller.UserId, projectId, userId, responsibility.ToString());

        // There is no endpoint for one membership, so no Location header: the team is read with
        // GET /api/projects/{id}/members.
        return TypedResults.Created((string?)null, MemberView.From(member, people, showEndDetails: true));
    }
}
