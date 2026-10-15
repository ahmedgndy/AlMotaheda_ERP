using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlMotaheda.Modules.Workforce.Features.Sheets;

/// <summary>The new allocations of one line. <paramref name="Field"/> is where form errors on it are reported.</summary>
internal sealed record LineAllocationChange(int LineId, IReadOnlyList<PortionForm> Allocations, string Field);

/// <summary>Why an allocation change was refused, if it was.</summary>
internal sealed record AllocationOutcome(ValidationProblem? Invalid, ProblemHttpResult? Conflict)
{
    public static AllocationOutcome Done { get; } = new(null, null);
}

/// <summary>
/// Sets the item allocations of some lines of one sheet, shared by the sheet-level and the line-level endpoints.
/// Runs under the project lock. It changes only the links: on an approved line the amounts are recomputed from the
/// frozen net; the net, the rate and the payments never change.
/// </summary>
internal static class AllocationChanges
{
    /// <param name="sheet">Tracked, with the lines named in <paramref name="changes"/> and their allocations loaded.</param>
    public static async Task<AllocationOutcome> ApplyAsync(
        WorkforceDbContext db,
        IProjectItems projectItems,
        int projectId,
        DailySheet sheet,
        IReadOnlyList<LineAllocationChange> changes,
        int userId,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var lines = sheet.Lines.ToDictionary(line => line.Id);

        foreach (var change in changes)
        {
            if (!lines.TryGetValue(change.LineId, out var line) || line.IsVoid)
            {
                errors.Add(change.Field.Replace(".allocations", ".lineId", StringComparison.Ordinal), "The line is not on this sheet.");
            }
        }

        if (errors.HasErrors)
        {
            return new AllocationOutcome(WorkforceProblems.Validation(errors), null);
        }

        // Nobody's day may be counted twice.
        if (changes.FirstOrDefault(change => !LaborAllocator.FitsInOneDay(change.Allocations.Select(allocation => allocation.Portion))) is { } tooMuch)
        {
            return new AllocationOutcome(null, WorkforceProblems.ExceedsDay(tooMuch.LineId));
        }

        var itemIds = changes.SelectMany(change => change.Allocations).Select(allocation => allocation.ItemId).Distinct().ToArray();
        var items = await projectItems.FindAsync(projectId, itemIds, cancellationToken);

        foreach (var change in changes)
        {
            for (var index = 0; index < change.Allocations.Count; index++)
            {
                if (!items.ContainsKey(change.Allocations[index].ItemId))
                {
                    errors.Add($"{change.Field}[{index}].itemId", "The project item was not found on this project.");
                }
            }
        }

        if (errors.HasErrors)
        {
            return new AllocationOutcome(WorkforceProblems.Validation(errors), null);
        }

        // An existing link may stay on an item deactivated since; a new link needs an active item.
        foreach (var change in changes)
        {
            var current = lines[change.LineId].Allocations.Select(allocation => allocation.ProjectItemId).ToHashSet();
            if (change.Allocations.FirstOrDefault(allocation => !current.Contains(allocation.ItemId) && !items[allocation.ItemId].IsActive) is { } inactive)
            {
                return new AllocationOutcome(null, WorkforceProblems.InactiveItem(inactive.ItemId));
            }
        }

        foreach (var change in changes)
        {
            Replace(db, lines[change.LineId], change.Allocations, userId, now);
        }

        sheet.UpdatedAt = now;
        sheet.UpdatedBy = userId;
        return AllocationOutcome.Done;
    }

    /// <summary>
    /// Makes the line's allocations equal to <paramref name="wanted"/>: links that stay are updated in place (so the
    /// unique (line, item) index never sees a duplicate), the others are removed or added.
    /// </summary>
    public static void Replace(WorkforceDbContext db, AttendanceLine line, IReadOnlyList<PortionForm> wanted, int userId, DateTime now)
    {
        var byItem = wanted.ToDictionary(allocation => allocation.ItemId);

        foreach (var existing in line.Allocations.ToList())
        {
            if (byItem.TryGetValue(existing.ProjectItemId, out var keep))
            {
                existing.Portion = keep.Portion;
            }
            else
            {
                // Removed through the context: the key doesn't cascade, so EF won't delete an orphan by itself.
                db.LaborAllocations.Remove(existing);
                line.Allocations.Remove(existing);
            }
        }

        foreach (var allocation in wanted.Where(allocation => line.Allocations.All(existing => existing.ProjectItemId != allocation.ItemId)))
        {
            line.Allocations.Add(new LaborAllocation
            {
                ProjectItemId = allocation.ItemId,
                Portion = allocation.Portion,
                CreatedAt = now,
                CreatedBy = userId,
            });
        }

        // Approved lines get their item amounts again from the frozen net.
        SheetApproval.ApplyAmounts(line);
    }
}
