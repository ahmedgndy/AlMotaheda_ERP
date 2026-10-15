using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Data.Configurations;
using AlMotaheda.Modules.Fleet.Security;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Features.Assignments;

/// <summary>
/// <c>POST /api/projects/{pid}/equipment</c>: send a machine of the register to a project. The same register row is
/// used on every project; this adds one stay to its history.
/// </summary>
/// <remarks>
/// A machine on another project answers 409 <c>equipment_assigned</c> (end that stay first); one under maintenance,
/// lost or retired answers 409 <c>equipment_unavailable</c>. Runs under the machine's lock, and the filtered unique
/// index on open assignments is the last guard against two projects taking the same machine at once.
/// </remarks>
internal static class AssignEquipment
{
    internal sealed record Request(int? EquipmentId, int? ResponsibleUserId, string? OperatorName, string? StartDate, string? Notes);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/equipment", HandleAsync)
            .RequirePermission(FleetPermissions.Module, PermissionAction.Edit)
            .WithName("AssignEquipment")
            .WithSummary("Send a machine to a project (startDate defaults to today). 409 equipment_assigned, equipment_unavailable");

    internal static async Task<Results<Created<EquipmentAssignmentView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        Request request,
        HttpContext httpContext,
        FleetDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CompanyClock clock,
        FleetLog log,
        CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var today = clock.Today;
        var errors = new ValidationErrors();

        if (request.EquipmentId is null or < 1)
        {
            errors.Add("equipmentId", "equipmentId is required.");
        }

        if (request.ResponsibleUserId is < 1)
        {
            errors.Add("responsibleUserId", "responsibleUserId is not valid.");
        }

        var operatorName = Input.OptionalText(
            request.OperatorName, EquipmentAssignmentConfiguration.OperatorNameMaxLength, errors, "operatorName", "The operator name");
        var notes = Input.OptionalText(request.Notes, EquipmentAssignmentConfiguration.NotesMaxLength, errors, "notes", "Notes");
        var startDate = Input.OptionalDate(request.StartDate, errors, "startDate", "The start date") ?? today;
        if (startDate > today)
        {
            errors.Add("startDate", "The start date can't be in the future.");
        }

        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return FleetProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return FleetProblems.CompletedProject();
        }

        var equipmentId = request.EquipmentId!.Value;

        if (request.ResponsibleUserId is { } responsibleId)
        {
            var people = await users.FindAsync([responsibleId], cancellationToken);
            if (!people.TryGetValue(responsibleId, out var person))
            {
                errors.Add("responsibleUserId", "The user was not found.");
            }
            else if (!person.IsActive)
            {
                errors.Add("responsibleUserId", "This user is disabled and can't be made responsible.");
            }
        }

        if (!await db.Equipment.AnyAsync(candidate => candidate.Id == equipmentId, cancellationToken))
        {
            errors.Add("equipmentId", "The machine was not found in the register.");
        }

        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await AppLock.TryAcquireAsync(db, AppLock.Equipment(equipmentId), cancellationToken))
        {
            return FleetProblems.TryAgainLater();
        }

        // Read under the lock: where the machine is and when its last stay ended.
        var equipment = await db.Equipment.AsNoTracking()
            .Where(candidate => candidate.Id == equipmentId)
            .Select(candidate => new
            {
                candidate.Status,
                HasOpen = db.EquipmentAssignments.Any(assignment => assignment.EquipmentId == candidate.Id && assignment.EndDate == null),
                LastEnd = db.EquipmentAssignments
                    .Where(assignment => assignment.EquipmentId == candidate.Id && assignment.EndDate != null)
                    .Max(assignment => assignment.EndDate),
            })
            .SingleAsync(cancellationToken);

        switch (AssignmentRules.CanAssign(equipment.Status, equipment.HasOpen, equipment.LastEnd, startDate))
        {
            case AssignDecision.AlreadyAssigned:
                return FleetProblems.AlreadyAssigned();
            case AssignDecision.Unavailable:
                return FleetProblems.Unavailable(equipment.Status.ToString());
            case AssignDecision.OverlapsHistory:
                errors.Add("startDate", $"The machine was on a project until {equipment.LastEnd:yyyy-MM-dd}; the start date can't be before that.");
                return FleetProblems.Validation(errors);
        }

        var assignment = new EquipmentAssignment
        {
            EquipmentId = equipmentId,
            ProjectId = projectId,
            ResponsibleUserId = request.ResponsibleUserId,
            OperatorName = operatorName,
            StartDate = startDate,
            Notes = notes,
            AssignedAt = clock.UtcNow,
            AssignedBy = caller.UserId,
        };
        db.EquipmentAssignments.Add(assignment);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, EquipmentAssignmentConfiguration.OpenAssignmentIndex))
        {
            return FleetProblems.AlreadyAssigned();
        }

        await transaction.CommitAsync(cancellationToken);
        log.EquipmentAssigned(caller.UserId, projectId, equipmentId, assignment.Id);

        var row = await AssignmentQueries.One(db, projectId, assignment.Id).SingleAsync(cancellationToken);
        var view = (await EquipmentAssignmentView.FromAsync([row], project, users, cancellationToken))[0];
        return TypedResults.Created($"/api/projects/{projectId}/equipment", view);
    }
}
