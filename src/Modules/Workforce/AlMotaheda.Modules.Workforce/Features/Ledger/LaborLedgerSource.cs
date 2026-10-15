using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Features.Sheets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Workforce.Features.Ledger;

/// <summary>Labor waiting for review: shown apart, never cost. The amount is a preview with today's rates.</summary>
internal sealed record PendingLabor(decimal Amount, int Lines);

/// <summary>The pending labor arithmetic, with no database access: the same <see cref="LaborPay"/> as approval.</summary>
internal static class LaborPending
{
    public static PendingLabor Sum(IReadOnlyList<PendingLineRow> lines, LaborPaySettings settings)
    {
        var amount = 0m;
        foreach (var line in lines)
        {
            var pay = LaborPay.Compute(
                new RateInputs(line.LineTradeId, line.WorkerTradeId, line.LineTradeDefaultRate, line.AssignmentRate, line.WorkerRate),
                line.Days,
                line.OvertimeHours,
                [new PayAdjustment(AdjustmentKind.Allowance, line.Allowances), new PayAdjustment(AdjustmentKind.Deduction, line.Deductions)],
                settings);
            amount += Math.Max(0m, pay.NetAmount);
        }

        return new PendingLabor(amount, lines.Count);
    }
}

/// <summary>
/// Loads the flat rows of one project and runs <see cref="LaborItemLedger"/> over them. Shared by the contracts the
/// Projects module calls (<see cref="ILaborItemCosts"/>, <see cref="IProjectCostSource"/>) and the labor summary,
/// so the items screens, the overview and this module's own screens show the same figures.
/// </summary>
internal sealed class LaborLedgerSource(WorkforceDbContext db, IOptions<WorkforceOptions> options)
{
    public async Task<LaborLedger> LoadAsync(int projectId, DateOnly? from, DateOnly? to, bool withDeductionReasons, CancellationToken cancellationToken)
    {
        var lines = await LaborQueries.LedgerLines(db, projectId, from, to).ToListAsync(cancellationToken);
        if (lines.Count == 0)
        {
            return LaborLedger.Empty;
        }

        var allocations = await LaborQueries.LedgerAllocations(db, projectId, from, to).ToListAsync(cancellationToken);
        var deductions = withDeductionReasons
            ? await LaborQueries.LedgerDeductions(db, projectId).ToListAsync(cancellationToken)
            : [];

        return LaborItemLedger.Compute(lines, allocations, deductions);
    }

    public async Task<PendingLabor> PendingAsync(int projectId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var rows = await LaborQueries.PendingLines(db, projectId, from, to).ToListAsync(cancellationToken);
        return LaborPending.Sum(rows, options.Value.PaySettings);
    }

    /// <summary>The project's labor for the overview and the reports (<see cref="IProjectCostSource"/>).</summary>
    public async Task<ProjectCostFigures> SummarizeAsync(int projectId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var ledger = await LoadAsync(projectId, from, to, withDeductionReasons: false, cancellationToken);
        var pending = await PendingAsync(projectId, from, to, cancellationToken);

        return new ProjectCostFigures(ledger.Recognized, ledger.Paid, ledger.RecognizedLines, pending.Amount, pending.Lines, Committed: 0m);
    }
}
