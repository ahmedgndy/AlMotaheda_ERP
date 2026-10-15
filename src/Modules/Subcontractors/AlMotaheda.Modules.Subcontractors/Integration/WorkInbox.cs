using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Subcontractors.Common;
using AlMotaheda.Modules.Subcontractors.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Integration;

/// <summary>
/// Subcontractors' part of the work inbox: measured work and manpower sheets recorded for review (subcontractors
/// Approve, never the recorder). Work has no "returned" state; rejected work is final. Amounts only with
/// subcontractors See money. The parent id is the subcontract, whose page lists the work.
/// </summary>
internal sealed class SubcontractorsInboxSource(SubcontractorsDbContext db) : IWorkInboxSource
{
    public async Task<IReadOnlyList<WorkInboxItem>> ListAsync(WorkInboxQuery query, CancellationToken cancellationToken)
    {
        if (!query.Caller.HasPermission(SubPermissions.Module, PermissionAction.Approve))
        {
            return [];
        }

        var ids = query.ProjectIds.ToList();
        var me = query.UserId;
        var seesMoney = query.Caller.HasPermission(SubPermissions.Module, PermissionAction.SeeMoney);

        var measurements = await db.ProgressMeasurements.AsNoTracking()
            .Where(work => ids.Contains(work.ContractItem.Contract.ProjectId) && work.Status == WorkStatus.Recorded && work.CreatedBy != me)
            .OrderBy(work => work.CreatedAt)
            .Take(query.Limit)
            .Select(work => new
            {
                work.Id, work.ContractItem.Contract.ProjectId, work.ContractItem.ContractId, work.MeasuredOn, work.CreatedAt, work.CreatedBy, work.Amount,
                Line = work.ContractItem.Name, work.ContractItem.Contract.Subcontractor.NameAr, work.ContractItem.Contract.Subcontractor.NameEn,
            })
            .ToListAsync(cancellationToken);

        var manpower = await db.ManpowerSheets.AsNoTracking()
            .Where(sheet => ids.Contains(sheet.Contract.ProjectId) && sheet.Status == WorkStatus.Recorded && sheet.CreatedBy != me)
            .OrderBy(sheet => sheet.CreatedAt)
            .Take(query.Limit)
            .Select(sheet => new
            {
                sheet.Id, sheet.Contract.ProjectId, sheet.ContractId, sheet.ToDate, sheet.CreatedAt, sheet.CreatedBy, sheet.Amount, sheet.Workers,
                sheet.Contract.Subcontractor.NameAr, sheet.Contract.Subcontractor.NameEn,
            })
            .ToListAsync(cancellationToken);

        return
        [
            .. measurements.Select(work => new WorkInboxItem(
                WorkInboxReasons.Review, WorkInboxRecordTypes.Measurement, work.ProjectId, work.Id, work.ContractId, work.MeasuredOn, work.CreatedAt,
                $"{Name(work.NameAr, work.NameEn)} · {work.Line}", seesMoney ? work.Amount : null, work.CreatedBy, null)),
            .. manpower.Select(sheet => new WorkInboxItem(
                WorkInboxReasons.Review, WorkInboxRecordTypes.Manpower, sheet.ProjectId, sheet.Id, sheet.ContractId, sheet.ToDate, sheet.CreatedAt,
                $"{Name(sheet.NameAr, sheet.NameEn)} · {sheet.Workers}", seesMoney ? sheet.Amount : null, sheet.CreatedBy, null)),
        ];
    }

    private static string Name(string ar, string en) => string.IsNullOrWhiteSpace(en) ? ar : $"{ar} / {en}";
}
