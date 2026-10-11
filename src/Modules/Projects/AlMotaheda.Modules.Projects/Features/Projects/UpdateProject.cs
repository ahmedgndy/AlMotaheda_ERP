using AlMotaheda.BuildingBlocks.Authorization;
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

namespace AlMotaheda.Modules.Projects.Features.Projects;

/// <summary>
/// <c>PUT /api/projects/{id}</c>: edit a project's information and status. People are changed through the
/// membership endpoints, not here.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Needs projects Edit, and the project must be in the caller's scope (404 otherwise).</item>
///   <item>An Executive may edit any project; anyone else only as the project's active manager (D3).</item>
///   <item>Only an Executive changes the code (D14), and only an Executive changes a completed project (D8).
///   A project manager may set their project to Completed, but can't reopen it.</item>
///   <item>The manager check runs under the project's lock, the same lock a manager replacement takes, so an edit
///   by a manager who has just been replaced can't slip through.</item>
/// </list>
/// </remarks>
internal static class UpdateProject
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
        string? RowVersion) : IProjectFieldsRequest;

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPut("/{projectId:int}", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.Edit)
            .WithName("UpdateProject")
            .WithSummary("Edit a project's information and status (an Executive, or the project's manager)");

    internal static async Task<Results<Ok<ProjectDetails>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        ProjectAccess access,
        IUserDirectory users,
        BusinessCalendar calendar,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        // A status is always sent on edit: the form shows the current one.
        var fields = ProjectFields.Validate(request, defaultStatus: null, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (!await access.VisibleProjects(caller).AnyAsync(candidate => candidate.Id == projectId, cancellationToken))
        {
            return ProjectProblems.ProjectNotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await ProjectLock.TryAcquireAsync(db, projectId, cancellationToken))
        {
            return ProjectProblems.TryAgainLater();
        }

        var project = await db.Projects.SingleAsync(candidate => candidate.Id == projectId, cancellationToken);
        var responsibility = caller.IsExecutive
            ? null
            : await access.ResponsibilityAsync(caller.UserId, projectId, cancellationToken);

        switch (ProjectRules.Edit(caller, responsibility, project.Status))
        {
            case EditDecision.NotAllowed:
                log.ActionRefused(caller.UserId, nameof(UpdateProject), "not an Executive or the project's manager");
                return ProjectProblems.EditNotAllowed();
            case EditDecision.ProjectCompleted:
                log.ActionRefused(caller.UserId, nameof(UpdateProject), "project is completed");
                return ProjectProblems.CompletedProject();
        }

        if (fields!.Code != project.Code)
        {
            if (!ProjectRules.CanChangeCode(caller))
            {
                log.ActionRefused(caller.UserId, nameof(UpdateProject), "changing the project code");
                return ProjectProblems.CodeChangeNotAllowed();
            }

            if (await db.Projects.AnyAsync(other => other.Id != projectId && other.Code == fields.Code, cancellationToken))
            {
                return ProjectProblems.CodeTaken();
            }
        }

        var changedFields = fields.ChangedFields(project);
        var oldStatus = project.Status;

        fields.ApplyTo(project);
        project.UpdatedAt = calendar.UtcNow;
        project.UpdatedBy = caller.UserId;

        // Saves only if nobody changed the project since the caller read it.
        db.Entry(project).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (UniqueViolations.IndexOf(exception) == ProjectConfiguration.CodeIndex)
        {
            return ProjectProblems.CodeTaken();
        }

        await transaction.CommitAsync(cancellationToken);
        log.ProjectUpdated(caller.UserId, project.Id, changedFields, oldStatus.ToString(), project.Status.ToString());

        var details = await ProjectDetails.LoadAsync(db, access, users, caller, project.Id, cancellationToken);
        return details is null ? ProjectProblems.ProjectNotFound() : TypedResults.Ok(details);
    }
}
