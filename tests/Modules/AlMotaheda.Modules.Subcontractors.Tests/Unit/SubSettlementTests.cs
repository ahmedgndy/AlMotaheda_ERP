using AlMotaheda.Modules.Subcontractors.Features.Accounts;
using AlMotaheda.Modules.Subcontractors.Features.Payments;

namespace AlMotaheda.Modules.Subcontractors.Tests.Unit;

/// <summary>
/// How payments settle work: oldest work first, advances kept and applied later, a void frees its work and other
/// money moves to it, and nothing is ever over-allocated. Concrete numbers, every allocation asserted.
/// </summary>
public sealed class SubSettlementTests
{
    private static readonly DateOnly Jan1 = new(2026, 1, 1);

    private static SettlementPayment Pay(int id, int day, decimal amount) => new(id, Jan1.AddDays(day - 1), amount);

    private static SettlementWork Measured(int id, int day, decimal amount) => new(new WorkKey(WorkKind.Measurement, id), Jan1.AddDays(day - 1), amount);

    private static SettlementWork Crew(int id, int day, decimal amount) => new(new WorkKey(WorkKind.Manpower, id), Jan1.AddDays(day - 1), amount);

    private static SettlementAllocation Line(int paymentId, WorkKind kind, int workId, decimal amount) => new(paymentId, new WorkKey(kind, workId), amount);

    [Fact]
    public void A_payment_settles_the_oldest_work_first_by_date_then_id()
    {
        var lines = SubSettlement.Apply(
            [Pay(1, 10, 1_000m)],
            [Measured(5, 8, 700m), Measured(9, 5, 600m), Measured(3, 8, 300m)],
            []);

        Assert.Equal(
            [Line(1, WorkKind.Measurement, 9, 600m), Line(1, WorkKind.Measurement, 3, 300m), Line(1, WorkKind.Measurement, 5, 100m)],
            lines);
    }

    [Fact]
    public void On_the_same_date_measurements_come_before_manpower()
    {
        var lines = SubSettlement.Apply([Pay(1, 3, 150m)], [Crew(1, 2, 100m), Measured(2, 2, 100m)], []);

        Assert.Equal([Line(1, WorkKind.Measurement, 2, 100m), Line(1, WorkKind.Manpower, 1, 50m)], lines);
    }

    [Fact]
    public void Older_payments_are_applied_first()
    {
        var lines = SubSettlement.Apply([Pay(7, 20, 500m), Pay(4, 2, 300m), Pay(2, 20, 100m)], [Measured(1, 1, 850m)], []);

        Assert.Equal(
            [Line(4, WorkKind.Measurement, 1, 300m), Line(2, WorkKind.Measurement, 1, 100m), Line(7, WorkKind.Measurement, 1, 450m)],
            lines);
    }

    [Fact]
    public void An_advance_stays_unapplied_until_work_is_approved_then_settles_it()
    {
        // Paid 500 before any work: nothing to settle, the whole payment is an unapplied advance.
        Assert.Empty(SubSettlement.Apply([Pay(1, 1, 500m)], [], []));

        // 300 of work approved later takes 300 of the advance.
        var first = SubSettlement.Apply([Pay(1, 1, 500m)], [Measured(1, 5, 300m)], []);
        Assert.Equal([Line(1, WorkKind.Measurement, 1, 300m)], first);

        // Another 400 approved: the 200 still unapplied goes to it, and 200 of that work stays outstanding.
        var second = SubSettlement.Apply([Pay(1, 1, 500m)], [Measured(1, 5, 300m), Crew(2, 6, 400m)], first);
        Assert.Equal([Line(1, WorkKind.Manpower, 2, 200m)], second);

        // Nothing more to do: the advance is used up.
        Assert.Empty(SubSettlement.Apply([Pay(1, 1, 500m)], [Measured(1, 5, 300m), Crew(2, 6, 400m)], [.. first, .. second]));
    }

