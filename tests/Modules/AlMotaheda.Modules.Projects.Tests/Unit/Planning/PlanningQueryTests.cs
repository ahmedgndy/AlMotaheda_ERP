using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Budgets;
using AlMotaheda.Modules.Projects.Features.Catalog;
using AlMotaheda.Modules.Projects.Features.DailyReports;
using AlMotaheda.Modules.Projects.Features.ReportRequests;
using AlMotaheda.Modules.Projects.Features.Reports;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Tests.Unit.Planning;

/// <summary>
/// Every query of this part translates to SQL Server SQL and keeps to one project. <c>ToQueryString()</c> opens no
/// connection, so a query EF can't translate fails here without a database.
/// </summary>
public sealed class PlanningQueryTests
{
    private static readonly DateOnly Today = new(2026, 3, 15);

    private static ProjectsDbContext CreateContext() => new ProjectsDbContextDesignTimeFactory().CreateDbContext([]);

    [Fact]
    public void Budget_queries_stay_inside_the_project()
    {
        using var db = CreateContext();

        var headers = BudgetQueries.Headers(db, 1).ToQueryString();
        var one = BudgetQueries.One(db, 1, 2).ToQueryString();
        var approved = BudgetQueries.Approved(db, 1).ToQueryString();
        var amounts = BudgetQueries.Amounts(db, 1, [2, 3]).ToQueryString();
        var lines = BudgetQueries.Lines(db, 1, 2).ToQueryString();

        Assert.Contains("ORDER BY [b].[Version] DESC", headers);
        Assert.All([headers, one, approved], sql => Assert.Contains("[b].[ProjectId] = @", sql));
        Assert.Contains("[b].[Status] = 'Approved'", approved);
        Assert.All([amounts, lines], sql =>
        {
            Assert.Contains("[projects].[BudgetLine]", sql);
            Assert.Contains("[ProjectId] = @", sql);
        });
        Assert.Contains("LEFT JOIN [projects].[ProjectItem]", lines);
    }

    [Theory]
    [InlineData(null, null, null, null)]
    [InlineData("Steel", "Active", CatalogPriceStatuses.PendingChange, "rebar")]
    [InlineData(null, "Inactive", CatalogPriceStatuses.Scheduled, null)]
    [InlineData(null, null, CatalogPriceStatuses.Active, null)]
    public void The_catalog_list_filters_in_sql(string? category, string? status, string? priceStatus, string? search)
    {
        using var db = CreateContext();
        var filter = new CatalogFilter(
            category is null ? null : Enum.Parse<CatalogCategory>(category),
            status is null ? null : Enum.Parse<CatalogItemStatus>(status),
            priceStatus,
            search);

        var sql = CatalogQueries.Filtered(db, 1, filter, Today).ToQueryString();

        Assert.Contains("[projects].[CatalogItem]", sql);
        Assert.Contains("[ProjectId] = @", sql);
        if (search is not null)
        {
            Assert.Contains("LIKE", sql);
            // The text is a parameter: it appears only in the parameter declarations, never in the statement.
            Assert.DoesNotContain(search, sql[sql.IndexOf("SELECT", StringComparison.Ordinal)..]);
        }

        if (priceStatus is not null)
        {
            Assert.Contains("[projects].[ItemPrice]", sql);
        }
    }

    [Fact]
    public void Catalog_detail_queries_translate()
    {
        using var db = CreateContext();

        Assert.Contains("[projects].[CatalogItem]", CatalogQueries.One(db, 1, 2).ToQueryString());
        Assert.Contains("[projects].[ItemPrice]", CatalogQueries.Prices(db, 1, 2).ToQueryString());
        Assert.Contains("[projects].[ItemPrice]", CatalogQueries.Prices(db, 1).ToQueryString());
        Assert.Contains("[projects].[CatalogItem]", CatalogQueries.Estimates(db, 1).ToQueryString());
        Assert.Contains("[projects].[ItemPrice]", CatalogQueries.Changes(db, 1, ItemPriceStatus.Pending).ToQueryString());
        Assert.Contains("[projects].[ItemPrice]", CatalogQueries.Changes(db, 1, null).ToQueryString());

        // Amounts bought count approved supplies only.
        var purchases = CatalogQueries.Purchases(db, 1).ToQueryString();
        Assert.Contains("[projects].[ProjectExpense]", purchases);
        Assert.Contains("'Approved'", purchases);
        Assert.Contains("[projects].[ProjectExpense]", CatalogQueries.Purchases(db, 1, 2).ToQueryString());
        Assert.Contains("TOP(@", CatalogQueries.RecentSupplies(db, 1, 2, 10).ToQueryString());
    }

