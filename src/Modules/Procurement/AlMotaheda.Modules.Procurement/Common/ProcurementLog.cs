using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Procurement.Common;

/// <summary>Structured events of the Procurement module (event ids 10001 to 10999). Ids only, never amounts.</summary>
internal sealed partial class ProcurementLog(ILogger<ProcurementLog> logger)
{
    private readonly ILogger<ProcurementLog> _logger = logger;

    [LoggerMessage(EventId = 10001, Level = LogLevel.Warning, Message = "User {ActorId} changed the procurement policy: auto-approval {AutoApprove}, price tolerance {Tolerance}%")]
    public partial void PolicyChanged(int actorId, bool autoApprove, decimal tolerance);

    [LoggerMessage(EventId = 10011, Level = LogLevel.Information, Message = "User {ActorId} created requisition {RequisitionId} on project {ProjectId}; auto-approved {AutoApproved}")]
    public partial void RequisitionCreated(int actorId, int projectId, int requisitionId, bool autoApproved);

    [LoggerMessage(EventId = 10012, Level = LogLevel.Information, Message = "User {ActorId} moved requisition {RequisitionId} to {Status}")]
    public partial void RequisitionMoved(int actorId, int requisitionId, string status);

    [LoggerMessage(EventId = 10021, Level = LogLevel.Information, Message = "User {ActorId} moved quotation request {RequestId} to {Status}")]
    public partial void QuotationRequestMoved(int actorId, int requestId, string status);

    [LoggerMessage(EventId = 10031, Level = LogLevel.Information, Message = "User {ActorId} issued purchase order {OrderId} from requisition {RequisitionId}")]
    public partial void OrderIssued(int actorId, int requisitionId, int orderId);

    [LoggerMessage(EventId = 10032, Level = LogLevel.Information, Message = "User {ActorId} revised purchase order {OrderId} (revision {Revision})")]
    public partial void OrderRevised(int actorId, int orderId, int revision);

    [LoggerMessage(EventId = 10033, Level = LogLevel.Information, Message = "User {ActorId} cancelled purchase order {OrderId}")]
    public partial void OrderCancelled(int actorId, int orderId);

    [LoggerMessage(EventId = 10041, Level = LogLevel.Information, Message = "User {ActorId} recorded goods receipt {ReceiptId} on purchase order {OrderId}")]
    public partial void GoodsReceived(int actorId, int orderId, int receiptId);

    [LoggerMessage(EventId = 10051, Level = LogLevel.Information, Message = "User {ActorId} recorded supplier invoice {InvoiceId} on purchase order {OrderId}: {Status}")]
    public partial void InvoiceRecorded(int actorId, int orderId, int invoiceId, string status);

    [LoggerMessage(EventId = 10052, Level = LogLevel.Information, Message = "User {ActorId} resolved supplier invoice {InvoiceId}: {Resolution}")]
    public partial void InvoiceResolved(int actorId, int invoiceId, string resolution);
}
