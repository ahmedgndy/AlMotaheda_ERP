using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;
using AlMotaheda.Modules.Projects.Features.Items;
using AlMotaheda.Modules.Projects.Features.SupplierPayments;
using AlMotaheda.Modules.Projects.Features.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Tests.Unit.Core;

/// <summary>
/// Every query of this part translates to SQL Server SQL. <c>ToQueryString()</c> opens no connection, so a query
/// EF can't translate fails here without a database.
/// </summary>
public sealed class CoreQueryTests
{
    private static ProjectsDbContext CreateContext() => new ProjectsDbContextDesignTimeFactory().CreateDbContext([]);

    [Fact]
    public void Supplier_search_filters_in_sql()
    {
        using var db = CreateContext();

        var sql = SupplierQueries.Search(db, "cem", SupplierStatus.Active, SupplierKind.Materials).ToQueryString();

        Assert.Contains("[projects].[Supplier]", sql);
        Assert.Contains("LIKE", sql);
        Assert.Contains("[Status] = @", sql);
        Assert.Contains("ORDER BY [s].[NameEn]", sql);
    }

    [Fact]
    public void Settlement_rows_sum_only_allocations_of_payments_that_are_not_voided()
    {
        using var db = CreateContext();

        var sql = ExpenseQueries.SettlementRows(db, 1).ToQueryString();
        var some = ExpenseQueries.SettlementRows(db, 1, [1, 2, 3]).ToQueryString();

        Assert.Contains("[projects].[SupplierPaymentAllocation]", sql);
        Assert.Contains("[VoidedAt] IS NULL", sql);
        Assert.Contains("SUM(", sql);
        Assert.Contains("[projects].[SupplierPayment]", some);
    }

    [Theory]
    [InlineData(null, "date")]
    [InlineData(PaymentStates.Paid, "amount")]
    [InlineData(PaymentStates.PartiallyPaid, "status")]
    [InlineData(PaymentStates.Unpaid, "date")]
    public void The_expense_list_translates_with_every_filter_including_the_payment_state(string? paymentState, string sort)
    {
        using var db = CreateContext();
        var filter = new ExpenseFilter(
            ExpenseKind.Supply, ExpenseStatus.Approved, PaymentChannel.Credit, SupplierId: 3, ItemId: null, Unlinked: true,
            paymentState, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), "cement", sort, RecordedBy: 7);

        var sql = ExpenseQueries.Filtered(db, 1, filter).ToQueryString();

        Assert.Contains("[projects].[ProjectExpense]", sql);
        Assert.Contains("[CreatedBy] = @", sql);
        Assert.Contains("[ProjectItemId] IS NULL", sql);
        Assert.Contains("ORDER BY", sql);
        if (paymentState is not null)
        {
            Assert.Contains("[VoidedAt] IS NULL", sql);
        }
    }

    [Fact]
    public void Expense_rows_load_their_payments_in_the_same_query()
    {
        using var db = CreateContext();

        var one = ExpenseQueries.One(db, 1, 2).ToQueryString();
        var rows = ExpenseQueries.Rows(db, 1).ToQueryString();

        Assert.Contains("[projects].[Supplier]", one);
        Assert.Contains("[projects].[ProjectItem]", one);
        Assert.Contains("[projects].[CatalogItem]", one);
        Assert.Contains("[projects].[SupplierPayment]", one);
        Assert.Contains("[projects].[ProjectExpense]", rows);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(5, true)]
    [InlineData(5, false)]
    public void Item_lines_pick_one_item_or_the_not_linked_group(int? itemId, bool approved)
    {
        using var db = CreateContext();

        var sql = ExpenseQueries.ItemLines(db, 1, itemId, approved).ToQueryString();

        Assert.Contains(itemId is null ? "[ProjectItemId] IS NULL" : "[ProjectItemId] = @", sql);
        Assert.Contains(approved ? "'Approved'" : "'Recorded'", sql);
        Assert.Contains("'Supply'", sql);
    }

    [Fact]
    public void Item_queries_translate()
    {
        using var db = CreateContext();

        Assert.Contains("[projects].[ProjectItem]", ItemQueries.Headers(db, 1).ToQueryString());
        Assert.Contains("[Id] = @", ItemQueries.One(db, 1, 2).ToQueryString());
        Assert.Contains("[ProjectItemId] = @", ItemQueries.LinkedExpenses(db, 2).ToQueryString());
    }

    [Fact]
    public void Payment_queries_translate_and_hide_voided_payments_unless_asked()
    {
        using var db = CreateContext();

        var live = PaymentQueries.List(db, 1, supplierId: 4, includeVoided: false).ToQueryString();
        var all = PaymentQueries.List(db, 1, supplierId: null, includeVoided: true).ToQueryString();

        Assert.Contains("[VoidedAt] IS NULL", live);
        Assert.Contains("[SupplierId] = @", live);
        Assert.DoesNotContain("[VoidedAt] IS NULL", all);
        Assert.Contains("[projects].[SupplierPaymentAllocation]", all);
        Assert.Contains("[projects].[SupplierPayment]", PaymentQueries.One(db, 1, 2).ToQueryString());
        Assert.Contains("[IdempotencyKey] = @", PaymentQueries.ByKey(db, Guid.NewGuid()).ToQueryString());
    }
}
