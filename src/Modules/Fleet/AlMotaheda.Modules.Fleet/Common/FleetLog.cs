using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Fleet.Common;

/// <summary>
/// Audit events of the equipment register, assignments and equipment costs (event ids 5001 to 5999). Until the
/// platform AuditLog table exists, these entries are the record of who changed what.
/// </summary>
/// <remarks>Ids and code names only: never names, notes, references or amounts.</remarks>
internal sealed partial class FleetLog(ILogger<FleetLog> logger)
{
    private readonly ILogger<FleetLog> _logger = logger;

    [LoggerMessage(EventId = 5001, Level = LogLevel.Information, Message = "User {ActorId} registered equipment {EquipmentId} ({Ownership})")]
    public partial void EquipmentCreated(int actorId, int equipmentId, string ownership);

    [LoggerMessage(EventId = 5002, Level = LogLevel.Information, Message = "User {ActorId} updated equipment {EquipmentId}: changed {ChangedFields}; status {OldStatus} -> {NewStatus}")]
    public partial void EquipmentUpdated(int actorId, int equipmentId, string changedFields, string oldStatus, string newStatus);

    [LoggerMessage(EventId = 5011, Level = LogLevel.Information, Message = "User {ActorId} set rental terms {RentalId} on equipment {EquipmentId} from supplier {SupplierId}; closed terms {ClosedRentalId}")]
    public partial void RentalTermsSet(int actorId, int equipmentId, int rentalId, int supplierId, int? closedRentalId);

    [LoggerMessage(EventId = 5012, Level = LogLevel.Information, Message = "User {ActorId} closed rental terms {RentalId} on equipment {EquipmentId}")]
    public partial void RentalTermsClosed(int actorId, int equipmentId, int rentalId);

    [LoggerMessage(EventId = 5021, Level = LogLevel.Information, Message = "User {ActorId} assigned equipment {EquipmentId} to project {ProjectId} (assignment {AssignmentId})")]
    public partial void EquipmentAssigned(int actorId, int projectId, int equipmentId, int assignmentId);

    [LoggerMessage(EventId = 5022, Level = LogLevel.Information, Message = "User {ActorId} ended assignment {AssignmentId} of equipment {EquipmentId} on project {ProjectId}")]
    public partial void AssignmentEnded(int actorId, int projectId, int equipmentId, int assignmentId);

    [LoggerMessage(EventId = 5031, Level = LogLevel.Information, Message = "User {ActorId} recorded equipment cost {CostId} ({Type}, {Channel}) for equipment {EquipmentId} on project {ProjectId}")]
    public partial void CostRecorded(int actorId, int projectId, int costId, int equipmentId, string type, string channel);

    [LoggerMessage(EventId = 5032, Level = LogLevel.Information, Message = "User {ActorId} resubmitted equipment cost {CostId} on project {ProjectId}; was {OldStatus}")]
    public partial void CostResubmitted(int actorId, int projectId, int costId, string oldStatus);

    [LoggerMessage(EventId = 5033, Level = LogLevel.Information, Message = "User {ActorId} reviewed equipment cost {CostId} on project {ProjectId}: {NewStatus}")]
    public partial void CostReviewed(int actorId, int projectId, int costId, string newStatus);

    [LoggerMessage(EventId = 5041, Level = LogLevel.Information, Message = "User {ActorId} recorded the payment of equipment cost {CostId} on project {ProjectId} ({Method})")]
    public partial void CostPaid(int actorId, int projectId, int costId, string method);

    [LoggerMessage(EventId = 5042, Level = LogLevel.Information, Message = "User {ActorId} voided the payment of equipment cost {CostId} on project {ProjectId}")]
    public partial void CostPaymentVoided(int actorId, int projectId, int costId);

    [LoggerMessage(EventId = 5043, Level = LogLevel.Information, Message = "User {ActorId} requested payment {RequestId} of equipment cost {CostId} on project {ProjectId}")]
    public partial void CostPaymentRequested(int actorId, int projectId, int costId, int requestId);

    [LoggerMessage(EventId = 5044, Level = LogLevel.Information, Message = "User {ActorId} asked to correct equipment cost {CostId} on project {ProjectId} (adjustment {AdjustmentId})")]
    public partial void CostAdjustmentRequested(int actorId, int projectId, int costId, int adjustmentId);

    [LoggerMessage(EventId = 5045, Level = LogLevel.Information, Message = "User {ActorId} decided adjustment {AdjustmentId} of equipment cost {CostId} on project {ProjectId}: {Outcome}")]
    public partial void CostAdjustmentDecided(int actorId, int projectId, int costId, int adjustmentId, string outcome);

    [LoggerMessage(EventId = 5091, Level = LogLevel.Warning, Message = "User {ActorId} was refused {Action}: {Reason}")]
    public partial void ActionRefused(int? actorId, string action, string reason);
}
