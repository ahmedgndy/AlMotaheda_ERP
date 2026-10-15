using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Subcontractors.Common;

/// <summary>
/// Structured audit events of the Subcontractors module (event ids 4001 to 4999). Until the platform AuditLog
/// table exists, these entries are the record of who changed what.
/// </summary>
/// <remarks>Ids only: never names, notes or amounts.</remarks>
internal sealed partial class SubcontractorsLog(ILogger<SubcontractorsLog> logger)
{
    private readonly ILogger<SubcontractorsLog> _logger = logger;

    [LoggerMessage(EventId = 4001, Level = LogLevel.Information, Message = "User {ActorId} created subcontractor {SubcontractorId}")]
    public partial void SubcontractorCreated(int actorId, int subcontractorId);

    [LoggerMessage(EventId = 4002, Level = LogLevel.Information, Message = "User {ActorId} updated subcontractor {SubcontractorId}; status {OldStatus} -> {NewStatus}")]
    public partial void SubcontractorUpdated(int actorId, int subcontractorId, string oldStatus, string newStatus);

    [LoggerMessage(EventId = 4011, Level = LogLevel.Information, Message = "User {ActorId} created contract {ContractId} for subcontractor {SubcontractorId} on project {ProjectId} ({Model})")]
    public partial void ContractCreated(int actorId, int projectId, int contractId, int subcontractorId, string model);

    [LoggerMessage(EventId = 4012, Level = LogLevel.Information, Message = "User {ActorId} updated contract {ContractId} on project {ProjectId}; status {OldStatus} -> {NewStatus}")]
    public partial void ContractUpdated(int actorId, int projectId, int contractId, string oldStatus, string newStatus);

    [LoggerMessage(EventId = 4021, Level = LogLevel.Information, Message = "User {ActorId} added item {ContractItemId} to contract {ContractId} on project {ProjectId}")]
    public partial void ContractItemCreated(int actorId, int projectId, int contractId, int contractItemId);

    [LoggerMessage(EventId = 4022, Level = LogLevel.Information, Message = "User {ActorId} updated item {ContractItemId} of contract {ContractId} on project {ProjectId}")]
    public partial void ContractItemUpdated(int actorId, int projectId, int contractId, int contractItemId);

    [LoggerMessage(EventId = 4023, Level = LogLevel.Information, Message = "User {ActorId} deleted item {ContractItemId} of contract {ContractId} on project {ProjectId}")]
    public partial void ContractItemDeleted(int actorId, int projectId, int contractId, int contractItemId);

    [LoggerMessage(EventId = 4024, Level = LogLevel.Information, Message = "User {ActorId} linked contract item {ContractItemId} on project {ProjectId} to project item {ProjectItemId}; previous item {PreviousItemId}")]
    public partial void ContractItemLinked(int actorId, int projectId, int contractItemId, int? projectItemId, int? previousItemId);

    [LoggerMessage(EventId = 4031, Level = LogLevel.Information, Message = "User {ActorId} recorded measurement {MeasurementId} on contract {ContractId} of project {ProjectId}")]
    public partial void MeasurementRecorded(int actorId, int projectId, int contractId, int measurementId);

    [LoggerMessage(EventId = 4032, Level = LogLevel.Information, Message = "User {ActorId} reviewed measurement {MeasurementId} on project {ProjectId}: {NewStatus}; {AllocationCount} allocation change(s)")]
    public partial void MeasurementReviewed(int actorId, int projectId, int measurementId, string newStatus, int allocationCount);

    [LoggerMessage(EventId = 4041, Level = LogLevel.Information, Message = "User {ActorId} recorded manpower sheet {SheetId} on contract {ContractId} of project {ProjectId}")]
    public partial void ManpowerRecorded(int actorId, int projectId, int contractId, int sheetId);

    [LoggerMessage(EventId = 4042, Level = LogLevel.Information, Message = "User {ActorId} reviewed manpower sheet {SheetId} on project {ProjectId}: {NewStatus}; {AllocationCount} allocation change(s)")]
    public partial void ManpowerReviewed(int actorId, int projectId, int sheetId, string newStatus, int allocationCount);

    [LoggerMessage(EventId = 4043, Level = LogLevel.Information, Message = "User {ActorId} linked manpower sheet {SheetId} on project {ProjectId} to project item {ProjectItemId}; previous item {PreviousItemId}")]
    public partial void ManpowerLinked(int actorId, int projectId, int sheetId, int? projectItemId, int? previousItemId);

    [LoggerMessage(EventId = 4051, Level = LogLevel.Information, Message = "User {ActorId} recorded payment {PaymentId} on contract {ContractId} of project {ProjectId}; {AllocationCount} allocation change(s)")]
    public partial void PaymentRecorded(int actorId, int projectId, int contractId, int paymentId, int allocationCount);

    [LoggerMessage(EventId = 4052, Level = LogLevel.Information, Message = "User {ActorId} voided payment {PaymentId} on contract {ContractId} of project {ProjectId}; {AllocationCount} allocation change(s)")]
    public partial void PaymentVoided(int actorId, int projectId, int contractId, int paymentId, int allocationCount);

    [LoggerMessage(EventId = 4091, Level = LogLevel.Warning, Message = "User {ActorId} was refused {Action}: {Reason}")]
    public partial void ActionRefused(int? actorId, string action, string reason);
}
