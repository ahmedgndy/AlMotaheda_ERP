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
/// <c>PUT /api/projects/{id}/manager</c>: an Executive assigns or replaces the project manager.
/// </summary>
/// <remarks>
/// <para>The current manager's membership ends on the new start date and the new one begins, in one transaction,
/// so a project is never left without a manager. If the new manager was already on the project in another
/// responsibility, that membership ends too. History is kept; nothing is deleted.</para>
/// <para>Sending the current manager again changes nothing and returns their membership.</para>
/// </remarks>
internal static class SetProjectManager
{
    internal sealed record Request(int? UserId, string? StartDate, string? Reason);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPut("/{projectId:int}/manager", HandleAsync)
            .RequireExecutive()
            .WithName("SetProjectManager")
            .WithSummary("Assign or replace the project manager (Executive only)");

    internal static async Task<Results<Ok<MemberView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
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

        var startDate = MemberDates.Start(request.StartDate, today, errors);
        var reason = Input.OptionalText(request.Reason, ProjectMemberConfiguration.EndReasonMaxLength, errors, "reason", "Reason");

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await gate.CheckAsync(httpContext.User, nameof(SetProjectManager), cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var userId = request.UserId!.Value;
        var people = await users.FindAsync([userId], cancellationToken);
        if (MemberAssignment.Problem(userId, MemberResponsibility.ProjectManager, people) is { } problem)
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

        var active = await db.ProjectMembers
            .Where(member => member.ProjectId == projectId && member.EndDate == null)
            .ToListAsync(cancellationToken);

        var currentManager = active.FirstOrDefault(member => member.Responsibility == MemberResponsibility.ProjectManager);
        if (currentManager?.UserId == userId)
        {
            return TypedResults.Ok(MemberView.From(currentManager, people, showEndDetails: true));
        }

        // The memberships that end: the current manager's, and the new manager's own if they're already on the team.
        var ending = active.Where(member => member == currentManager || member.UserId == userId).ToList();
        if (ending.FirstOrDefault(member => startDate < member.StartDate) is { } replaced)
        {
            errors.Add("startDate", $"The start date can't be before {replaced.StartDate:yyyy-MM-dd}, when the membership it replaces began.");
            return ProjectProblems.Validation(errors);
        }

        var now = calendar.UtcNow;
        foreach (var member in ending)
        {
            member.EndDate = startDate;
            member.EndedAt = now;
            member.EndedBy = caller.UserId;
            member.EndReason = member == currentManager
                ? reason ?? "Replaced as project manager"
                : "Became the project manager";
        }

        var newManager = new ProjectMember
        {
            ProjectId = projectId,
            UserId = userId,
            Responsibility = MemberResponsibility.ProjectManager,
            StartDate = startDate,
            AssignedAt = now,
            AssignedBy = caller.UserId,
        };

        try
        {
            // Two saves: the filtered unique indexes need the old rows ended before the new row arrives.
            await db.SaveChangesAsync(cancellationToken);
            db.ProjectMembers.Add(newManager);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (UniqueViolations.IndexOf(exception) is not null)
        {
            // Not expected while the project row is locked; answered as a conflict rather than a server error.
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);

        foreach (var member in ending.Where(member => member != currentManager))
        {
            log.MemberEnded(caller.UserId, projectId, member.Id, member.UserId, member.Responsibility.ToString());
        }

        log.ManagerChanged(caller.UserId, projectId, userId, currentManager?.UserId);

        return TypedResults.Ok(MemberView.From(newManager, people, showEndDetails: true));
    }
}
