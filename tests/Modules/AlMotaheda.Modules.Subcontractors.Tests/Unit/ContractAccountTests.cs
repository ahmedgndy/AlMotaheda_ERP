using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Features.Accounts;

namespace AlMotaheda.Modules.Subcontractors.Tests.Unit;

/// <summary>
/// The contract account against the prototype's three seed checks (unit rate, 5 % retention, daily rate), with
/// every figure asserted.
/// </summary>
public sealed class ContractAccountTests
{
    private static int _nextId;

    internal static WorkRow Measurement(decimal amount, WorkStatus status = WorkStatus.Approved, decimal allocated = 0, int contractId = 1) =>
        new(WorkKind.Measurement, ++_nextId, contractId, 7, 1, null, new DateOnly(2026, 9, 1), amount, status, allocated);

    internal static WorkRow Manpower(decimal amount, WorkStatus status = WorkStatus.Approved, decimal allocated = 0, int contractId = 1) =>
        new(WorkKind.Manpower, ++_nextId, contractId, 7, null, null, new DateOnly(2026, 9, 1), amount, status, allocated);

    internal static PaymentRow Payment(decimal amount, bool voided = false, int contractId = 1) =>
        new(++_nextId, contractId, new DateOnly(2026, 9, 2), amount, voided, 0);

    [Fact]
    public void Unit_rate_contract_matches_the_prototype_seed()
    {
        // CT-011-07: value 1,000,000; executed 500,000; paid 400,000; no retention.
        var work = new[]
        {
            Measurement(300_000, allocated: 300_000),
            Measurement(200_000, allocated: 100_000),
            Measurement(100_000, WorkStatus.Recorded),
            Measurement(50_000, WorkStatus.Rejected),
        };
        var payments = new[] { Payment(300_000), Payment(100_000), Payment(70_000, voided: true) };

        var account = ContractAccount.Compute(1_000_000m, 0m, isCompleted: false, work, payments);

        Assert.Equal(1_000_000m, account.Value);
        Assert.Equal(500_000m, account.Executed);
        Assert.Equal(2, account.ExecutedRecords);
        Assert.Equal(100_000m, account.PendingReview);
        Assert.Equal(1, account.PendingRecords);
        Assert.Equal(500_000m, account.RemainingToExecute);
        Assert.Equal(400_000m, account.PaidTotal);
        Assert.Equal(400_000m, account.AppliedToWork);
        Assert.Equal(0m, account.UnappliedAdvance);
        Assert.Equal(100_000m, account.OutstandingWork);
        Assert.Equal(0m, account.RetentionHeld);
        Assert.Equal(100_000m, account.DueNow);
        Assert.Equal(50m, account.CompletedPercent);

        // The prototype's "remaining" (value − paid) is 600,000.
        Assert.Equal(600_000m, account.Value - account.PaidTotal);
    }

    [Fact]
    public void Five_percent_retention_matches_the_prototype_seed()
    {
        // CT-011-01: executed 3,045,000; 5 % retention; paid 2,450,000.
        var work = new[] { Measurement(2_000_000, allocated: 2_000_000), Measurement(1_045_000, allocated: 450_000) };
        var payments = new[] { Payment(2_000_000), Payment(450_000) };

        var account = ContractAccount.Compute(3_500_000m, 5m, isCompleted: false, work, payments);

        Assert.Equal(3_045_000m, account.Executed);
        Assert.Equal(152_250m, account.RetentionHeld);
        Assert.Equal(2_450_000m, account.PaidTotal);
        Assert.Equal(442_750m, account.DueNow);
        Assert.Equal(455_000m, account.RemainingToExecute);
        Assert.Equal(595_000m, account.OutstandingWork);
        Assert.Equal(0m, account.UnappliedAdvance);
        Assert.Equal(87m, account.CompletedPercent);
        Assert.Equal(5m, account.RetentionPercent);

        // A completed contract holds no retention, so everything executed and unpaid is due.
        var completed = ContractAccount.Compute(3_500_000m, 5m, isCompleted: true, work, payments);
        Assert.Equal(0m, completed.RetentionHeld);
        Assert.Equal(595_000m, completed.DueNow);
    }

    [Fact]
    public void Daily_rate_contract_matches_the_prototype_seed()
    {
        // CT-012-09: ceiling 200,000; approved manpower 131,200; pending 19,200; paid 83,200.
        var work = new[]
        {
            Manpower(64_000, allocated: 64_000),
            Manpower(67_200, allocated: 19_200),
            Manpower(19_200, WorkStatus.Recorded),
        };
        var payments = new[] { Payment(64_000), Payment(19_200) };

        var account = ContractAccount.Compute(200_000m, 0m, isCompleted: false, work, payments);

        Assert.Equal(131_200m, account.Executed);
        Assert.Equal(19_200m, account.PendingReview);
        Assert.Equal(68_800m, account.RemainingToExecute);
        Assert.Equal(83_200m, account.PaidTotal);
        Assert.Equal(48_000m, account.DueNow);
        Assert.Equal(48_000m, account.OutstandingWork);
        Assert.Equal(65.6m, account.CompletedPercent);
    }

    [Fact]
    public void An_advance_is_paid_but_not_applied_and_never_makes_anything_due()
    {
        var account = ContractAccount.Compute(100_000m, 10m, isCompleted: false, [Measurement(20_000, allocated: 20_000)], [Payment(50_000)]);

        Assert.Equal(50_000m, account.PaidTotal);
        Assert.Equal(20_000m, account.AppliedToWork);
        Assert.Equal(30_000m, account.UnappliedAdvance);
        Assert.Equal(0m, account.OutstandingWork);
        Assert.Equal(2_000m, account.RetentionHeld);
        Assert.Equal(0m, account.DueNow);
    }

    [Fact]
    public void Executed_work_above_the_value_shows_no_negative_commitment_and_caps_at_100_percent()
    {
        var account = ContractAccount.Compute(100m, 0m, isCompleted: false, [Measurement(150)], []);

        Assert.Equal(0m, account.RemainingToExecute);
        Assert.Equal(100m, account.CompletedPercent);
    }

    [Fact]
    public void Retention_is_rounded_to_the_piastre()
    {
        var account = ContractAccount.Compute(10_000m, 2.5m, isCompleted: false, [Measurement(333.33m)], []);

        Assert.Equal(8.33m, account.RetentionHeld);
        Assert.Equal(325m, account.DueNow);
    }

    [Fact]
    public void Totals_add_every_contract()
    {
        var first = ContractAccount.Compute(1_000_000m, 0m, false, [Measurement(500_000, allocated: 400_000)], [Payment(400_000)]);
        var second = ContractAccount.Compute(200_000m, 0m, false, [Manpower(131_200, allocated: 83_200)], [Payment(83_200)]);

        var totals = ContractTotals.Sum([first, second]);

        Assert.Equal(2, totals.ContractCount);
        Assert.Equal(1_200_000m, totals.Value);
        Assert.Equal(631_200m, totals.Executed);
        Assert.Equal(568_800m, totals.RemainingToExecute);
        Assert.Equal(483_200m, totals.PaidTotal);
        Assert.Equal(148_000m, totals.DueNow);
        Assert.Equal(148_000m, totals.OutstandingWork);
    }
}
