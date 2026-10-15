using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Features.Accounts;
using AlMotaheda.Modules.Subcontractors.Features.Work;
using static AlMotaheda.Modules.Subcontractors.Tests.Unit.ContractAccountTests;

namespace AlMotaheda.Modules.Subcontractors.Tests.Unit;

/// <summary>The rules of measured work, manpower and cost figures, with no database.</summary>
public sealed class WorkRulesTests
{
    [Fact]
    public void Only_someone_else_reviews_and_only_a_recorded_record()
    {
        const int recorder = 7, reviewer = 8;

        Assert.Equal(ReviewDecision.Allowed, WorkRules.Review(WorkStatus.Recorded, recorder, reviewer));
        Assert.Equal(ReviewDecision.OwnRecord, WorkRules.Review(WorkStatus.Recorded, recorder, recorder));
        Assert.Equal(ReviewDecision.WrongStatus, WorkRules.Review(WorkStatus.Approved, recorder, reviewer));
        Assert.Equal(ReviewDecision.WrongStatus, WorkRules.Review(WorkStatus.Rejected, recorder, reviewer));
        Assert.Equal(ReviewDecision.OwnRecord, WorkRules.Review(WorkStatus.Approved, recorder, recorder));
    }

    [Fact]
    public void A_rejected_sheet_cannot_be_linked()
    {
        Assert.True(WorkRules.CanLink(WorkStatus.Recorded));
        Assert.True(WorkRules.CanLink(WorkStatus.Approved));
        Assert.False(WorkRules.CanLink(WorkStatus.Rejected));
    }

    [Fact]
    public void Over_quantity_is_blocked_with_a_small_tolerance()
    {
        Assert.True(WorkRules.FitsItemQuantity(10m, 6m, 4m));
        Assert.True(WorkRules.FitsItemQuantity(10m, 6m, 4.0005m));
        Assert.False(WorkRules.FitsItemQuantity(10m, 6m, 4.001m));
        Assert.Equal(4m, WorkRules.RemainingQuantity(10m, 6m));
        Assert.Equal(0m, WorkRules.RemainingQuantity(10m, 12m));

        // A Job line closed in three thirds still fits.
        var third = WorkRules.QuantityFromPercent(33.33m, 1m);
        Assert.Equal(0.333m, third);
        Assert.True(WorkRules.FitsItemQuantity(1m, third * 2, WorkRules.QuantityFromPercent(33.34m, 1m)));
    }

    [Fact]
    public void A_percentage_of_a_lump_sum_line_becomes_a_quantity_and_back()
    {
        Assert.Equal(0.05m, WorkRules.QuantityFromPercent(5m, 1m));
        Assert.Equal(1.5m, WorkRules.QuantityFromPercent(50m, 3m));
        Assert.Equal(35m, WorkRules.PercentOfLine(0.35m, 1m));
        Assert.Equal(0m, WorkRules.PercentOfLine(1m, 0m));
    }

    [Fact]
    public void Amounts_are_computed_by_the_server_to_the_piastre()
    {
        Assert.Equal(180_000m, WorkRules.MeasurementAmount(0.05m, 3_600_000m));
        Assert.Equal(41.67m, WorkRules.MeasurementAmount(0.333m, 125.125m));
        Assert.Equal(19_200m, WorkRules.ManpowerAmount(16, 3m, 400m));
        Assert.Equal(1_312.5m, WorkRules.ManpowerAmount(7, 1.5m, 125m));
    }

    [Fact]
    public void The_ceiling_and_the_contract_value_are_never_passed()
    {
        Assert.True(WorkRules.FitsCeiling(200_000m, 150_400m, 49_600m));
        Assert.False(WorkRules.FitsCeiling(200_000m, 150_400m, 49_600.01m));
        Assert.True(WorkRules.FitsContractValue(1_000m, 400m, 600m));
        Assert.False(WorkRules.FitsContractValue(1_000m, 400m, 600.01m));
    }

