using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Features.Assignments;
using AlMotaheda.Modules.Workforce.Features.Ledger;
using AlMotaheda.Modules.Workforce.Features.Payments;
using AlMotaheda.Modules.Workforce.Features.Sheets;
using AlMotaheda.Modules.Workforce.Features.Trades;
using AlMotaheda.Modules.Workforce.Features.Workers;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Tests.Unit;

/// <summary>
/// Every query of the module translates to SQL Server SQL. <c>ToQueryString()</c> opens no connection, so a query EF
/// can't translate fails here without a database.
/// </summary>
public sealed class QueryTranslationTests
{
    private static WorkforceDbContext CreateContext() => new WorkforceDbContextDesignTimeFactory().CreateDbContext([]);

    [Fact]
    public void Trade_list_translates_with_and_without_the_active_filter()
    {
        using var db = CreateContext();

        Assert.Contains("[IsActive] = CAST(1 AS bit)", TradeQueries.List(db, activeOnly: true).ToQueryString());
        Assert.DoesNotContain("[IsActive] =", TradeQueries.List(db, activeOnly: false).ToQueryString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Worker_search_filters_in_sql_and_matches_national_ids_only_when_allowed(bool searchNationalId)
    {
        using var db = CreateContext();
        var filter = new WorkerFilter("ahmed", TradeId: 3, WorkerStatus.Active, ProjectId: 7, searchNationalId);

        var sql = WorkerQueries.Search(db, filter).ToQueryString();

        Assert.Contains("[workforce].[Worker]", sql);
        Assert.Contains("[workforce].[Trade]", sql);
        Assert.Contains("LIKE", sql);
        Assert.Contains("[TradeId] = @", sql);
        Assert.Contains("[Status] = @", sql);
        Assert.Contains("[workforce].[WorkerAssignment]", sql);
        Assert.Contains("[EndDate] IS NULL", sql);
        Assert.Equal(searchNationalId, sql.Contains("[NationalId] LIKE", StringComparison.Ordinal));
        Assert.Contains("[Id] = @", WorkerQueries.One(db, 4).ToQueryString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Assignment_queries_translate(bool includeEnded)
    {
        using var db = CreateContext();

        var project = AssignmentQueries.OfProject(db, 1, includeEnded).ToQueryString();

        Assert.Contains("[ProjectId] = @", project);
        Assert.Equal(!includeEnded, project.Contains("WHERE [w].[ProjectId] = @projectId AND [w].[EndDate] IS NULL", StringComparison.Ordinal));
        Assert.Contains("[WorkerId] = @", AssignmentQueries.OfWorker(db, 2).ToQueryString());
        Assert.Contains("[Id] = @", AssignmentQueries.One(db, 1, 3).ToQueryString());
    }

    [Fact]
    public void The_sheet_list_filters_in_sql()
    {
        using var db = CreateContext();
        var filter = new SheetFilter(SheetStatus.Approved, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31), Shift.Day, RecordedBy: 5);

        var sql = SheetQueries.List(db, 1, filter).ToQueryString();

        Assert.Contains("[workforce].[DailySheet]", sql);
        Assert.Contains("[ProjectId] = @", sql);
        Assert.Contains("[Status] = @", sql);
        Assert.Contains("[Shift] = @", sql);
        Assert.Contains("[RecordedBy] = @", sql);
        Assert.Contains("[WorkDate] >= @", sql);
        Assert.Contains("ORDER BY [d].[WorkDate] DESC", sql);
    }

    [Fact]
    public void One_sheet_loads_its_lines_rates_payments_adjustments_and_allocations_in_one_query()
    {
        using var db = CreateContext();

        var sql = SheetQueries.One(db, 1, 2).ToQueryString();

        Assert.Contains("[workforce].[AttendanceLine]", sql);
        Assert.Contains("[workforce].[WorkerAssignment]", sql);
        Assert.Contains("[workforce].[WorkerPaymentAllocation]", sql);
        Assert.Contains("[VoidedAt] IS NULL", sql);
        Assert.Contains("[workforce].[LineAdjustment]", sql);
        Assert.Contains("[workforce].[LaborAllocation]", sql);
        Assert.Contains("[SheetId] = @", SheetQueries.RateRows(db, 2).ToQueryString());
        Assert.Contains("[workforce].[DailySheet]", SheetQueries.LineOfProject(db, 1, 3).ToQueryString());
    }

    [Fact]
    public void Ledger_queries_read_only_approved_non_void_lines_of_the_project()
    {
        using var db = CreateContext();
        var from = new DateOnly(2026, 1, 1);
        var to = new DateOnly(2026, 1, 31);

        var lines = LaborQueries.LedgerLines(db, 1, from, to).ToQueryString();
        var allocations = LaborQueries.LedgerAllocations(db, 1, null, null).ToQueryString();
        var deductions = LaborQueries.LedgerDeductions(db, 1).ToQueryString();

        foreach (var sql in new[] { lines, allocations, deductions })
        {
            Assert.Contains("[Status] = 'Approved'", sql);
            Assert.Contains("[IsVoid] = CAST(0 AS bit)", sql);
            Assert.Contains("[ProjectId] = @", sql);
        }

        Assert.Contains("[WorkDate] >= @", lines);
        Assert.Contains("[VoidedAt] IS NULL", lines);
        Assert.Contains("[Kind] = 'Deduction'", deductions);
    }

    [Fact]
    public void Pending_and_link_queries_translate()
    {
        using var db = CreateContext();

        var pending = LaborQueries.PendingLines(db, 1, null, new DateOnly(2026, 2, 1)).ToQueryString();

        // Only submitted sheets wait for a reviewer; a returned sheet is back with its recorder.
        Assert.Contains("'Submitted'", pending);
        Assert.DoesNotContain("'Returned'", pending);
        Assert.Contains("[workforce].[LineAdjustment]", pending);
        Assert.Contains("[ProjectItemId] = @", LaborQueries.LinksToItem(db, 1, 5).ToQueryString());
        Assert.Contains("GROUP BY", LaborQueries.SheetCounts(db, 1).ToQueryString());
        Assert.Contains("[EndDate] IS NULL", LaborQueries.OpenAssignments(db, 1).ToQueryString());
    }

    [Fact]
    public void Payment_queries_translate_and_hide_voided_payments_unless_asked()
    {
        using var db = CreateContext();

        var live = WorkerPaymentQueries.OfWorker(db, 1, includeVoided: false).ToQueryString();
        var all = WorkerPaymentQueries.OfWorker(db, 1, includeVoided: true).ToQueryString();

        Assert.Contains("[VoidedAt] IS NULL", live);
        Assert.DoesNotContain("[VoidedAt] IS NULL", all);
        Assert.Contains("[workforce].[WorkerPaymentAllocation]", all);
        Assert.Contains("[Id] = @", WorkerPaymentQueries.One(db, 2).ToQueryString());
        Assert.Contains("[IdempotencyKey] = @", WorkerPaymentQueries.ByKey(db, Guid.NewGuid()).ToQueryString());

        var payable = WorkerPaymentQueries.PayableLines(db, 1).ToQueryString();
        Assert.Contains("[Status] = 'Approved'", payable);
        Assert.Contains("[IsVoid] = CAST(0 AS bit)", payable);
        Assert.Contains("[NetAmount] IS NOT NULL", payable);
        Assert.Contains("[VoidedAt]", WorkerPaymentQueries.PaidParts(db, 1).ToQueryString());
    }

    [Fact]
    public void Worker_screen_queries_limit_payments_to_the_visible_projects_in_sql()
    {
        using var db = CreateContext();

        var visibleOnly = WorkerPaymentQueries.OfWorker(db, 1, includeVoided: false, projectIds: [4, 7]).ToQueryString();
        var everything = WorkerPaymentQueries.OfWorker(db, 1, includeVoided: false).ToQueryString();
        var projects = WorkerPaymentQueries.PaidProjectIds(db, 1).ToQueryString();
        var pending = WorkerPaymentQueries.PendingLinesByProject(db, 1).ToQueryString();

        Assert.Contains("EXISTS", visibleOnly);
        Assert.DoesNotContain("EXISTS", everything);
        Assert.Contains("DISTINCT", projects);
        Assert.Contains("[ProjectId]", projects);
        Assert.Contains("GROUP BY", pending);
        Assert.Contains("'Submitted'", pending);
        Assert.Contains("'Returned'", pending);
    }

    [Fact]
    public void Sheet_reference_queries_translate()
    {
        using var db = CreateContext();

        var sql = SheetReferences.AssignedOnQuery(db, 1, new DateOnly(2026, 1, 15), [1, 2, 3]).ToQueryString();

        Assert.Contains("[StartDate] <= @", sql);
        Assert.Contains("[EndDate] IS NULL OR", sql);
    }
}