    [Theory]
    [InlineData(null, false, false, false)]
    [InlineData("Submitted", true, true, true)]
    [InlineData("Returned", false, true, false)]
    public void The_daily_report_list_filters_in_sql(string? status, bool from, bool to, bool author)
    {
        using var db = CreateContext();
        var filter = new DailyReportFilter(
            status is null ? null : Enum.Parse<DailyReportStatus>(status),
            from ? Today.AddDays(-30) : null,
            to ? Today : null,
            author ? 7 : null);

        var sql = DailyReportQueries.Filtered(db, 1, filter).ToQueryString();

        Assert.Contains("[projects].[DailyReport]", sql);
        Assert.Contains("[ProjectId] = @", sql);
        Assert.Equal(status is not null, sql.Contains("[Status] = @", StringComparison.Ordinal) || sql.Contains("[Status] = '", StringComparison.Ordinal));
        Assert.Equal(author, sql.Contains("[AuthorUserId] = @", StringComparison.Ordinal));
    }

    [Fact]
    public void Daily_report_detail_queries_translate_and_show_no_prices()
    {
        using var db = CreateContext();

        Assert.Contains("[projects].[DailyReport]", DailyReportQueries.One(db, 1, 2).ToQueryString());

        // Materials received that day come from the project's supplies, without any price or amount.
        var received = DailyReportQueries.ReceivedOn(db, 1, Today).ToQueryString();
        Assert.Contains("[projects].[ProjectExpense]", received);
        Assert.Contains("[ExpenseDate] = @", received);
        Assert.DoesNotContain("[UnitPrice]", received);
        Assert.DoesNotContain("[Amount]", received);
    }

    [Theory]
    [InlineData(true, false, null)]
    [InlineData(false, false, "Requested")]
    [InlineData(true, true, "Submitted")]
    public void Report_requests_are_limited_to_the_ones_the_viewer_may_see_in_sql(bool seesEvery, bool mine, string? status)
    {
        using var db = CreateContext();
        var viewer = new RequestViewer(7, seesEvery);
        var filter = new ReportRequestFilter(status is null ? null : Enum.Parse<ReportRequestStatus>(status), mine);

        var sql = ReportRequestQueries.Filtered(db, 1, viewer, filter).ToQueryString();
        var one = ReportRequestQueries.One(db, 1, 2, viewer).ToQueryString();

        Assert.Contains("[ProjectId] = @", sql);
        var limited = !seesEvery || mine;
        Assert.Equal(limited, sql.Contains("[RequesterUserId] = @", StringComparison.Ordinal));
        Assert.Equal(!seesEvery, one.Contains("[RecipientUserId] = @", StringComparison.Ordinal));
        Assert.Contains("[projects].[DailyReport]", ReportRequestQueries.LinkedReports(db, 1, 2).ToQueryString());
    }

    [Fact]
    public void Cost_rows_of_a_period_filter_on_the_business_date_and_ignore_voided_payments()
    {
        using var db = CreateContext();

        var all = ReportQueries.SettlementRows(db, 1, null, null).ToQueryString();
        var period = ReportQueries.SettlementRows(db, 1, Today.AddDays(-30), Today).ToQueryString();
        var fromOnly = ReportQueries.SettlementRows(db, 1, Today, null).ToQueryString();

        Assert.DoesNotContain("[ExpenseDate]", all);
        Assert.Contains("[ExpenseDate] >= @", period);
        Assert.Contains("[ExpenseDate] <= @", period);
        Assert.DoesNotContain("[ExpenseDate] <= @", fromOnly);
        Assert.All([all, period], sql =>
        {
            Assert.Contains("[ProjectId] = @", sql);
            Assert.Contains("[VoidedAt] IS NULL", sql);
        });
        Assert.Contains("[projects].[Supplier]", ReportQueries.SupplierNames(db, [1, 2]).ToQueryString());
    }
}
