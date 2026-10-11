using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Projects.Common;

/// <summary>
/// Structured audit events for projects and project membership. Until the platform AuditLog table exists,
/// these entries are the record of who changed what.
/// </summary>
/// <remarks>
/// People and projects are identified by id only. Names, notes, addresses and contact details are never logged;
/// changes are logged by field name, never by value.
/// </remarks>
internal sealed partial class ProjectsLog(ILogger<ProjectsLog> logger)
{
    private readonly ILogger<ProjectsLog> _logger = logger;

    [LoggerMessage(EventId = 2001, Level = LogLevel.Information, Message = "User {ActorId} created project {ProjectId} ({Code})")]
    public partial void ProjectCreated(int actorId, int projectId, string code);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Information, Message = "User {ActorId} updated project {ProjectId}: changed {ChangedFields}; status {OldStatus} -> {NewStatus}")]
    public partial void ProjectUpdated(int actorId, int projectId, string changedFields, string oldStatus, string newStatus);

    [LoggerMessage(EventId = 2101, Level = LogLevel.Information, Message = "User {ActorId} made user {UserId} the manager of project {ProjectId}; previous manager {PreviousUserId}")]
    public partial void ManagerChanged(int actorId, int projectId, int userId, int? previousUserId);

    [LoggerMessage(EventId = 2102, Level = LogLevel.Information, Message = "User {ActorId} added user {UserId} to project {ProjectId} as {Responsibility}")]
    public partial void MemberAdded(int actorId, int projectId, int userId, string responsibility);

    [LoggerMessage(EventId = 2103, Level = LogLevel.Information, Message = "User {ActorId} ended membership {MemberId} of user {UserId} on project {ProjectId} ({Responsibility})")]
    public partial void MemberEnded(int actorId, int projectId, int memberId, int userId, string responsibility);

    [LoggerMessage(EventId = 2301, Level = LogLevel.Warning, Message = "User {ActorId} was refused {Action}: {Reason}")]
    public partial void ActionRefused(int? actorId, string action, string reason);
}
