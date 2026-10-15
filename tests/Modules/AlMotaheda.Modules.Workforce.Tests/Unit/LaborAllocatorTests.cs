using AlMotaheda.Modules.Workforce.Features.Sheets;

namespace AlMotaheda.Modules.Workforce.Tests.Unit;

/// <summary>
/// The allocation of labor lines to project items, to the piastre. The requirement: an engineer records 14 workers,
/// 7 did plumbing; plumbing shows exactly those 7, and nobody's day counts twice.
/// </summary>
public sealed class LaborAllocatorTests
{
    private const int Plumbing = 10;
    private const int Concrete = 20;
    private const int Tiling = 30;

    [Fact]
    public void Fourteen_workers_seven_on_plumbing_each_item_gets_its_own_workers_and_the_items_add_up_to_the_sheet()
    {
        var sheet = FourteenWorkers.Lines;

        // Approval: each line's allocations get their amounts.
        var parts = sheet
            .SelectMany(line => LaborAllocator.Split(line.Net, paid: 0m, line.Allocations.Select(allocation => (allocation.ItemId, allocation.Portion, (decimal?)null)).ToList())
                .Select(part => (line.WorkerId, Part: part)))
            .ToList();

        var plumbing = parts.Where(entry => entry.Part.ItemId == Plumbing).ToList();
        Assert.Equal([1, 2, 3, 4, 5, 6, 7], plumbing.Select(entry => entry.WorkerId).Order());
        Assert.All(plumbing, entry => Assert.Equal(1m, entry.Part.Portion));
        Assert.Equal(3_850m, plumbing.Sum(entry => entry.Part.Amount)); // 7 × 550

        var concrete = parts.Where(entry => entry.Part.ItemId == Concrete).ToList();
        Assert.Equal([8, 9, 10, 11, 12, 14], concrete.Select(entry => entry.WorkerId).Order());
        Assert.Equal(1_200m + 300m + 300m + 75m, concrete.Sum(entry => entry.Part.Amount)); // 3 × 400 + 2 × 300 + 300 × 0.25

        var tiling = parts.Where(entry => entry.Part.ItemId == Tiling).ToList();
        Assert.Equal([11, 12, 13, 14], tiling.Select(entry => entry.WorkerId).Order());
        Assert.Equal(300m + 300m + 530m + 225m, tiling.Sum(entry => entry.Part.Amount));

        // Every worker's day is allocated in full, once: nothing left unlinked, no portion above one day.
        Assert.DoesNotContain(parts, entry => entry.Part.ItemId is null);
        Assert.All(sheet, line => Assert.True(LaborAllocator.FitsInOneDay(line.Allocations.Select(allocation => allocation.Portion))));

        // The items add up to the sheet's net, to the piastre.
        Assert.Equal(7_080m, sheet.Sum(line => line.Net));
        Assert.Equal(7_080m, plumbing.Sum(entry => entry.Part.Amount) + concrete.Sum(entry => entry.Part.Amount) + tiling.Sum(entry => entry.Part.Amount));
    }

    [Fact]
    public void Portions_above_one_day_are_refused()
    {
        Assert.False(LaborAllocator.FitsInOneDay([0.6m, 0.5m]));
        Assert.False(LaborAllocator.FitsInOneDay([1m, 0.0001m]));
        Assert.True(LaborAllocator.FitsInOneDay([0.5m, 0.5m]));
        Assert.True(LaborAllocator.FitsInOneDay([1m]));
        Assert.True(LaborAllocator.FitsInOneDay([]));
    }

    [Fact]
    public void A_half_and_half_split_of_an_odd_amount_keeps_the_cents_right()
    {
        var amounts = LaborAllocator.Amounts(300.01m, [0.5m, 0.5m]);

        Assert.Equal([150.01m, 150.00m], amounts);
        Assert.Equal(300.01m, amounts.Sum());
    }

    [Fact]
    public void Thirds_add_up_to_the_net_with_the_last_part_taking_the_rest()
    {
        var amounts = LaborAllocator.Amounts(100m, [0.3333m, 0.3333m, 0.3334m]);

        Assert.Equal([33.33m, 33.33m, 33.34m], amounts);
        Assert.Equal(100m, amounts.Sum());
    }

