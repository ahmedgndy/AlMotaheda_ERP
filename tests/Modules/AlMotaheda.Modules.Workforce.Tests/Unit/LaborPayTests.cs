using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Features.Sheets;

namespace AlMotaheda.Modules.Workforce.Tests.Unit;

/// <summary>The pay of one labor line, to the piastre, with no database.</summary>
public sealed class LaborPayTests
{
    private const int Mason = 3;
    private const int Guard = 13;

    private static readonly LaborPaySettings Settings = LaborPaySettings.Default;

    [Fact]
    public void A_full_day_pays_the_daily_rate()
    {
        var pay = LaborPay.Compute(450m, days: 1m, overtimeHours: 0m, [], Settings);

        Assert.Equal(450m, pay.DailyRate);
        Assert.Equal(450m, pay.BaseAmount);
        Assert.Equal(0m, pay.OvertimeAmount);
        Assert.Equal(0m, pay.AllowancesAmount);
        Assert.Equal(0m, pay.DeductionsAmount);
        Assert.Equal(450m, pay.NetAmount);
    }

    [Fact]
    public void A_half_day_pays_half_the_rate_rounded_to_the_piastre()
    {
        var even = LaborPay.Compute(450m, days: 0.5m, overtimeHours: 0m, [], Settings);
        var odd = LaborPay.Compute(333.33m, days: 0.5m, overtimeHours: 0m, [], Settings);

        Assert.Equal(225m, even.BaseAmount);
        Assert.Equal(225m, even.NetAmount);
        Assert.Equal(166.67m, odd.BaseAmount); // 166.665 rounds away from zero
        Assert.Equal(166.67m, odd.NetAmount);
    }

    [Fact]
    public void Overtime_is_hours_times_rate_over_eight_times_one_and_a_half()
    {
        var two = LaborPay.Compute(450m, days: 1m, overtimeHours: 2m, [], Settings);
        var odd = LaborPay.Compute(333m, days: 1m, overtimeHours: 1.5m, [], Settings);

        Assert.Equal(168.75m, two.OvertimeAmount); // 2 × 450 ÷ 8 × 1.5
        Assert.Equal(618.75m, two.NetAmount);
        Assert.Equal(93.66m, odd.OvertimeAmount); // 1.5 × 333 ÷ 8 × 1.5 = 93.65625
        Assert.Equal(426.66m, odd.NetAmount);
    }

    [Fact]
    public void Overtime_follows_the_settings()
    {
        var pay = LaborPay.Compute(500m, days: 1m, overtimeHours: 1m, [], new LaborPaySettings(RegularHours: 10m, OvertimeMultiplier: 2m));

        Assert.Equal(100m, pay.OvertimeAmount); // 1 × 500 ÷ 10 × 2
        Assert.Equal(600m, pay.NetAmount);
    }

    [Fact]
    public void Allowances_add_and_deductions_subtract()
    {
        PayAdjustment[] adjustments =
        [
            new(AdjustmentKind.Allowance, 50m),   // food
            new(AdjustmentKind.Allowance, 30m),   // transport
            new(AdjustmentKind.Deduction, 100m),  // damaged formwork panel
        ];

        var pay = LaborPay.Compute(430m, days: 1m, overtimeHours: 1m, adjustments, Settings);

        Assert.Equal(430m, pay.BaseAmount);
        Assert.Equal(80.63m, pay.OvertimeAmount); // 1 × 430 ÷ 8 × 1.5 = 80.625
        Assert.Equal(80m, pay.AllowancesAmount);
        Assert.Equal(100m, pay.DeductionsAmount);
        Assert.Equal(490.63m, pay.NetAmount);
        Assert.False(pay.IsNegative);
    }

    [Fact]
    public void Deductions_above_the_pay_give_a_negative_net_that_the_caller_must_refuse()
    {
        var pay = LaborPay.Compute(450m, days: 0.5m, overtimeHours: 0m, [new(AdjustmentKind.Deduction, 300m)], Settings);

        Assert.Equal(-75m, pay.NetAmount);
        Assert.True(pay.IsNegative);
    }

    [Fact]
    public void The_rate_is_the_project_rate_then_the_worker_rate_then_the_trade_default()
    {
        Assert.Equal(520m, LaborPay.RateOf(new RateInputs(Mason, Mason, 480m, AssignmentRate: 520m, WorkerRate: 400m)));
        Assert.Equal(400m, LaborPay.RateOf(new RateInputs(Mason, Mason, 480m, AssignmentRate: null, WorkerRate: 400m)));
        Assert.Equal(480m, LaborPay.RateOf(new RateInputs(Mason, Mason, 480m, AssignmentRate: null, WorkerRate: null)));
    }

    [Fact]
    public void A_day_worked_in_another_trade_pays_that_trades_default_rate()
    {
        // A mason with his own rate and a project rate stands guard for a night: the guard rate applies.
        var rate = new RateInputs(LineTradeId: Guard, WorkerTradeId: Mason, LineTradeDefaultRate: 450m, AssignmentRate: 520m, WorkerRate: 400m);

        var pay = LaborPay.Compute(rate, days: 1m, overtimeHours: 0m, [], Settings);

        Assert.Equal(450m, LaborPay.RateOf(rate));
        Assert.Equal(450m, pay.DailyRate);
        Assert.Equal(450m, pay.NetAmount);
    }
}