    [Fact]
    public void The_contract_value_is_never_recognized_cost_and_neither_is_recorded_or_rejected_work()
    {
        var contracts = new[]
        {
            new ContractRow(1, 7, 1_000_000m, 0m, ContractStatus.Active),
            new ContractRow(2, 8, 500_000m, 0m, ContractStatus.Completed),
            new ContractRow(3, 9, 250_000m, 0m, ContractStatus.Pending),
        };
        var work = new[]
        {
            Measurement(300_000, allocated: 100_000, contractId: 1),
            Measurement(80_000, WorkStatus.Recorded, contractId: 1),
            Measurement(40_000, WorkStatus.Rejected, contractId: 1),
            Manpower(120_000, allocated: 120_000, contractId: 2),
            Manpower(5_000, WorkStatus.Recorded, contractId: 2),
        };

        var figures = SubcontractFigures.CostFigures(contracts, work, from: null, to: null);

        Assert.Equal(420_000m, figures.Recognized);
        Assert.Equal(220_000m, figures.Paid);
        Assert.Equal(2, figures.RecognizedRecords);
        Assert.Equal(85_000m, figures.PendingReview);
        Assert.Equal(2, figures.PendingRecords);

        // Commitment: 1,000,000 − 300,000 on the active contract, all 250,000 of the unsigned one; none on the completed one.
        Assert.Equal(950_000m, figures.Committed);
        Assert.Equal(200_000m, figures.Outstanding);
    }

    [Fact]
    public void Cost_figures_respect_the_date_range_but_the_commitment_does_not()
    {
        var contracts = new[] { new ContractRow(1, 7, 1_000m, 0m, ContractStatus.Active) };
        var inside = new WorkRow(WorkKind.Measurement, 1, 1, 7, 1, null, new DateOnly(2026, 3, 10), 100m, WorkStatus.Approved, 0);
        var before = inside with { Id = 2, Date = new DateOnly(2026, 2, 28), Amount = 200m };
        var after = inside with { Id = 3, Date = new DateOnly(2026, 4, 1), Amount = 300m, Status = WorkStatus.Recorded };

        var figures = SubcontractFigures.CostFigures(contracts, [inside, before, after], new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31));

        Assert.Equal(100m, figures.Recognized);
        Assert.Equal(1, figures.RecognizedRecords);
        Assert.Equal(0m, figures.PendingReview);
        Assert.Equal(700m, figures.Committed);
    }

    [Fact]
    public void Linking_moves_approved_work_into_the_item_without_adding_a_record()
    {
        var unlinked = new[]
        {
            new WorkRow(WorkKind.Measurement, 1, 1, 7, 10, null, new DateOnly(2026, 3, 1), 1_000m, WorkStatus.Approved, 400m),
            new WorkRow(WorkKind.Measurement, 2, 1, 7, 10, null, new DateOnly(2026, 3, 2), 500m, WorkStatus.Approved, 0m),
            new WorkRow(WorkKind.Measurement, 3, 1, 7, 10, null, new DateOnly(2026, 3, 3), 900m, WorkStatus.Recorded, 0m),
            new WorkRow(WorkKind.Manpower, 4, 2, 8, null, 5, new DateOnly(2026, 3, 3), 300m, WorkStatus.Approved, 300m),
        };

        var before = SubcontractFigures.ItemTotals(unlinked);
        Assert.Equal(
            [new ItemCostTotal(5, 300m, 300m, 1, 1), new ItemCostTotal(null, 1_500m, 400m, 2, 1)],
            before);

        // Contract item 10 is linked to project item 5: the same rows now carry the link.
        var linked = unlinked.Select(row => row.ContractItemId == 10 ? row with { ProjectItemId = 5 } : row).ToList();
        var after = SubcontractFigures.ItemTotals(linked);

        Assert.Equal([new ItemCostTotal(5, 1_800m, 700m, 3, 2)], after);
        Assert.Equal(before.Sum(total => total.Cost), after.Sum(total => total.Cost));
        Assert.Equal(before.Sum(total => total.RecordCount), after.Sum(total => total.RecordCount));
    }
}
