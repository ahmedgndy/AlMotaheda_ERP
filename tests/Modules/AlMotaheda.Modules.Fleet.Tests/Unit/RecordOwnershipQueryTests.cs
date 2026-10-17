using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features.Costs;
using AlMotaheda.Modules.Fleet.Integration;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Tests.Unit;

/// <summary>
/// "Is this record on this project?" for Fleet records, and the voided-payment history, translate to SQL and always
/// filter by the project (or the cost). No database needed.
/// </summary>
public sealed class RecordOwnershipQueryTests
{
    private static FleetDbContext CreateContext() => new FleetDbContextDesignTimeFactory().CreateDbContext([]);

    [Fact]
    public void A_machine_is_on_a_project_when_it_has_ever_been_assigned_there()
    {
        using var db = CreateContext();

        var sql = FleetRecordOwnership.Query(db, 3, ProjectRecordKinds.Equipment, [5, 6])!.ToQueryString();

        Assert.Contains("[fleet].[EquipmentAssignment]", sql);
        Assert.Contains("[ProjectId] = @", sql);
        Assert.Contains("DISTINCT", sql);
        Assert.DoesNotContain("[EndDate]", sql);
    }

    [Fact]
    public void An_equipment_cost_is_on_its_own_project_only()
    {
        using var db = CreateContext();

        var sql = FleetRecordOwnership.Query(db, 3, ProjectRecordKinds.EquipmentCost, [5])!.ToQueryString();

        Assert.Contains("[fleet].[EquipmentCost]", sql);
        Assert.Contains("[ProjectId] = @", sql);
    }

    [Fact]
    public void Kinds_of_other_modules_are_not_answered_here()
    {
        using var db = CreateContext();

        Assert.Null(FleetRecordOwnership.Query(db, 3, ProjectRecordKinds.Expense, [5]));
        Assert.Equal([ProjectRecordKinds.Equipment, ProjectRecordKinds.EquipmentCost, ProjectRecordKinds.RepairRequest], new FleetRecordOwnership(db).Kinds);
    }

    [Fact]
    public void The_voided_payments_of_a_cost_are_read_newest_first()
    {
        using var db = CreateContext();

        var sql = VoidedEquipmentPaymentQueries.ForCost(db, 9).ToQueryString();

        Assert.Contains("[fleet].[VoidedEquipmentCostPayment]", sql);
        Assert.Contains("[CostId] = @", sql);
        Assert.Contains("ORDER BY", sql);
        Assert.Contains("DESC", sql);
    }
}