    [Fact]
    public void A_voided_payment_frees_its_work_and_other_unapplied_money_is_applied_to_it()
    {
        // Payment 1 (1,000) settled the work; payment 2 (500) came later and stayed unapplied.
        var work = new[] { Measured(1, 2, 1_000m) };
        var beforeVoid = SubSettlement.Apply([Pay(1, 3, 1_000m), Pay(2, 4, 500m)], work, []);
        Assert.Equal([Line(1, WorkKind.Measurement, 1, 1_000m)], beforeVoid);

        // Payment 1 is voided: its allocations are left out, and payment 2's 500 moves to the work.
        var afterVoid = SubSettlement.Apply([Pay(2, 4, 500m)], work, []);

        Assert.Equal([Line(2, WorkKind.Measurement, 1, 500m)], afterVoid);
    }

    [Fact]
    public void Re_application_to_work_a_payment_already_touches_adds_to_the_same_payment_and_work()
    {
        // Work of 100: payment 1 gave 50, payment 2 gave 50 and kept 150 unapplied.
        var existing = new[] { Line(1, WorkKind.Measurement, 1, 50m), Line(2, WorkKind.Measurement, 1, 50m) };
        Assert.Empty(SubSettlement.Apply([Pay(1, 1, 50m), Pay(2, 2, 200m)], [Measured(1, 1, 100m)], existing));

        // Payment 1 is voided: payment 2 covers the freed 50. The caller grows its existing row to 100.
        var lines = SubSettlement.Apply([Pay(2, 2, 200m)], [Measured(1, 1, 100m)], [existing[1]]);

        Assert.Equal([Line(2, WorkKind.Measurement, 1, 50m)], lines);
    }

    [Fact]
    public void Work_already_partly_paid_takes_only_what_is_left_on_it()
    {
        var lines = SubSettlement.Apply(
            [Pay(1, 1, 600m), Pay(2, 2, 1_000m)],
            [Measured(1, 1, 600m), Measured(2, 3, 800m)],
            [Line(1, WorkKind.Measurement, 1, 600m), Line(2, WorkKind.Measurement, 2, 300m)]);

        Assert.Equal([Line(2, WorkKind.Measurement, 2, 500m)], lines);
    }

    [Fact]
    public void Nothing_is_ever_over_allocated_and_either_the_money_or_the_open_work_runs_out()
    {
        var random = new Random(20261014);

        for (var round = 0; round < 500; round++)
        {
            var payments = Enumerable.Range(1, random.Next(0, 6))
                .Select(id => Pay(id, random.Next(1, 30), random.Next(1, 100_000) / 100m))
                .ToList();
            var work = Enumerable.Range(1, random.Next(0, 8))
                .Select(id => random.Next(2) == 0
                    ? Measured(id, random.Next(1, 30), random.Next(1, 100_000) / 100m)
                    : Crew(id, random.Next(1, 30), random.Next(1, 100_000) / 100m))
                .ToList();

            var first = SubSettlement.Apply(payments, work, []);

            // Drop one payment (a void) and settle again on top of what is left.
            var kept = payments.Where((_, index) => index % 3 != 0).ToList();
            var keptIds = kept.Select(payment => payment.PaymentId).ToHashSet();
            var remaining = first.Where(line => keptIds.Contains(line.PaymentId)).ToList();
            var all = remaining.Concat(SubSettlement.Apply(kept, work, remaining)).ToList();

            Assert.All(all, line => Assert.True(line.Amount > 0));
            foreach (var payment in kept)
            {
                Assert.True(all.Where(line => line.PaymentId == payment.PaymentId).Sum(line => line.Amount) <= payment.Amount);
            }

            foreach (var piece in work)
            {
                Assert.True(all.Where(line => line.Work == piece.Work).Sum(line => line.Amount) <= piece.Amount);
            }

            var moneyLeft = kept.Sum(payment => payment.Amount) - all.Sum(line => line.Amount);
            var workLeft = work.Sum(piece => piece.Amount) - all.Sum(line => line.Amount);
            Assert.True(moneyLeft == 0 || workLeft == 0, $"Round {round}: {moneyLeft} unapplied while {workLeft} of work is open.");
        }
    }
}
