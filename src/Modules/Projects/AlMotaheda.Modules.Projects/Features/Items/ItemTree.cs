namespace AlMotaheda.Modules.Projects.Features.Items;

/// <summary>
/// A project's items as main items and their sub-items, with no database access. Two levels only: every item is
/// either a main item (no parent) or a sub-item of a main item.
/// </summary>
/// <remarks>
/// A record is linked to exactly one item, so "a main item with its sub-items" is its own figures plus the figures of
/// each sub-item. The project totals are the sum of every item's own figures (<see cref="ItemsLedger"/>): the
/// roll-up is shown next to a main item, never added again into a total.
/// </remarks>
internal sealed class ItemTree
{
    private static readonly IReadOnlyList<int> NoChildren = [];

    private readonly Dictionary<int, IReadOnlyList<int>> _children;

    private ItemTree(IReadOnlyList<ItemHeader> ordered, Dictionary<int, IReadOnlyList<int>> children)
    {
        Ordered = ordered;
        _children = children;
    }

    /// <summary>
    /// Every item in screen order: each main item (by English name) followed by its sub-items (by English name).
    /// A sub-item whose main item is not in the list (it was filtered out by the caller) comes at the end.
    /// </summary>
    public IReadOnlyList<ItemHeader> Ordered { get; }

    public static ItemTree Build(IReadOnlyList<ItemHeader> items)
    {
        var byName = items.OrderBy(item => item.NameEn, StringComparer.OrdinalIgnoreCase).ThenBy(item => item.Id).ToList();
        var ids = byName.Select(item => item.Id).ToHashSet();

        var children = byName
            .Where(item => item.ParentItemId is not null)
            .GroupBy(item => item.ParentItemId!.Value)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<int>)group.Select(item => item.Id).ToList());

        var ordered = new List<ItemHeader>(byName.Count);
        var subItems = byName.Where(item => item.ParentItemId is not null).ToLookup(item => item.ParentItemId!.Value);
        foreach (var main in byName.Where(item => item.ParentItemId is null))
        {
            ordered.Add(main);
            ordered.AddRange(subItems[main.Id]);
        }

        ordered.AddRange(byName.Where(item => item.ParentItemId is { } parent && !ids.Contains(parent)));

        return new ItemTree(ordered, children);
    }

    /// <summary>The sub-items of a main item; empty for a sub-item or a main item without any.</summary>
    public IReadOnlyList<int> SubItemIds(int itemId) => _children.TryGetValue(itemId, out var ids) ? ids : NoChildren;

    /// <summary>
    /// The figures of a main item together with its sub-items; <c>null</c> for a sub-item, which has no roll-up.
    /// </summary>
    public ItemFigures? WithSubItems(ItemHeader item, ItemsLedgerResult ledger) =>
        item.IsMainItem
            ? SubItemIds(item.Id).Aggregate(ledger.Of(item.Id), (sum, child) => sum.Add(ledger.Of(child)))
            : null;

    /// <summary>The item and, for a main item, its sub-items: every item whose records belong to its breakdown.</summary>
    public IReadOnlyList<int> SelfAndSubItems(int itemId) => [itemId, .. SubItemIds(itemId)];
}
