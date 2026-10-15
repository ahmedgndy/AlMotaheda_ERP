using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace AlMotaheda.Modules.Projects.Features.ReportRequests;

/// <summary>
/// <c>POST /api/projects/{pid}/report-requests</c>: ask one person for one piece of information about the
/// project (reports Edit), or, with <c>isProactive: true</c>, file a report nobody asked for (reports Create).
/// </summary>
/// <remarks>
/// <para>A request is created <c>Requested</c>; the caller is its requester and <c>recipientUserId</c> answers.</para>
/// <para>A proactive report is created <c>Submitted</c> with its text: the caller is its recipient (the author),
/// and <c>sendToUserId</c> is the requester, who completes or returns it. So one state machine serves both.</para>
/// <para>The other person must be an active user who can open the project now.</para>
/// </remarks>
internal static class CreateReportRequest
{
    internal sealed record Request(
        bool IsProactive,
        int? RecipientUserId,
        int? SendToUserId,
        string? Type,
        string? Priority,
        string? Title,
        string? Description,
        string? DueDate,
        string? ResponseText);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapPost("/{projectId:int}/report-requests", HandleAsync)
            .WithName("CreateReportRequest")
            .WithSummary("Ask someone for a report (reports Edit), or file a report nobody asked for with isProactive (reports Create)");

    internal static async Task<Results<Created<ReportRequestView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        // The permission depends on what is sent: asking needs Edit, filing needs Create.
        if (request.IsProactive && !httpContext.User.HasPermission(ReportPermissions.Module, PermissionAction.Create))
        {
            return PlanningProblems.ProactiveReportsNeedCreate();
        }

        if (!request.IsProactive && !httpContext.User.HasPermission(ReportPermissions.Module, PermissionAction.Edit))
        {
            return PlanningProblems.ReportRequestsNeedEdit();
        }

        var errors = new ValidationErrors();
        var today = clock.Today;

        if (!Input.TryParseName<ReportRequestType>(request.Type, out var type))
        {
            errors.Add("type", $"type must be one of: {string.Join(", ", Enum.GetNames<ReportRequestType>())}.");
        }

        var priority = ReportRequestPriority.Normal;
        if (Input.Clean(request.Priority) is not null && !Input.TryParseName(request.Priority, out priority))
        {
            errors.Add("priority", "priority must be Normal, High or Urgent.");
        }

        var title = Input.RequiredText(request.Title, ReportRequestConfiguration.TitleMaxLength, errors, "title", "The title");
        var description = Input.OptionalText(request.Description, ReportRequestConfiguration.DescriptionMaxLength, errors, "description", "The description");
        var dueDate = Input.OptionalDate(request.DueDate, errors, "dueDate", "The due date");

        int otherUserId;
        string? responseText;
        if (request.IsProactive)
        {
            otherUserId = Person(request.SendToUserId, "sendToUserId", caller.UserId, errors);
            responseText = Input.RequiredText(request.ResponseText, ReportRequestConfiguration.ResponseMaxLength, errors, "responseText", "The report text");
            if (request.RecipientUserId is not null)
            {
                errors.Add("recipientUserId", "A filed report has no recipient: you are its author. Use sendToUserId.");
            }

            if (dueDate is not null)
            {
                errors.Add("dueDate", "A filed report has no due date.");
            }
        }
        else
        {
            otherUserId = Person(request.RecipientUserId, "recipientUserId", caller.UserId, errors);
            responseText = null;
            if (request.SendToUserId is not null)
            {
                errors.Add("sendToUserId", "sendToUserId is only for a filed report (isProactive).");
            }

            if (Input.Clean(request.ResponseText) is not null)
            {
                errors.Add("responseText", "The answer is written by the recipient, after the request is sent.");
            }

            if (dueDate < today)
            {
                errors.Add("dueDate", "The due date can't be in the past.");
            }
        }

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return ProjectProblems.CompletedProject();
        }

        if (!await ReportRequestParties.CanTakePartAsync(db, users, projectId, otherUserId, cancellationToken))
        {
            errors.Add(
                request.IsProactive ? "sendToUserId" : "recipientUserId",
                "This person is not an active user who can open this project.");
            return ProjectProblems.Validation(errors);
        }

        var now = clock.UtcNow;
        var created = new ReportRequest
        {
            ProjectId = projectId,
            RequesterUserId = request.IsProactive ? otherUserId : caller.UserId,
            RecipientUserId = request.IsProactive ? caller.UserId : otherUserId,
            Type = type,
            Priority = priority,
            Title = title!,
            Description = description,
            DueDate = dueDate,
            IsProactive = request.IsProactive,
            Status = request.IsProactive ? ReportRequestStatus.Submitted : ReportRequestStatus.Requested,
            ResponseText = responseText,
            CreatedAt = now,
            StartedAt = request.IsProactive ? now : null,
            SubmittedAt = request.IsProactive ? now : null,
        };
        db.ReportRequests.Add(created);
        await db.SaveChangesAsync(cancellationToken);

        log.ReportRequestCreated(caller.UserId, projectId, created.Id, created.RecipientUserId, created.IsProactive);

        var view = await ReportRequestView.LoadAsync(db, users, caller, projectId, created.Id, today, cancellationToken);
        return TypedResults.Created($"/api/projects/{projectId}/report-requests/{created.Id}", view!);
    }

    private static int Person(int? userId, string field, int callerUserId, ValidationErrors errors)
    {
        if (userId is null)
        {
            errors.Add(field, $"{field} is required.");
            return 0;
        }

        if (userId < 1)
        {
            errors.Add(field, $"{field} is not valid.");
            return 0;
        }

        if (userId == callerUserId)
        {
            errors.Add(field, "You can't send a report request to yourself.");
            return 0;
        }

        return userId.Value;
    }
}
