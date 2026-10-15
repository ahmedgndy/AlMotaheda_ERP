using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Features.Accounts;
using AlMotaheda.Modules.Subcontractors.Features.Companies;
using AlMotaheda.Modules.Subcontractors.Features.Contracts;
using AlMotaheda.Modules.Subcontractors.Features.Payments;
using AlMotaheda.Modules.Subcontractors.Integration;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Tests.Unit;

/// <summary>
/// Every query of the module translates to SQL Server SQL. <c>ToQueryString()</c> opens no connection, so a query
/// EF can't translate fails here without a database. The SQL is also checked for the project filter and for
/// ignoring voided payments.
/// </summary>
public sealed class QueryTranslationTests
{
    private static SubcontractorsDbContext CreateContext() => new SubcontractorsDbContextDesignTimeFactory().CreateDbContext([]);

    [Fact]
    public void Company_search_filters_and_orders_in_sql()
    {
        using var db = CreateContext();

        var sql = SubcontractorQueries.Search(db, "abc", SubcontractorSpecialty.Civil, SubcontractorStatus.Active).ToQueryString();

        Assert.Contains("[sub].[Subcontractor]", sql);
        Assert.Contains("LIKE", sql);
        Assert.Contains("[Specialty] = @", sql);
        Assert.Contains("[Status] = @", sql);
        Assert.Contains("ORDER BY [s].[NameEn]", sql);
    }

    [Fact]
    public void Work_rows_are_read_per_project_and_count_only_payments_that_are_not_voided()
    {
        using var db = CreateContext();

        foreach (var sql in new[]
                 {
                     WorkQueries.Measurements(db, 1).ToQueryString(),
                     WorkQueries.Measurements(db, 1, contractId: 2).ToQueryString(),
                     WorkQueries.ManpowerSheets(db, 1).ToQueryString(),
                     WorkQueries.ManpowerSheets(db, 1, contractId: 2).ToQueryString(),
                 })
        {
            Assert.Contains("[ProjectId] = @", sql);
            Assert.Contains("[sub].[SubPaymentAllocation]", sql);
            Assert.Contains("[VoidedAt] IS NULL", sql);
            Assert.Contains("SUM(", sql);
        }
    }

    [Fact]
    public void Contract_and_payment_rows_are_read_per_project()
    {
        using var db = CreateContext();

        Assert.Contains("[ProjectId] = @", WorkQueries.Contracts(db, 1).ToQueryString());
        Assert.Contains("[ProjectId] = @", WorkQueries.Payments(db, 1).ToQueryString());
        Assert.Contains("[ContractId] = @", WorkQueries.Payments(db, 1, contractId: 3).ToQueryString());
    }

    [Fact]
    public void Contract_screen_queries_check_the_project_of_every_child_record()
    {
        using var db = CreateContext();

        foreach (var sql in new[]
                 {
                     ContractQueries.Rows(db, 1).ToQueryString(),
                     ContractQueries.Rows(db, 1, ContractStatus.Active).ToQueryString(),
                     ContractQueries.One(db, 1, 2).ToQueryString(),
                     ContractQueries.Items(db, 1, 2).ToQueryString(),
                     ContractQueries.Measurements(db, 1, 2).ToQueryString(),
                     ContractQueries.Measurement(db, 1, 9).ToQueryString(),
                     ContractQueries.ManpowerSheets(db, 1, 2).ToQueryString(),
                     ContractQueries.ManpowerSheet(db, 1, 9).ToQueryString(),
                     ContractQueries.Payments(db, 1, 2).ToQueryString(),
                     ContractQueries.Payment(db, 1, 9).ToQueryString(),
                 })
        {
            Assert.Contains("[ProjectId] = @", sql);
        }
    }

    [Fact]
    public void The_idempotency_key_lookup_translates()
    {
        using var db = CreateContext();

        Assert.Contains("[IdempotencyKey] = @", ContractQueries.ByKey(db, Guid.NewGuid()).ToQueryString());
    }

    [Fact]
    public void Settlement_reads_open_payments_and_approved_work_of_one_contract()
    {
        using var db = CreateContext();

        var payments = SettlementQueries.OpenPayments(db, 2).ToQueryString();
        var work = SettlementQueries.ApprovedWork(db, 2).ToQueryString();

        Assert.Contains("[VoidedAt] IS NULL", payments);
        Assert.Contains("[sub].[SubPaymentAllocation]", payments);
        Assert.Contains("UNION ALL", work);
        Assert.Contains("[sub].[ProgressMeasurement]", work);
        Assert.Contains("[sub].[ManpowerSheet]", work);
        Assert.Contains("'Approved'", work);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, 7)]
    public void Item_costs_read_only_approved_work_of_the_project(bool itemFilter, int? itemId)
    {
        using var db = CreateContext();

        var sql = ItemCostQueries.ApprovedWork(db, 1, itemFilter, itemId).ToQueryString();

        Assert.Contains("UNION ALL", sql);
        Assert.Contains("[ProjectId] = @", sql);
        Assert.Contains("'Approved'", sql);
        Assert.Contains("[VoidedAt] IS NULL", sql);
        if (itemFilter)
        {
            Assert.Contains(itemId is null ? "[ProjectItemId] IS NULL" : "[ProjectItemId] = @", sql);
        }
    }
}
