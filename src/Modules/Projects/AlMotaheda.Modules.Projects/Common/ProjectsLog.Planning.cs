using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Projects.Common;

/// <summary>
/// Audit events of budgets, the price catalog, daily site reports and report requests (event ids 2601 to 2699).
/// Ids only: never names, notes, prices or amounts.
/// </summary>
internal sealed partial class ProjectsLog
{
    [LoggerMessage(EventId = 2601, Level = LogLevel.Information, Message = "User {ActorId} created draft budget {BudgetId} (version {Version}) on project {ProjectId}; copied from {CopiedFromBudgetId}")]
    public partial void BudgetCreated(int actorId, int projectId, int budgetId, int version, int? copiedFromBudgetId);

    [LoggerMessage(EventId = 2602, Level = LogLevel.Information, Message = "User {ActorId} updated draft budget {BudgetId} on project {ProjectId} ({LineCount} line(s))")]
    public partial void BudgetUpdated(int actorId, int projectId, int budgetId, int lineCount);

    [LoggerMessage(EventId = 2603, Level = LogLevel.Information, Message = "User {ActorId} approved budget {BudgetId} (version {Version}) on project {ProjectId}; superseded budget {SupersededBudgetId}")]
    public partial void BudgetApproved(int actorId, int projectId, int budgetId, int version, int? supersededBudgetId);

    [LoggerMessage(EventId = 2611, Level = LogLevel.Information, Message = "User {ActorId} added catalog item {CatalogItemId} on project {ProjectId} with first price {PriceId}")]
    public partial void CatalogItemCreated(int actorId, int projectId, int catalogItemId, int priceId);

    [LoggerMessage(EventId = 2612, Level = LogLevel.Information, Message = "User {ActorId} updated catalog item {CatalogItemId} on project {ProjectId}; status {OldStatus} -> {NewStatus}")]
    public partial void CatalogItemUpdated(int actorId, int projectId, int catalogItemId, string oldStatus, string newStatus);

    [LoggerMessage(EventId = 2613, Level = LogLevel.Information, Message = "User {ActorId} requested price change {PriceId} for catalog item {CatalogItemId} on project {ProjectId}")]
    public partial void PriceChangeRequested(int actorId, int projectId, int catalogItemId, int priceId);

    [LoggerMessage(EventId = 2614, Level = LogLevel.Information, Message = "User {ActorId} decided price change {PriceId} for catalog item {CatalogItemId} on project {ProjectId}: {NewStatus}")]
    public partial void PriceChangeDecided(int actorId, int projectId, int catalogItemId, int priceId, string newStatus);

    [LoggerMessage(EventId = 2621, Level = LogLevel.Information, Message = "User {ActorId} submitted daily report {ReportId} on project {ProjectId}")]
    public partial void DailyReportSubmitted(int actorId, int projectId, int reportId);

    [LoggerMessage(EventId = 2622, Level = LogLevel.Information, Message = "User {ActorId} resubmitted daily report {ReportId} on project {ProjectId}; was {OldStatus}")]
    public partial void DailyReportResubmitted(int actorId, int projectId, int reportId, string oldStatus);

    [LoggerMessage(EventId = 2623, Level = LogLevel.Information, Message = "User {ActorId} reviewed daily report {ReportId} on project {ProjectId}: {NewStatus}")]
    public partial void DailyReportReviewed(int actorId, int projectId, int reportId, string newStatus);

    [LoggerMessage(EventId = 2631, Level = LogLevel.Information, Message = "User {ActorId} created report request {RequestId} on project {ProjectId} for user {RecipientUserId} (proactive {IsProactive})")]
    public partial void ReportRequestCreated(int actorId, int projectId, int requestId, int recipientUserId, bool isProactive);

    [LoggerMessage(EventId = 2632, Level = LogLevel.Information, Message = "User {ActorId} moved report request {RequestId} on project {ProjectId}: {OldStatus} -> {NewStatus}")]
    public partial void ReportRequestMoved(int actorId, int projectId, int requestId, string oldStatus, string newStatus);
}
