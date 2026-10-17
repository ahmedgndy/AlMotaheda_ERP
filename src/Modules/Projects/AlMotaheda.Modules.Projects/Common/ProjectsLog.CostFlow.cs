using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Projects.Common;

/// <summary>Log messages of the cost-flow features (docs/cost-flow). Event ids 2800–2899. No amounts, no names.</summary>
internal sealed partial class ProjectsLog
{
    [LoggerMessage(EventId = 2801, Level = LogLevel.Information, Message = "User {ActorId} saved master catalog item {MasterItemId}: {Change}")]
    public partial void MasterItemSaved(int actorId, int masterItemId, string change);

    [LoggerMessage(EventId = 2802, Level = LogLevel.Information, Message = "User {ActorId} requested master price change {PriceId} for master item {MasterItemId}")]
    public partial void MasterPriceChangeRequested(int actorId, int masterItemId, int priceId);

    [LoggerMessage(EventId = 2803, Level = LogLevel.Information, Message = "User {ActorId} decided master price change {PriceId} for master item {MasterItemId}: {NewStatus}")]
    public partial void MasterPriceChangeDecided(int actorId, int masterItemId, int priceId, string newStatus);

    [LoggerMessage(EventId = 2804, Level = LogLevel.Information, Message = "User {ActorId} selected {CatalogCount} catalog items and {ItemCount} project items from the master catalog on project {ProjectId}")]
    public partial void MasterItemsSelected(int actorId, int projectId, int catalogCount, int itemCount);

    [LoggerMessage(EventId = 2811, Level = LogLevel.Information, Message = "Price variance {VarianceId} raised on expense {ExpenseId} of project {ProjectId} ({Direction})")]
    public partial void PriceVarianceRaised(int projectId, int expenseId, int varianceId, string direction);

    [LoggerMessage(EventId = 2812, Level = LogLevel.Information, Message = "User {ActorId} decided price variance {VarianceId} on project {ProjectId}: {NewStatus}")]
    public partial void PriceVarianceDecided(int actorId, int projectId, int varianceId, string newStatus);

    [LoggerMessage(EventId = 2813, Level = LogLevel.Information, Message = "User {ActorId} changed the pricing policy")]
    public partial void PricingPolicyChanged(int actorId);

    [LoggerMessage(EventId = 2821, Level = LogLevel.Information, Message = "User {ActorId} set {Count} components on item {ItemId} of project {ProjectId}")]
    public partial void ItemComponentsSet(int actorId, int projectId, int itemId, int count);
}
