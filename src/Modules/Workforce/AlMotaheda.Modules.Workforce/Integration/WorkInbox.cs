using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Security;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Integration;

/// <summary>
/// Workforce's part of the work inbox: labor sheets submitted for review (workers Approve, never the recorder) and
/// sheets returned to their recorder.
/// </summary>
internal sealed class WorkforceInboxSource(WorkforceDbContext db) : IWorkInboxSource
{
    public async Task<IReadOnlyList<WorkInboxItem>> ListAsync(WorkInboxQuery query, CancellationToken cancellationToken)
    {
        var ids = query.ProjectIds.ToList();
        var me = query.UserId;
        var items = new List<WorkInboxItem>();

        if (query.Caller.HasPermission(WorkforcePermissions.Workers, PermissionAction.Approve))
        {
            var submitted = await db.DailySheets.AsNoTracking()
                .Where(sheet => ids.Contains(sheet.ProjectId) && sheet.Status == SheetStatus.Submitted && sheet.RecordedBy != me)
                .OrderBy(sheet => sheet.SubmittedAt)
                .Take(query.Limit)
                .Select(sheet => new { sheet.Id, sheet.ProjectId, sheet.WorkDate, sheet.Shift, sheet.SubmittedAt, sheet.CreatedAt, sheet.RecordedBy, Workers = sheet.Lines.Count })
                .ToListAsync(cancellationToken);
            items.AddRange(submitted.Select(sheet => new WorkInboxItem(
                WorkInboxReasons.Review, WorkInboxRecordTypes.LaborSheet, sheet.ProjectId, sheet.Id, null, sheet.WorkDate,
                sheet.SubmittedAt ?? sheet.CreatedAt, $"{sheet.Shift} · {sheet.Workers}", null, sheet.RecordedBy, null)));
        }

        var returned = await db.DailySheets.AsNoTracking()
            .Where(sheet => ids.Contains(sheet.ProjectId) && sheet.Status == SheetStatus.Returned && sheet.RecordedBy == me)
            .OrderBy(sheet => sheet.ReviewedAt)
            .Take(query.Limit)
            .Select(sheet => new { sheet.Id, sheet.ProjectId, sheet.WorkDate, sheet.Shift, sheet.ReviewedAt, sheet.CreatedAt, sheet.ReviewedBy, sheet.ReviewNote, Workers = sheet.Lines.Count })
            .ToListAsync(cancellationToken);
        items.AddRange(returned.Select(sheet => new WorkInboxItem(
            WorkInboxReasons.Returned, WorkInboxRecordTypes.LaborSheet, sheet.ProjectId, sheet.Id, null, sheet.WorkDate,
            sheet.ReviewedAt ?? sheet.CreatedAt, $"{sheet.Shift} · {sheet.Workers}", null, sheet.ReviewedBy, sheet.ReviewNote)));

        return items;
    }
}