    [Fact]
    public void A_line_allocated_in_part_leaves_the_rest_not_linked()
    {
        var parts = LaborAllocator.Split(600m, paid: 0m, [(Plumbing, 0.5m, null)]);

        Assert.Equal(2, parts.Count);
        Assert.Equal(new LinePart(Plumbing, 0.5m, 300m, 0m), parts[0]);
        Assert.Equal(new LinePart(null, 0.5m, 300m, 0m), parts[1]);
        Assert.Equal(0.5m, LaborAllocator.UnallocatedPortion([0.5m]));
    }

    [Fact]
    public void A_line_with_no_allocation_is_wholly_not_linked()
    {
        var parts = LaborAllocator.Split(450m, paid: 450m, []);

        Assert.Equal(new LinePart(null, 1m, 450m, 450m), Assert.Single(parts));
    }

    [Fact]
    public void Paid_shares_of_a_split_partly_paid_line_add_up_to_what_was_paid()
    {
        // 1000 split 30 % plumbing, 30 % concrete, 40 % not linked; 333.33 paid on the line.
        var parts = LaborAllocator.Split(1_000m, paid: 333.33m, [(Plumbing, 0.3m, 300m), (Concrete, 0.3m, 300m)]);

        Assert.Equal([300m, 300m, 400m], parts.Select(part => part.Amount));
        Assert.Equal([100m, 100m, 133.33m], parts.Select(part => part.Paid));
        Assert.Equal(333.33m, parts.Sum(part => part.Paid));
    }

    [Fact]
    public void Paid_shares_of_an_odd_half_split_add_up_to_what_was_paid()
    {
        var parts = LaborAllocator.Split(300.01m, paid: 100m, [(Plumbing, 0.5m, 150.01m), (Concrete, 0.5m, 150m)]);

        Assert.Equal([50m, 50m], parts.Select(part => part.Paid));
        Assert.Equal(100m, parts.Sum(part => part.Paid));
    }

    [Fact]
    public void Many_tiny_portions_of_a_tiny_amount_never_go_below_zero_and_still_add_up()
    {
        // Rounding each tenth of 0.05 on its own would give 0.01 nine times and leave -0.04 for the last part.
        var amounts = LaborAllocator.Amounts(0.05m, Enumerable.Repeat(0.1m, 10).ToList());

        Assert.All(amounts, amount => Assert.True(amount >= 0));
        Assert.Equal(0.05m, amounts.Sum());
    }

    [Fact]
    public void A_zero_net_line_has_zero_parts()
    {
        var parts = LaborAllocator.Split(0m, paid: 0m, [(Plumbing, 0.5m, null), (Concrete, 0.5m, null)]);

        Assert.All(parts, part => Assert.Equal(0m, part.Amount));
        Assert.All(parts, part => Assert.Equal(0m, part.Paid));
    }
}

/// <summary>
/// The requirement's example: 14 workers on one day. Workers 1–7 are plumbers on the plumbing item all day; 8–10 on
/// concrete; 11 and 12 split their day half concrete, half tiling; 13 tiles all day; 14 works a quarter on concrete
/// and three quarters on tiling.
/// </summary>
internal static class FourteenWorkers
{
    internal sealed record Line(int LineId, int WorkerId, decimal Days, decimal Rate, decimal Net, IReadOnlyList<PortionForm> Allocations);

    public const int Plumbing = 10;
    public const int Concrete = 20;
    public const int Tiling = 30;

    public static IReadOnlyList<Line> Lines { get; } =
    [
        .. Enumerable.Range(1, 7).Select(worker => new Line(100 + worker, worker, 1m, 550m, 550m, [new PortionForm(Plumbing, 1m)])),
        .. Enumerable.Range(8, 3).Select(worker => new Line(100 + worker, worker, 1m, 400m, 400m, [new PortionForm(Concrete, 1m)])),
        .. Enumerable.Range(11, 2).Select(worker => new Line(100 + worker, worker, 1m, 600m, 600m, [new PortionForm(Concrete, 0.5m), new PortionForm(Tiling, 0.5m)])),
        new Line(113, 13, 1m, 530m, 530m, [new PortionForm(Tiling, 1m)]),
        new Line(114, 14, 1m, 300m, 300m, [new PortionForm(Concrete, 0.25m), new PortionForm(Tiling, 0.75m)]),
    ];
}
