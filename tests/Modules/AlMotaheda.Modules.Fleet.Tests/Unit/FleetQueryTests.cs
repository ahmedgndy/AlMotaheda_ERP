using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features.Assignments;
using AlMotaheda.Modules.Fleet.Features.Costs;
using AlMotaheda.Modules.Fleet.Features.Register;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Tests.Unit;

/// <summary>
/// Every Fleet query translates to SQL Server SQL. <c>ToQueryString()</c> opens no connection, so a query EF can't
/// translate fails here without a database.
/// </summary>
public sealed class FleetQueryTests
{
    private static FleetDbContext CreateContext() => new FleetDbContextDesignTimeFactory().CreateDbContext([]);

    [Fact]
    public void The_register_search_filters_in_sql_with_every_filter()
    {
        using var db = CreateContext();
        var filter = new EquipmentFilter("ex", EquipmentCategory.Heavy, EquipmentOwnership.Rented, EquipmentStatus.InService, Available: true);

        var sql = EquipmentQueries.Search(db, filter).ToQueryString();

        Assert.Contains("[fleet].[Equipment]", sql);
        Assert.Contains("LIKE", sql);
        Assert.Contains("[Category] = @", sql);
        Assert.Contains("[Ownership] = @", sql);
        Assert.Contains("NOT EXISTS", sql);
        Assert.Contains("[fleet].[EquipmentRental]", sql);
        Assert.Contains("ORDER BY [e].[AssetNo]", sql);
    }

    [Fact]
    public void Machines_that_are_not_available_are_found_in_sql_too()
    {
        using var db = CreateContext();

        var sql = EquipmentQueries.Search(db, new EquipmentFilter(null, null, null, null, Available: false)).ToQueryString();

        Assert.Contains("EXISTS", sql);
        Assert.Contains("[EndDate] IS NULL", sql);
    }

    [Fact]
    public void One_machine_and_its_rental_terms_translate()
    {
        using var db = CreateContext();

        Assert.Contains("[fleet].[Equipment]", EquipmentQueries.One(db, 3).ToQueryString());
        Assert.Contains("ORDER BY [e].[StartDate] DESC", EquipmentQueries.Rentals(db, 3).ToQueryString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void The_machines_of_a_project_are_read_for_that_project_only(bool includeEnded)
    {
        using var db = CreateContext();

        var sql = AssignmentQueries.OfProject(db, 1, includeEnded).ToQueryString();

        Assert.Contains("[fleet].[EquipmentAssignment]", sql);
        Assert.Contains("[ProjectId] = @", sql);
        // Once in the sort (current stays first), and once more in the filter when ended stays are left out.
        Assert.Equal(includeEnded ? 1 : 2, sql.Split("[EndDate] IS NULL").Length - 1);
    }

    [Fact]
    public void The_history_of_a_machine_and_one_assignment_translate()
    {
        using var db = CreateContext();

        Assert.Contains("[EquipmentId] = @", AssignmentQueries.OfEquipment(db, 3).ToQueryString());
        Assert.Contains("[ProjectId] = @", AssignmentQueries.One(db, 1, 2).ToQueryString());
    }

    [Fact]
    public void The_cost_list_translates_with_every_filter_and_stays_in_one_project()
    {
        using var db = CreateContext();
        var filter = new EquipmentCostFilter(
            EquipmentCostType.Fuel, EquipmentCostStatus.Approved, EquipmentId: 3, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31), RecordedBy: 7);

        var sql = EquipmentCostQueries.Filtered(db, 1, filter).ToQueryString();

        Assert.Contains("[fleet].[EquipmentCost]", sql);
        Assert.Contains("[ProjectId] = @", sql);
        Assert.Contains("[CreatedBy] = @", sql);
        Assert.Contains("[CostDate] >= @", sql);
        Assert.Contains("[CostDate] <= @", sql);
        Assert.Contains("ORDER BY [e].[CostDate] DESC", sql);
    }

    [Fact]
    public void Settlement_rows_read_one_project_flat_and_limit_the_period_in_sql()
    {
        using var db = CreateContext();

        var all = EquipmentCostQueries.SettlementRows(db, 1).ToQueryString();
        var period = EquipmentCostQueries.SettlementRows(db, 1, new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)).ToQueryString();

        Assert.Contains("[ProjectId] = @", all);
        Assert.DoesNotContain("JOIN", all);
        Assert.DoesNotContain("[CostDate] >=", all);
        Assert.Contains("[CostDate] >= @", period);
        Assert.Contains("[CostDate] <= @", period);
    }

    [Fact]
    public void One_cost_names_and_stays_translate()
    {
        using var db = CreateContext();

        Assert.Contains("[ProjectId] = @", EquipmentCostQueries.One(db, 1, 2).ToQueryString());
        Assert.Contains("[fleet].[Equipment]", EquipmentCostQueries.Names(db, [3, 4]).ToQueryString());
        Assert.Contains("[EquipmentId] = @", EquipmentCostQueries.StaysOn(db, 1, 3).ToQueryString());
    }
}
