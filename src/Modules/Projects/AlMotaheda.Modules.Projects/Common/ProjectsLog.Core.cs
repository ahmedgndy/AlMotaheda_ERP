using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Projects.Common;

/// <summary>
/// Audit events of suppliers, project items, expenses and supplier payments (event ids 2401 to 2599).
/// Ids only: never names, notes or amounts.
/// </summary>
internal sealed partial class ProjectsLog
{
    [LoggerMessage(EventId = 2401, Level = LogLevel.Information, Message = "User {ActorId} created supplier {SupplierId}")]
    public partial void SupplierCreated(int actorId, int supplierId);

    [LoggerMessage(EventId = 2402, Level = LogLevel.Information, Message = "User {ActorId} updated supplier {SupplierId}; status {OldStatus} -> {NewStatus}")]
    public partial void SupplierUpdated(int actorId, int supplierId, string oldStatus, string newStatus);

    [LoggerMessage(EventId = 2411, Level = LogLevel.Information, Message = "User {ActorId} created item {ItemId} on project {ProjectId}")]
    public partial void ItemCreated(int actorId, int projectId, int itemId);

    [LoggerMessage(EventId = 2412, Level = LogLevel.Information, Message = "User {ActorId} updated item {ItemId} on project {ProjectId}")]
    public partial void ItemUpdated(int actorId, int projectId, int itemId);

    [LoggerMessage(EventId = 2413, Level = LogLevel.Information, Message = "User {ActorId} set item {ItemId} on project {ProjectId} to {Status}")]
    public partial void ItemStatusChanged(int actorId, int projectId, int itemId, string status);

    [LoggerMessage(EventId = 2414, Level = LogLevel.Information, Message = "User {ActorId} deleted item {ItemId} on project {ProjectId}")]
    public partial void ItemDeleted(int actorId, int projectId, int itemId);

    [LoggerMessage(EventId = 2415, Level = LogLevel.Information, Message = "User {ActorId} moved item {ItemId} on project {ProjectId} under {ParentItemId}; was under {PreviousParentItemId}")]
    public partial void ItemMoved(int actorId, int projectId, int itemId, int? parentItemId, int? previousParentItemId);

    [LoggerMessage(EventId = 2421, Level = LogLevel.Information, Message = "User {ActorId} recorded {Kind} {ExpenseId} on project {ProjectId} ({Channel})")]
    public partial void ExpenseRecorded(int actorId, int projectId, int expenseId, string kind, string channel);

    [LoggerMessage(EventId = 2422, Level = LogLevel.Information, Message = "User {ActorId} resubmitted record {ExpenseId} on project {ProjectId}; was {OldStatus}")]
    public partial void ExpenseResubmitted(int actorId, int projectId, int expenseId, string oldStatus);

    [LoggerMessage(EventId = 2423, Level = LogLevel.Information, Message = "User {ActorId} reviewed record {ExpenseId} on project {ProjectId}: {NewStatus}")]
    public partial void ExpenseReviewed(int actorId, int projectId, int expenseId, string newStatus);

    [LoggerMessage(EventId = 2424, Level = LogLevel.Information, Message = "User {ActorId} linked record {ExpenseId} on project {ProjectId} to item {ItemId}; previous item {PreviousItemId}")]
    public partial void ExpenseLinked(int actorId, int projectId, int expenseId, int? itemId, int? previousItemId);

    [LoggerMessage(EventId = 2431, Level = LogLevel.Information, Message = "User {ActorId} recorded supplier payment {PaymentId} to supplier {SupplierId} on project {ProjectId} with {AllocationCount} allocation(s)")]
    public partial void SupplierPaymentRecorded(int actorId, int projectId, int paymentId, int supplierId, int allocationCount);

    [LoggerMessage(EventId = 2432, Level = LogLevel.Information, Message = "User {ActorId} voided supplier payment {PaymentId} on project {ProjectId}")]
    public partial void SupplierPaymentVoided(int actorId, int projectId, int paymentId);

    [LoggerMessage(EventId = 2433, Level = LogLevel.Information, Message = "User {ActorId} requested payment {RequestId} to supplier {SupplierId} on project {ProjectId} with {AllocationCount} allocation(s)")]
    public partial void SupplierPaymentRequested(int actorId, int projectId, int requestId, int supplierId, int allocationCount);

    [LoggerMessage(EventId = 2434, Level = LogLevel.Information, Message = "User {ActorId} asked to correct record {ExpenseId} on project {ProjectId} (adjustment {AdjustmentId})")]
    public partial void ExpenseAdjustmentRequested(int actorId, int projectId, int expenseId, int adjustmentId);

    [LoggerMessage(EventId = 2435, Level = LogLevel.Information, Message = "User {ActorId} decided adjustment {AdjustmentId} of record {ExpenseId} on project {ProjectId}: {Outcome}")]
    public partial void ExpenseAdjustmentDecided(int actorId, int projectId, int expenseId, int adjustmentId, string outcome);

    [LoggerMessage(EventId = 2436, Level = LogLevel.Information, Message = "User {ActorId} posted {LineCount} supplier invoice lines as records on project {ProjectId}")]
    public partial void SupplierInvoicePosted(int actorId, int projectId, int lineCount);
}
