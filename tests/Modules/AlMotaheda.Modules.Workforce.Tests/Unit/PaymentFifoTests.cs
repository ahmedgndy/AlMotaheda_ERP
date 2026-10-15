using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Features.Payments;

namespace AlMotaheda.Modules.Workforce.Tests.Unit;

/// <summary>Worker payments settle the oldest approved, unpaid days first, across projects, to the piastre.</summary>
public sealed class PaymentFifoTests
{
    private const int ProjectA = 1;
    private const int ProjectB = 2;

    private static readonly PayableLine[] Days =
    [
        new(LineId: 30, ProjectA, new DateOnly(2026, 1, 3), 1m, Net: 500m),
        new(LineId: 10, ProjectA, new DateOnly(2026, 1, 1), 1m, Net: 300m),
        new(LineId: 20, ProjectB, new DateOnly(2026, 1, 2), 0.5m, Net: 400m),
    ];

    [Fact]
    public void A_payment_settles_the_oldest_days_first_across_projects_and_the_last_one_in_part()
    {
        var plan = PaymentFifo.Plan(600m, PaymentFifo.Settle(Days, []));

        Assert.True(plan.Fits);
        Assert.Equal(1_200m, plan.Outstanding);
        Assert.Equal([new FifoAllocation(10, 300m), new FifoAllocation(20, 300m)], plan.Allocations);
    }

    [Fact]
    public void Days_of_the_same_date_are_settled_by_line_id()
    {
        PayableLine[] sameDay =
        [
            new(LineId: 8, ProjectB, new DateOnly(2026, 1, 1), 1m, 100m),
            new(LineId: 7, ProjectA, new DateOnly(2026, 1, 1), 1m, 100m),
        ];

        var plan = PaymentFifo.Plan(150m, PaymentFifo.Settle(sameDay, []));

        Assert.Equal([new FifoAllocation(7, 100m), new FifoAllocation(8, 50m)], plan.Allocations);
    }

    [Fact]
    public void Paid_days_are_skipped_and_a_partly_paid_day_takes_only_its_rest()
    {
        PaidPart[] paid = [new(10, 300m, PaymentVoided: false), new(20, 150m, PaymentVoided: false)];

        var plan = PaymentFifo.Plan(400m, PaymentFifo.Settle(Days, paid));

        Assert.Equal(750m, plan.Outstanding);
        Assert.Equal([new FifoAllocation(20, 250m), new FifoAllocation(30, 150m)], plan.Allocations);
    }

    [Fact]
    public void A_payment_above_what_is_owed_is_refused()
    {
        var exact = PaymentFifo.Plan(1_200m, PaymentFifo.Settle(Days, []));
        var over = PaymentFifo.Plan(1_200.01m, PaymentFifo.Settle(Days, []));
        var nothingOwed = PaymentFifo.Plan(1m, PaymentFifo.Settle(Days, [new(10, 300m, false), new(20, 400m, false), new(30, 500m, false)]));

        Assert.True(exact.Fits);
        Assert.Equal(1_200m, exact.Allocations.Sum(allocation => allocation.Amount));
        Assert.False(over.Fits);
        Assert.Equal(1_200m, over.Outstanding);
        Assert.Empty(over.Allocations);
        Assert.False(nothingOwed.Fits);
        Assert.Equal(0m, nothingOwed.Outstanding);
    }

    [Fact]
    public void Allocations_of_voided_payments_are_ignored()
    {
        PaidPart[] paid = [new(10, 300m, PaymentVoided: true), new(10, 100m, PaymentVoided: false)];

        var settled = PaymentFifo.Settle(Days, paid);
        var plan = PaymentFifo.Plan(250m, settled);

        Assert.Equal(100m, settled.Single(line => line.LineId == 10).Paid);
        Assert.Equal([new FifoAllocation(10, 200m), new FifoAllocation(20, 50m)], plan.Allocations);
    }

    [Fact]
    public void The_account_shows_approved_pay_paid_and_outstanding_in_total_and_by_project()
    {
        PaidPart[] paid = [new(10, 300m, false), new(20, 100m, false), new(30, 999m, PaymentVoided: true)];
        var settled = PaymentFifo.Settle(Days, paid);

        var total = WorkerAccounts.Of(settled);
        var byProject = WorkerAccounts.ByProject(settled).ToDictionary(entry => entry.ProjectId, entry => entry.Figures);

        Assert.Equal(new WorkerAccountFigures(2.5m, 1_200m, 400m, 800m, OpenLines: 2, new DateOnly(2026, 1, 2)), total);
        Assert.Equal(new WorkerAccountFigures(2m, 800m, 300m, 500m, OpenLines: 1, new DateOnly(2026, 1, 3)), byProject[ProjectA]);
        Assert.Equal(new WorkerAccountFigures(0.5m, 400m, 100m, 300m, OpenLines: 1, new DateOnly(2026, 1, 2)), byProject[ProjectB]);
    }

    [Fact]
    public void A_caller_who_sees_one_project_gets_that_projects_figures_as_the_worker_total()
    {
        PaidPart[] paid = [new(10, 300m, false), new(20, 100m, false)];
        var settled = PaymentFifo.Settle(Days, paid);

        var visible = WorkerAccounts.OnProjects(settled, new HashSet<int> { ProjectA });
        var total = WorkerAccounts.Of(visible);

        // Project B's 400 earned and 100 paid are in no figure; project A's are exactly what a full view shows for A.
        Assert.Equal(new WorkerAccountFigures(2m, 800m, 300m, 500m, OpenLines: 1, new DateOnly(2026, 1, 3)), total);
        Assert.Equal([ProjectA], WorkerAccounts.ByProject(visible).Select(entry => entry.ProjectId));
        Assert.Empty(WorkerAccounts.OnProjects(settled, new HashSet<int>()));
        Assert.Equal(3, WorkerAccounts.OnProjects(settled, new HashSet<int> { ProjectA, ProjectB }).Count);
    }

    [Fact]
    public void A_payment_across_projects_shows_only_its_visible_part_to_a_caller_who_sees_one_project()
    {
        var row = new WorkerPaymentRow
        {
            Id = 5,
            WorkerId = 1,
            Amount = 700m,
            Method = WorkerPaymentMethod.Cash,
            Allocations =
            [
                new(LineId: 10, SheetId: 1, ProjectA, new DateOnly(2026, 1, 1), Shift.Day, 300m),
                new(LineId: 20, SheetId: 2, ProjectB, new DateOnly(2026, 1, 2), Shift.Day, 400m),
            ],
        };

        var partial = WorkerPaymentView.From(row, new HashSet<int> { ProjectA });
        var full = WorkerPaymentView.From(row, new HashSet<int> { ProjectA, ProjectB });
        var unfiltered = WorkerPaymentView.From(row);

        Assert.Equal(300m, partial.Amount);
        Assert.Equal([ProjectA], partial.Allocations.Select(allocation => allocation.ProjectId));
        Assert.Equal(700m, full.Amount);
        Assert.Equal(2, full.Allocations.Count);
        Assert.Equal(700m, unfiltered.Amount);
        Assert.Equal(2, unfiltered.Allocations.Count);
    }
}
