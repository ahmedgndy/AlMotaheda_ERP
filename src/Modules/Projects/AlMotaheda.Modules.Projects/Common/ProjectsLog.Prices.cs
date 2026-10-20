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

/// <summary>Audit events of cash custody (event ids 2851 to 2869). Ids only, never amounts.</summary>
internal sealed partial class ProjectsLog
{
    [LoggerMessage(EventId = 2851, Level = LogLevel.Information, Message = "User {ActorId} asked for custody funding on project {ProjectId} for user {HolderUserId}: payment request {RequestId}")]
    public partial void CustodyFundingRequested(int actorId, int projectId, int holderUserId, int requestId);

    [LoggerMessage(EventId = 2852, Level = LogLevel.Information, Message = "User {ActorId} paid custody entry {EntryId} on project {ProjectId} to user {HolderUserId} (payment request {RequestId})")]
    public partial void CustodyFunded(int actorId, int projectId, int entryId, int holderUserId, int requestId);

    [LoggerMessage(EventId = 2853, Level = LogLevel.Information, Message = "User {ActorId} recorded custody return {EntryId} on project {ProjectId} from user {HolderUserId}")]
    public partial void CustodyReturned(int actorId, int projectId, int entryId, int holderUserId);

    [LoggerMessage(EventId = 2854, Level = LogLevel.Information, Message = "User {ActorId} paid {SourceType} {SourceId} out of the custody of user {HolderUserId} on project {ProjectId} (pending review)")]
    public partial void CustodyOutflowHeld(int actorId, int projectId, int holderUserId, string sourceType, int sourceId);

    [LoggerMessage(EventId = 2855, Level = LogLevel.Information, Message = "Custody entry {EntryId} on project {ProjectId} for {SourceType} {SourceId}: {NewStatus}")]
    public partial void CustodyOutflowMoved(int projectId, int entryId, string sourceType, int sourceId, string newStatus);

    [LoggerMessage(EventId = 2856, Level = LogLevel.Warning, Message = "User {ActorId} paid record {ExpenseId} on project {ProjectId} out of custody beyond its balance (policy Warn)")]
    public partial void CustodyOverdrawn(int actorId, int projectId, int expenseId);
}

/// <summary>Audit events of supply agreements (event ids 2871 to 2879). Ids only.</summary>
internal sealed partial class ProjectsLog
{
    [LoggerMessage(EventId = 2871, Level = LogLevel.Information, Message = "User {ActorId} saved supply agreement {AgreementId} on project {ProjectId}: supplier {SupplierId}, item {ProjectItemId}")]
    public partial void SupplyAgreementSaved(int actorId, int projectId, int agreementId, int supplierId, int projectItemId);

    [LoggerMessage(EventId = 2872, Level = LogLevel.Information, Message = "User {ActorId} closed supply agreement {AgreementId} on project {ProjectId}")]
    public partial void SupplyAgreementClosed(int actorId, int projectId, int agreementId);
}

/// <summary>Audit events of Price Control (event ids 2881 to 2899). Ids only; the amounts are on the records.</summary>
internal sealed partial class ProjectsLog
{
    [LoggerMessage(EventId = 2881, Level = LogLevel.Warning, Message = "Price exception {ExceptionId} opened by user {ActorId} on {SourceType} {SourceId} (project {ProjectId}, price item {PriceItemId}); {RecipientCount} told")]
    public partial void PriceExceptionOpened(int actorId, int exceptionId, string sourceType, int sourceId, int? projectId, int priceItemId, int recipientCount);

    [LoggerMessage(EventId = 2882, Level = LogLevel.Information, Message = "User {ActorId} decided price exception {ExceptionId}: {NewStatus} (new range {NewRangeId})")]
    public partial void PriceExceptionDecided(int actorId, int exceptionId, string newStatus, int? newRangeId);

    [LoggerMessage(EventId = 2883, Level = LogLevel.Information, Message = "Price exception {ExceptionId} withdrawn by user {ActorId}: the transaction changed")]
    public partial void PriceExceptionWithdrawn(int actorId, int exceptionId);

    [LoggerMessage(EventId = 2884, Level = LogLevel.Information, Message = "User {ActorId} saved price category {CategoryId} ({Code})")]
    public partial void PriceCategorySaved(int actorId, int categoryId, string code);

    [LoggerMessage(EventId = 2885, Level = LogLevel.Information, Message = "User {ActorId} saved price unit {UnitId} ({Code})")]
    public partial void PriceUnitSaved(int actorId, int unitId, string code);

    [LoggerMessage(EventId = 2886, Level = LogLevel.Warning, Message = "User {ActorId} was refused on {SourceType} {SourceId}: price exception {ExceptionId} is {Status}")]
    public partial void PriceGateRefused(int actorId, string sourceType, int sourceId, int exceptionId, string status);
}
