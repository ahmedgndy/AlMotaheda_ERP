using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Workforce.Common;

/// <summary>
/// Structured audit events of the Workforce module (event ids 3001 to 3999). Until the platform AuditLog table
/// exists, these entries are the record of who changed what.
/// </summary>
/// <remarks>Ids only: never names, national ids, phone numbers, notes or amounts.</remarks>
internal sealed partial class WorkforceLog(ILogger<WorkforceLog> logger)
{
    private readonly ILogger<WorkforceLog> _logger = logger;

    [LoggerMessage(EventId = 3001, Level = LogLevel.Information, Message = "User {ActorId} created trade {TradeId}")]
    public partial void TradeCreated(int actorId, int tradeId);

    [LoggerMessage(EventId = 3002, Level = LogLevel.Information, Message = "User {ActorId} updated trade {TradeId}; active {IsActive}")]
    public partial void TradeUpdated(int actorId, int tradeId, bool isActive);

    [LoggerMessage(EventId = 3011, Level = LogLevel.Information, Message = "User {ActorId} created worker {WorkerId}")]
    public partial void WorkerCreated(int actorId, int workerId);

    [LoggerMessage(EventId = 3012, Level = LogLevel.Information, Message = "User {ActorId} updated worker {WorkerId}; status {OldStatus} -> {NewStatus}")]
    public partial void WorkerUpdated(int actorId, int workerId, string oldStatus, string newStatus);

    [LoggerMessage(EventId = 3021, Level = LogLevel.Information, Message = "User {ActorId} assigned worker {WorkerId} to project {ProjectId} (assignment {AssignmentId})")]
    public partial void WorkerAssigned(int actorId, int projectId, int workerId, int assignmentId);

    [LoggerMessage(EventId = 3022, Level = LogLevel.Information, Message = "User {ActorId} ended assignment {AssignmentId} of worker {WorkerId} on project {ProjectId}")]
    public partial void AssignmentEnded(int actorId, int projectId, int workerId, int assignmentId);

    [LoggerMessage(EventId = 3031, Level = LogLevel.Information, Message = "User {ActorId} recorded labor sheet {SheetId} on project {ProjectId} with {LineCount} line(s); status {Status}")]
    public partial void SheetRecorded(int actorId, int projectId, int sheetId, int lineCount, string status);

    [LoggerMessage(EventId = 3032, Level = LogLevel.Information, Message = "User {ActorId} edited labor sheet {SheetId} on project {ProjectId}; {LineCount} line(s); status {Status}")]
    public partial void SheetEdited(int actorId, int projectId, int sheetId, int lineCount, string status);

    [LoggerMessage(EventId = 3033, Level = LogLevel.Information, Message = "User {ActorId} submitted labor sheet {SheetId} on project {ProjectId}; was {OldStatus}")]
    public partial void SheetSubmitted(int actorId, int projectId, int sheetId, string oldStatus);

    [LoggerMessage(EventId = 3034, Level = LogLevel.Information, Message = "User {ActorId} reviewed labor sheet {SheetId} on project {ProjectId}: {NewStatus}")]
    public partial void SheetReviewed(int actorId, int projectId, int sheetId, string newStatus);

    [LoggerMessage(EventId = 3035, Level = LogLevel.Information, Message = "User {ActorId} set the item allocations of {LineCount} line(s) of labor sheet {SheetId} on project {ProjectId}")]
    public partial void AllocationsChanged(int actorId, int projectId, int sheetId, int lineCount);

    [LoggerMessage(EventId = 3041, Level = LogLevel.Information, Message = "User {ActorId} recorded worker payment {PaymentId} to worker {WorkerId} with {AllocationCount} allocation(s)")]
    public partial void PaymentRecorded(int actorId, int workerId, int paymentId, int allocationCount);

    [LoggerMessage(EventId = 3042, Level = LogLevel.Information, Message = "User {ActorId} voided worker payment {PaymentId} of worker {WorkerId}")]
    public partial void PaymentVoided(int actorId, int workerId, int paymentId);

    [LoggerMessage(EventId = 3043, Level = LogLevel.Information, Message = "User {ActorId} requested payment {RequestId} to worker {WorkerId} with {AllocationCount} allocation(s)")]
    public partial void PaymentRequested(int actorId, int workerId, int requestId, int allocationCount);

    [LoggerMessage(EventId = 3091, Level = LogLevel.Warning, Message = "User {ActorId} was refused {Action}: {Reason}")]
    public partial void ActionRefused(int actorId, string action, string reason);
}
