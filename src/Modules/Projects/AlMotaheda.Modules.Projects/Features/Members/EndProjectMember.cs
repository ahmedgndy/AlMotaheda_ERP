using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Members;

/// <summary>
/// <c>POST /api/projects/{id}/members/{memberId}/end</c>: an Executive ends a site engineer's or team member's time
/// on the project. The person can no longer open it (unless their role sees every project). The row is kept as
/// history. A manager is replaced, not ended.
/// </summary>
internal static class EndProjectMember
{
    internal sealed record Request(string? EndDate, string? Reason, string? RowVersion);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/members/{memberId:int}/end", HandleAsync)
            .RequireExecutive()
            .WithName("EndProjectMember")
            .WithSummary("End a site engineer's or team member's membership, keeping its history (Executive only)");

    internal static async Task<Results<Ok<MemberView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int memberId,
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

        var errors = new ValidationErrors();
        var endDate = MemberDates.End(request.EndDate, calendar.Today, errors);
        var reason = Input.RequiredText(request.Reason, ProjectMemberConfiguration.EndReasonMaxLength, errors, "reason", "Reason");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await gate.CheckAsync(httpContext.User, nameof(EndProjectMember), cancellationToken) is { } refusal)
        {
            return refusal;
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

        // The membership must belong to the project in the route.
        var member = await db.ProjectMembers.SingleOrDefaultAsync(
            candidate => candidate.Id == memberId && candidate.ProjectId == projectId,
            cancellationToken);

        if (member is null)
        {
            return ProjectProblems.MembershipNotFound();
        }

        if (member.EndDate is not null)
        {
            return ProjectProblems.AlreadyEnded();
        }

        if (member.Responsibility == MemberResponsibility.ProjectManager)
        {
            return ProjectProblems.ManagerCannotBeEnded();
        }

        if (endDate < member.StartDate)
        {
            errors.Add("endDate", $"The end date can't be before the start date ({member.StartDate:yyyy-MM-dd}).");
            return ProjectProblems.Validation(errors);
        }

        member.EndDate = endDate;
        member.EndedAt = calendar.UtcNow;
        member.EndedBy = caller.UserId;
        member.EndReason = reason;
        db.Entry(member).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.MemberEnded(caller.UserId, projectId, member.Id, member.UserId, member.Responsibility.ToString());

        var people = await users.FindAsync([member.UserId], cancellationToken);
        return TypedResults.Ok(MemberView.From(member, people, showEndDetails: true));
    }
}
