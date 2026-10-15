using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Sheets;

/// <summary>One line of a sheet form with everything the database said about it.</summary>
/// <param name="TradeId">The trade worked that day (the worker's own when the form left it empty).</param>
/// <param name="WorkType">The work type (the trade's default when the form left it empty).</param>
/// <param name="Preview">The pay with today's rates: what approval would freeze if nothing changes.</param>
internal sealed record ResolvedLine(LineForm Form, int TradeId, string WorkType, LinePay Preview);

/// <summary>The outcome of checking a sheet form against the database: the resolved lines, or why not.</summary>
internal sealed record SheetCheck(IReadOnlyList<ResolvedLine>? Lines, ValidationProblem? Invalid, ProblemHttpResult? Conflict);

/// <summary>A worker's facts that decide whether and how they may be recorded on a day.</summary>
internal sealed record WorkerFacts(int Id, WorkerStatus Status, int TradeId, decimal? DailyRate);

/// <summary>A trade's facts for a line.</summary>
internal sealed record TradeFacts(int Id, bool IsActive, decimal DefaultDailyRate, string DefaultWorkType);

/// <summary>
/// The database checks of a sheet form, shared by create and edit. In order: unknown workers, trades and items are
/// field errors (400); then the rules (409): portions above one day, an inactive worker, a worker not assigned to
/// the project that day, an inactive item, a negative net, a worker already recorded for that day and shift.
/// </summary>
internal static class SheetReferences
{
    public static async Task<SheetCheck> ResolveAsync(
        WorkforceDbContext db,
        IProjectItems projectItems,
        int projectId,
        SheetFields form,
        int? currentSheetId,
        LaborPaySettings settings,
        CancellationToken cancellationToken)
    {
        // Pure rule first: nobody's day may be counted twice.
        foreach (var line in form.Lines)
        {
            if (!LaborAllocator.FitsInOneDay(line.Allocations.Select(allocation => allocation.Portion)))
            {
                return Refuse(WorkforceProblems.ExceedsDay(line.Index));
            }
        }

        var errors = new ValidationErrors();
        var workerIds = form.Lines.Select(line => line.WorkerId).ToArray();

        var workers = await db.Workers.AsNoTracking()
            .Where(worker => workerIds.Contains(worker.Id))
            .Select(worker => new WorkerFacts(worker.Id, worker.Status, worker.TradeId, worker.DailyRate))
            .ToDictionaryAsync(worker => worker.Id, cancellationToken);

        var tradeIds = form.Lines.Where(line => line.TradeId is not null).Select(line => line.TradeId!.Value)
            .Concat(workers.Values.Select(worker => worker.TradeId))
            .Distinct()
            .ToArray();
        var trades = await db.Trades.AsNoTracking()
            .Where(trade => tradeIds.Contains(trade.Id))
            .Select(trade => new TradeFacts(trade.Id, trade.IsActive, trade.DefaultDailyRate, trade.DefaultWorkType))
            .ToDictionaryAsync(trade => trade.Id, cancellationToken);

        var itemIds = form.Lines.SelectMany(line => line.Allocations).Select(allocation => allocation.ItemId).Distinct().ToArray();
        var items = await projectItems.FindAsync(projectId, itemIds, cancellationToken);

        foreach (var line in form.Lines)
        {
            var field = $"lines[{line.Index}]";
            if (!workers.TryGetValue(line.WorkerId, out var worker))
            {
                errors.Add($"{field}.workerId", "The worker was not found.");
            }

            if (line.TradeId is { } tradeId)
            {
                if (!trades.TryGetValue(tradeId, out var trade))
                {
                    errors.Add($"{field}.tradeId", "The trade was not found.");
                }
                else if (!trade.IsActive && tradeId != worker?.TradeId)
                {
                    errors.Add($"{field}.tradeId", "The trade is inactive.");
                }
            }

            for (var index = 0; index < line.Allocations.Count; index++)
            {
                if (!items.ContainsKey(line.Allocations[index].ItemId))
                {
                    errors.Add($"{field}.allocations[{index}].itemId", "The project item was not found on this project.");
                }
            }
        }

        if (errors.HasErrors)
        {
            return new SheetCheck(null, WorkforceProblems.Validation(errors), null);
        }

        if (await CheckWorkersAsync(db, projectId, form.WorkDate, workers, cancellationToken) is { } refused)
        {
            return Refuse(refused.Problem);
        }

        var assignmentRates = await AssignmentRatesAsync(db, projectId, form.WorkDate, workerIds, cancellationToken);

        var resolved = new List<ResolvedLine>(form.Lines.Count);
        foreach (var line in form.Lines)
        {
            if (line.Allocations.FirstOrDefault(allocation => !items[allocation.ItemId].IsActive) is { } inactive)
            {
                return Refuse(WorkforceProblems.InactiveItem(inactive.ItemId));
            }

            var worker = workers[line.WorkerId];
            var trade = trades[line.TradeId ?? worker.TradeId];
            var rate = new RateInputs(trade.Id, worker.TradeId, trade.DefaultDailyRate, assignmentRates.GetValueOrDefault(worker.Id), worker.DailyRate);
            var preview = LaborPay.Compute(
                rate,
                line.Days,
                line.OvertimeHours,
                line.Adjustments.Select(adjustment => new PayAdjustment(adjustment.Kind, adjustment.Amount)),
                settings);

            if (preview.IsNegative)
            {
                return Refuse(WorkforceProblems.NetBelowZero(worker.Id));
            }

            resolved.Add(new ResolvedLine(line, trade.Id, line.WorkType ?? trade.DefaultWorkType, preview));
        }

        var taken = await db.AttendanceLines.AsNoTracking()
            .Where(line => !line.IsVoid && line.WorkDate == form.WorkDate && line.Shift == form.Shift
                && workerIds.Contains(line.WorkerId) && line.SheetId != (currentSheetId ?? 0))
            .Select(line => (int?)line.WorkerId)
            .FirstOrDefaultAsync(cancellationToken);
        if (taken is { } recordedWorker)
        {
            return Refuse(WorkforceProblems.AlreadyRecorded(recordedWorker));
        }

        return new SheetCheck(resolved, null, null);
    }

