using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Projects.Common;

/// <summary>Audit events of the company price list and the price checks of purchases (event ids 2801 to 2849). Ids only.</summary>
internal sealed partial class ProjectsLog
{
    [LoggerMessage(EventId = 2801, Level = LogLevel.Information, Message = "User {ActorId} added price item {PriceItemId} with first range {RangeId}")]
    public partial void PriceItemCreated(int actorId, int priceItemId, int rangeId);

    [LoggerMessage(EventId = 2802, Level = LogLevel.Information, Message = "User {ActorId} updated price item {PriceItemId}; status {OldStatus} -> {NewStatus}")]
    public partial void PriceItemUpdated(int actorId, int priceItemId, string oldStatus, string newStatus);

    [LoggerMessage(EventId = 2803, Level = LogLevel.Information, Message = "User {ActorId} requested price range {RangeId} for price item {PriceItemId}")]
    public partial void PriceRangeRequested(int actorId, int priceItemId, int rangeId);

    [LoggerMessage(EventId = 2804, Level = LogLevel.Information, Message = "User {ActorId} decided price range {RangeId} of price item {PriceItemId}: {NewStatus}")]
    public partial void PriceRangeDecided(int actorId, int priceItemId, int rangeId, string newStatus);

    [LoggerMessage(EventId = 2811, Level = LogLevel.Warning, Message = "User {ActorId} recorded record {ExpenseId} on project {ProjectId} above its reference range ({Policy}); {RecipientCount} told")]
    public partial void PriceAboveRange(int actorId, int projectId, int expenseId, string policy, int recipientCount);

    [LoggerMessage(EventId = 2812, Level = LogLevel.Information, Message = "User {ActorId} repeated the save of record {ExpenseId} on project {ProjectId}: nothing recorded")]
    public partial void ExpenseRepeated(int actorId, int projectId, int expenseId);
}
