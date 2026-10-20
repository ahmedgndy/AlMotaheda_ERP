using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.PriceControl;
using AlMotaheda.Modules.Projects.Features.Prices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Projects.Integration;

/// <summary>The Projects module's answer to <see cref="IPriceControl"/>, for the modules that carry prices (Procurement).</summary>
internal sealed class PriceControlContract(
    DbContextOptions<ProjectsDbContext> options,
    ProjectsDbContext db,
    IOptions<ProjectsOptions> settings,
    PriceExceptionCases cases,
    ProjectsLog log) : IPriceControl
{
    public async Task<IReadOnlyDictionary<int, PriceItemRef>> FindItemsAsync(IReadOnlyCollection<int> priceItemIds, CancellationToken cancellationToken)
    {
        if (priceItemIds.Count == 0)
        {
            return new Dictionary<int, PriceItemRef>();
        }

        return await db.PriceItems.AsNoTracking()
            .Where(item => priceItemIds.Contains(item.Id))
            .Select(item => new PriceItemRef(item.Id, item.NameAr, item.NameEn, item.Unit, item.Category.Code, item.Status == PriceItemStatus.Active))
            .ToDictionaryAsync(item => item.Id, cancellationToken);
    }

    public async Task<PriceVerdict> CheckAsync(PriceCheckRequest request, CancellationToken cancellationToken)
    {
        var policy = settings.Value.PriceCheckPolicy;
        var gates = PriceGate.Gates(policy);
        var blocked = policy == PriceCheckPolicy.Block;
        var unit = await db.PriceItems.AsNoTracking().Where(item => item.Id == request.PriceItemId).Select(item => item.Unit).SingleOrDefaultAsync(cancellationToken);
        if (unit is null || !string.Equals(unit, request.Unit, StringComparison.Ordinal))
        {
            return new PriceVerdict(PriceVerdict.UnitMismatch, null, null, null, null, 0, 0, 0, gates, blocked);
        }

        var range = await PriceRanges.ApplicableAsync(db, request.PriceItemId, request.SupplierId, request.Date, cancellationToken);
        var comparison = PriceCheck.Compare(request.UnitPrice, request.Quantity, range?.MinPrice, range?.MaxPrice);
        return new PriceVerdict(
            comparison.Result.ToString(), range?.RangeId, range?.MinPrice, range?.MaxPrice, range?.ReferencePrice, comparison.DifferencePerUnit,
            comparison.TotalDifference, comparison.PercentAbove(range?.MaxPrice), gates, blocked);
    }

    public async Task<int> OpenExceptionAsync(PriceExceptionDraft draft, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        await using var joined = await transaction.JoinAsync(options, ProjectsDatabase.Configure, joinedOptions => new ProjectsDbContext(joinedOptions), cancellationToken);
        var joinedCases = new PriceExceptionCases(joined, new Features.Expenses.CostAlerts(joined, cases.Users), settings, cases.Clock, log);
        var exception = await joinedCases.OpenAsync(draft, cancellationToken);
        await joined.SaveChangesAsync(cancellationToken);
        var told = await joined.PriceExceptionAlerts.CountAsync(alert => alert.ExceptionId == exception.Id, cancellationToken);
        log.PriceExceptionOpened(draft.SubmittedBy, exception.Id, draft.SourceType, draft.SourceId, draft.ProjectId, draft.PriceItemId, told);
        return exception.Id;
    }

    public async Task<IReadOnlyDictionary<int, PriceExceptionState>> StatesAsync(string sourceType, IReadOnlyCollection<int> sourceIds, CancellationToken cancellationToken)
    {
        var newest = await PriceExceptionCases.NewestAsync(db, sourceType, sourceIds, cancellationToken);
        return newest.ToDictionary(pair => pair.Key, pair => new PriceExceptionState(pair.Value.Id, pair.Key, pair.Value.Status.ToString()));
    }

    public async Task<IReadOnlyList<PriceItemListing>> ListItemsAsync(IReadOnlyCollection<string> categoryCodes, DateOnly date, CancellationToken cancellationToken)
    {
        var items = await db.PriceItems.AsNoTracking()
            .Where(item => item.Status == PriceItemStatus.Active && categoryCodes.Contains(item.Category.Code))
            .OrderBy(item => item.Category.SortOrder).ThenBy(item => item.NameAr)
            .Select(item => new PriceItemRef(item.Id, item.NameAr, item.NameEn, item.Unit, item.Category.Code, true))
            .ToListAsync(cancellationToken);

        // A short catalog (office items), so one range query per item is fine.
        var listings = new List<PriceItemListing>(items.Count);
        foreach (var item in items)
        {
            var range = await PriceRanges.ApplicableAsync(db, item.Id, null, date, cancellationToken);
            listings.Add(new PriceItemListing(item, range?.RangeId, range?.MinPrice, range?.MaxPrice, range?.ReferencePrice));
        }

        return listings;
    }

    public async Task<int> WithdrawExceptionsAsync(string sourceType, int sourceId, int userId, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        await using var joined = await transaction.JoinAsync(options, ProjectsDatabase.Configure, joinedOptions => new ProjectsDbContext(joinedOptions), cancellationToken);
        var joinedCases = new PriceExceptionCases(joined, new Features.Expenses.CostAlerts(joined, cases.Users), settings, cases.Clock, log);
        var withdrawn = await joinedCases.WithdrawOpenAsync(sourceType, sourceId, userId, cancellationToken);
        await joined.SaveChangesAsync(cancellationToken);
        return withdrawn;
    }
}
