using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Catalog;

namespace AlMotaheda.Modules.Projects.Tests.Unit.Planning;

/// <summary>Where a catalog price stands on a date, and what an item's estimate and purchases add up to.</summary>
public sealed class CatalogPricingTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);

    private static PriceRow Price(int id, decimal price, string from, ItemPriceStatus status, decimal? previous = null) =>
        new(id, 1, price, previous, DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), status,
            status == ItemPriceStatus.Approved && previous is null ? null : "Supplier raised prices",
            new DateTime(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc), 7, null, null, null);

    private static readonly PriceRow[] History =
    [
        Price(1, 100m, "2026-01-01", ItemPriceStatus.Approved),
        Price(2, 110m, "2026-03-01", ItemPriceStatus.Approved, previous: 100m),
        Price(3, 150m, "2026-04-01", ItemPriceStatus.Rejected, previous: 110m),
        Price(4, 120m, "2026-12-01", ItemPriceStatus.Approved, previous: 110m),
        Price(5, 125m, "2026-11-01", ItemPriceStatus.Pending, previous: 110m),
    ];

    [Fact]
    public void The_current_price_is_the_approved_one_with_the_latest_start_on_or_before_today()
    {
        var position = CatalogPricing.Position(History, Today);

        Assert.Equal(2, position.Current!.Id);
        Assert.Equal(110m, position.Current.Price);
        Assert.Equal(1, position.Previous!.Id);
        Assert.Equal(10.00m, position.ChangePercent);
        Assert.Equal(5, position.Pending!.Id);
        Assert.Equal(4, position.Scheduled!.Id);
        Assert.Equal(CatalogPriceStatuses.PendingChange, position.PriceStatus);
    }

    [Fact]
    public void A_future_price_becomes_current_by_itself_on_its_date()
    {
        var position = CatalogPricing.Position(History, new DateOnly(2026, 12, 1));

        Assert.Equal(120m, position.Current!.Price);
        Assert.Equal(110m, position.Previous!.Price);
        Assert.Equal(9.09m, position.ChangePercent);  // 9.0909…
        Assert.Null(position.Scheduled);
    }

    [Fact]
    public void Without_a_pending_request_a_planned_price_makes_the_item_scheduled_and_otherwise_active()
    {
        var noPending = History.Where(row => row.Status != ItemPriceStatus.Pending).ToList();
        var nothingPlanned = noPending.Where(row => row.Id != 4).ToList();

        Assert.Equal(CatalogPriceStatuses.Scheduled, CatalogPricing.Position(noPending, Today).PriceStatus);
        Assert.Equal(CatalogPriceStatuses.Active, CatalogPricing.Position(nothingPlanned, Today).PriceStatus);
    }

    [Fact]
    public void An_item_whose_only_price_starts_later_has_no_current_price_yet()
    {
        var position = CatalogPricing.Position([Price(9, 80m, "2026-11-01", ItemPriceStatus.Approved)], Today);

        Assert.Null(position.Current);
        Assert.Null(position.Previous);
        Assert.Null(position.ChangePercent);
        Assert.Equal(80m, position.Scheduled!.Price);
        Assert.Equal(CatalogPriceStatuses.Scheduled, position.PriceStatus);
        Assert.Null(CatalogPricing.EstimatedValue(500m, position.Current?.Price));
    }

    [Theory]
    [InlineData("1234.567", "110.05", "135864.10")]  // 135,864.09835
    [InlineData("0", "110.05", "0.00")]
    [InlineData("2.5", "0.03", "0.08")]              // 0.075
    public void The_estimated_value_is_quantity_times_current_price_to_the_piastre(string quantity, string price, string expected)
    {
        var value = CatalogPricing.EstimatedValue(Parse(quantity), Parse(price));

        Assert.Equal(Parse(expected), value);
    }

    [Theory]
    [InlineData("3", "1", "-66.67")]
    [InlineData("100", "112.5", "12.50")]
    [InlineData("80", "80", "0.00")]
    public void The_change_percent_is_rounded_to_two_decimals(string oldPrice, string newPrice, string expected)
    {
        Assert.Equal(Parse(expected), CatalogPricing.ChangePercent(Parse(oldPrice), Parse(newPrice)));
    }

    [Fact]
    public void A_first_price_has_no_change_percent()
    {
        Assert.Null(CatalogPricing.ChangePercent(null, 100m));
    }

    [Fact]
    public void Purchases_add_every_amount_but_only_quantities_in_the_items_unit()
    {
        PurchaseRow[] purchases =
        [
            new(1, "Bag", 10.5m, 1_155m),
            new(1, "Ton", 2m, 5_000m),
            new(1, "Bag", 4.25m, 488.75m),
        ];

        var totals = CatalogPricing.Purchases(purchases, "Bag");

        Assert.Equal(14.75m, totals.Quantity);
        Assert.Equal(6_643.75m, totals.Amount);
        Assert.Equal(3, totals.Count);
    }

    [Fact]
    public void The_summary_adds_the_estimates_at_todays_prices_and_hides_money_without_see_money()
    {
        CatalogItemEstimate[] items = [new(1, "Bag", 1_000m), new(2, "Ton", 20m)];
        PriceRow[] prices =
        [
            .. History,
            new(20, 2, 18_500m, null, new DateOnly(2026, 1, 1), ItemPriceStatus.Approved, null, DateTime.UnixEpoch, 7, DateTime.UnixEpoch, 7, null),
        ];
        PurchaseRow[] purchases = [new(1, "Bag", 10m, 1_100m), new(2, "Ton", 1.5m, 27_750m)];

        var withMoney = CatalogSummary.Compute(items, prices, purchases, Today, seesMoney: true);
        var withoutMoney = CatalogSummary.Compute(items, prices, purchases, Today, seesMoney: false);

        Assert.Equal(2, withMoney.ItemCount);
        Assert.Equal(480_000m, withMoney.EstimatedValue);   // 1,000 × 110 + 20 × 18,500
        Assert.Equal(28_850m, withMoney.AmountBought);
        Assert.Equal(1, withMoney.PendingChangeCount);
        Assert.Null(withoutMoney.EstimatedValue);
        Assert.Null(withoutMoney.AmountBought);
        Assert.Equal(1, withoutMoney.PendingChangeCount);
    }

    private static decimal Parse(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
}
