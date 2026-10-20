using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Features.Payments;
using AlMotaheda.Modules.Workforce.Features.Settlements;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Tests.Unit;

/// <summary>
/// The weekly settlement's arithmetic (formula E: approved labor less what is paid and held), with no database, and
/// its queries translated to SQL Server SQL.
/// </summary>
public sealed class SettlementPlanTests
{
    private static (int WorkerId, PayableLine Line) Day(int workerId, int lineId, int day, decimal net) =>
        (workerId, new PayableLine(lineId, 1, new DateOnly(2026, 1, day), 1m, net));

    [Fact]
    public void A_workers_outstanding_is_approved_less_paid_less_held_and_voided_payments_count_nowhere()
    {
        var payables = SettlementPlan.Payables(
            [Day(1, 10, 1, 400m), Day(1, 11, 2, 400m), Day(2, 20, 1, 400m)],
            [new PaidPart(10, 300m, PaymentVoided: false), new PaidPart(20, 400m, PaymentVoided: true)],
            [new PaidPart(11, 200m, PaymentVoided: false)]);

        var first = payables.Single(payable => payable.WorkerId == 1);
        Assert.Equal((800m, 300m, 200m, 300m), (first.Approved, first.Paid, first.Held, first.Outstanding));
        var second = payables.Single(payable => payable.WorkerId == 2);
        Assert.Equal((400m, 0m, 400m), (second.Approved, second.Paid, second.Outstanding));
    }

    [Fact]
    public void Without_amounts_a_settlement_pays_everything_outstanding_oldest_day_first()
    {
        var payables = SettlementPlan.Payables([Day(1, 11, 2, 400m), Day(1, 10, 1, 400m), Day(2, 20, 1, 400m)], [], []);

        var plan = SettlementPlan.Plan(payables, asked: null);

        Assert.Null(plan.TooMuchFor);
        Assert.Equal(1_200m, plan.Amount);
        Assert.Equal([10, 11], plan.Lines.Single(line => line.WorkerId == 1).Allocations.Select(allocation => allocation.LineId));
    }

    [Fact]
    public void An_amount_fills_the_oldest_day_first_and_more_than_owed_is_refused()
    {
        var payables = SettlementPlan.Payables([Day(1, 10, 1, 400m), Day(1, 11, 2, 400m)], [], []);

        var part = SettlementPlan.Plan(payables, new Dictionary<int, decimal> { [1] = 500m });
        Assert.Equal([(10, 400m), (11, 100m)], part.Lines.Single().Allocations.Select(allocation => (allocation.LineId, allocation.Amount)));

        var tooMuch = SettlementPlan.Plan(payables, new Dictionary<int, decimal> { [1] = 801m });
        Assert.Equal((1, 800m), (tooMuch.TooMuchFor, tooMuch.OutstandingOfThatWorker));
        Assert.Empty(tooMuch.Lines);

        var stranger = SettlementPlan.Plan(payables, new Dictionary<int, decimal> { [9] = 1m });
        Assert.Equal(9, stranger.TooMuchFor);
    }

    [Fact]
    public void The_settlement_queries_translate_and_filter_by_project_period_and_status_in_sql()
    {
        using var db = new WorkforceDbContextDesignTimeFactory().CreateDbContext([]);
        var from = new DateOnly(2026, 1, 3);
        var to = new DateOnly(2026, 1, 9);

        var payable = SettlementQueries.PayableLines(db, 3, from, to).ToQueryString();
        Assert.Contains("'Approved'", payable);
        Assert.Contains("[WorkDate] >= @", payable);
        var held = SettlementQueries.HeldParts(db, [1, 2], exceptSettlementId: 5).ToQueryString();
        Assert.Contains("[workforce].[PayrollSettlement]", held);
        Assert.Contains("'Pending'", held);
        Assert.Contains("[workforce].[WorkerPayment]", SettlementQueries.PaidParts(db, [1]).ToQueryString());
        Assert.Contains("GROUP BY", SettlementQueries.WaitingLines(db, 3, from, to).ToQueryString());
    }
}
