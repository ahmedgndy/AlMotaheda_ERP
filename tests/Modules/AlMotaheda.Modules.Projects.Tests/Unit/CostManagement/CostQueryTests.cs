using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;
using AlMotaheda.Modules.Projects.Features.Prices;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Tests.Unit.CostManagement;

/// <summary>Every query of cost management translates to SQL Server SQL (no database needed).</summary>
public sealed class CostQueryTests
{
    private static ProjectsDbContext CreateContext() => new ProjectsDbContextDesignTimeFactory().CreateDbContext([]);

    [Fact]
    public void The_price_list_and_its_ranges_translate()
    {
        using var db = CreateContext();

        var items = PriceQueries.Items(db, db.PriceItems.Where(item => item.NameEn.Contains("cem")), new DateOnly(2026, 1, 10)).ToQueryString();
        var ranges = PriceQueries.Ranges(db.PriceRanges.Where(range => range.Status == PriceRangeStatus.Pending)).ToQueryString();
        var applicable = PriceRanges.ApprovedOn(db.PriceRanges.Where(range => range.PriceItemId == 1), 4, new DateOnly(2026, 1, 10)).ToQueryString();

        Assert.Contains("[projects].[PriceItem]", items);
        Assert.Contains("[projects].[PriceRange]", items);
        Assert.Contains("[projects].[Supplier]", ranges);
        Assert.Contains("[EffectiveFrom] <= @", applicable);
        Assert.Contains("ORDER BY [p].[EffectiveFrom] DESC", applicable);
    }

    [Fact]
    public void The_lines_of_a_main_item_and_its_sub_items_are_one_query_with_the_price_check()
    {
        using var db = CreateContext();

        var sql = ExpenseQueries.LinesOfItems(db, 1, [10, 11, 12], approved: true).ToQueryString();

        Assert.Contains("[ProjectItemId] IN", sql.Replace("\n", " ", StringComparison.Ordinal));
        Assert.Contains("[ReferenceMaxPrice]", sql);
        Assert.Contains("[projects].[PriceItem]", sql);
    }
}