    /// <summary>
    /// Every worker must be active and assigned to the project on the day. Checked on create, edit and submit,
    /// because a worker can be set inactive or taken off the project in between.
    /// </summary>
    public static async Task<(ProblemHttpResult Problem, int WorkerId)?> CheckWorkersAsync(
        WorkforceDbContext db,
        int projectId,
        DateOnly workDate,
        IReadOnlyDictionary<int, WorkerFacts> workers,
        CancellationToken cancellationToken)
    {
        if (workers.Values.FirstOrDefault(worker => worker.Status != WorkerStatus.Active) is { } inactive)
        {
            return (WorkforceProblems.InactiveWorker(inactive.Id), inactive.Id);
        }

        var ids = workers.Keys.ToArray();
        var assigned = await AssignedOnQuery(db, projectId, workDate, ids).Select(assignment => assignment.WorkerId).Distinct().ToListAsync(cancellationToken);
        var missing = ids.FirstOrDefault(id => !assigned.Contains(id));
        if (missing > 0)
        {
            return (WorkforceProblems.WorkerNotOnProject(missing, workDate), missing);
        }

        return null;
    }

    /// <summary>The worker's rate on this project on that day, from the assignment that covers it (latest start wins).</summary>
    public static async Task<Dictionary<int, decimal?>> AssignmentRatesAsync(
        WorkforceDbContext db, int projectId, DateOnly workDate, IReadOnlyCollection<int> workerIds, CancellationToken cancellationToken)
    {
        var rows = await AssignedOnQuery(db, projectId, workDate, workerIds)
            .Select(assignment => new { assignment.WorkerId, assignment.StartDate, assignment.DailyRate })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.WorkerId)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(row => row.StartDate).First().DailyRate);
    }

    /// <summary>The assignments of these workers that cover <paramref name="workDate"/> on the project.</summary>
    public static IQueryable<WorkerAssignment> AssignedOnQuery(WorkforceDbContext db, int projectId, DateOnly workDate, IReadOnlyCollection<int> workerIds) =>
        db.WorkerAssignments.AsNoTracking()
            .Where(assignment => assignment.ProjectId == projectId && workerIds.Contains(assignment.WorkerId)
                && assignment.StartDate <= workDate && (assignment.EndDate == null || assignment.EndDate >= workDate));

    /// <summary>Builds the line rows of a sheet from resolved form lines.</summary>
    public static void AddLines(DailySheet sheet, IReadOnlyList<ResolvedLine> lines, DateTime now, int userId)
    {
        foreach (var resolved in lines)
        {
            var form = resolved.Form;
            var line = new AttendanceLine
            {
                WorkerId = form.WorkerId,
                TradeId = resolved.TradeId,
                WorkDate = sheet.WorkDate,
                Shift = sheet.Shift,
                Days = form.Days,
                OvertimeHours = form.OvertimeHours,
                WorkType = resolved.WorkType,
                Notes = form.Notes,
            };

            line.Adjustments.AddRange(form.Adjustments.Select(adjustment => new LineAdjustment
            {
                Kind = adjustment.Kind,
                Type = adjustment.Type,
                Amount = adjustment.Amount,
                Reason = adjustment.Reason,
            }));

            line.Allocations.AddRange(form.Allocations.Select(allocation => new LaborAllocation
            {
                ProjectItemId = allocation.ItemId,
                Portion = allocation.Portion,
                CreatedAt = now,
                CreatedBy = userId,
            }));

            sheet.Lines.Add(line);
        }
    }

    private static SheetCheck Refuse(ProblemHttpResult problem) => new(null, null, problem);
}
