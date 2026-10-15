using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Data;

namespace AlMotaheda.Modules.Workforce.Features.Sheets;

/// <summary>The two pay settings (<c>Workforce:RegularHours</c>, <c>Workforce:OvertimeMultiplier</c>).</summary>
internal sealed record LaborPaySettings(decimal RegularHours, decimal OvertimeMultiplier)
{
    public static LaborPaySettings Default { get; } = new(8m, 1.5m);
}

/// <summary>What decides the daily rate of one line.</summary>
/// <param name="LineTradeId">The trade worked that day.</param>
/// <param name="WorkerTradeId">The worker's own trade.</param>
/// <param name="LineTradeDefaultRate">The default daily rate of the trade worked that day.</param>
/// <param name="AssignmentRate">The worker's rate on this project, from the assignment that covers the day.</param>
/// <param name="WorkerRate">The worker's own rate.</param>
internal sealed record RateInputs(int LineTradeId, int WorkerTradeId, decimal LineTradeDefaultRate, decimal? AssignmentRate, decimal? WorkerRate);

/// <summary>One allowance or deduction, as the pay arithmetic needs it.</summary>
internal sealed record PayAdjustment(AdjustmentKind Kind, decimal Amount);

/// <summary>The pay of one line. Copied onto the line when the sheet is approved.</summary>
internal sealed record LinePay(
    decimal DailyRate,
    decimal BaseAmount,
    decimal OvertimeAmount,
    decimal AllowancesAmount,
    decimal DeductionsAmount,
    decimal NetAmount)
{
    /// <summary>The deductions are more than the day's pay; such a line can't be saved or approved.</summary>
    public bool IsNegative => NetAmount < 0;
}

/// <summary>
/// The pay of one labor line (03-workforce.md, "Pay of one line"), with no database access. The approval and every
/// preview call the same method, so a preview always shows what approval will freeze (with today's rates).
/// </summary>
internal static class LaborPay
{
    /// <summary>
    /// The daily rate of a line. A worker who works a day in another trade (a mason standing guard) gets that
    /// trade's default rate. Otherwise: the rate on this project, else the worker's own rate, else the trade's.
    /// </summary>
    public static decimal RateOf(RateInputs inputs) =>
        inputs.LineTradeId != inputs.WorkerTradeId
            ? inputs.LineTradeDefaultRate
            : inputs.AssignmentRate ?? inputs.WorkerRate ?? inputs.LineTradeDefaultRate;

    /// <summary>
    /// base = rate × days; overtime = hours × rate ÷ regular hours × multiplier (to the piastre, decision W3);
    /// net = base + overtime + allowances − deductions. A negative net is returned as is; the caller refuses it.
    /// </summary>
    public static LinePay Compute(
        decimal dailyRate,
        decimal days,
        decimal overtimeHours,
        IEnumerable<PayAdjustment> adjustments,
        LaborPaySettings settings)
    {
        var baseAmount = Money.Round(dailyRate * days);
        var overtime = Money.Round(overtimeHours * dailyRate * settings.OvertimeMultiplier / settings.RegularHours);

        var allowances = 0m;
        var deductions = 0m;
        foreach (var adjustment in adjustments)
        {
            if (adjustment.Kind == AdjustmentKind.Allowance)
            {
                allowances += adjustment.Amount;
            }
            else
            {
                deductions += adjustment.Amount;
            }
        }

        return new LinePay(dailyRate, baseAmount, overtime, allowances, deductions, baseAmount + overtime + allowances - deductions);
    }

    public static LinePay Compute(RateInputs rate, decimal days, decimal overtimeHours, IEnumerable<PayAdjustment> adjustments, LaborPaySettings settings) =>
        Compute(RateOf(rate), days, overtimeHours, adjustments, settings);
}
