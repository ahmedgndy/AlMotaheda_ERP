using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Integration;

/// <summary>
/// Workforce's section of a person's home page (<see cref="IPersonalOverviewSource"/>, section 12.A/B/C): the daily
/// labor sheets they recorded (the daily labor cost record) by status, and the weekly settlements they recorded or
/// paid from their custody. A recorded or approved sheet means work and cost, never a payment: only a confirmed
/// settlement is one.
/// </summary>
internal sealed class WorkforcePersonalOverview(WorkforceDbContext db) : IPersonalOverviewSource
{
    public string Section => "Labor";

    public async Task<PersonalOverviewSection?> LoadAsync(PersonalOverviewQuery query, CancellationToken cancellationToken)
    {
        var me = query.UserId;
        var projects = query.ProjectIds.ToList();
        if (projects.Count == 0)
        {
            return null;
        }

        var weekStart = query.Today.AddDays(-(int)query.Today.DayOfWeek + (query.Today.DayOfWeek < DayOfWeek.Saturday ? -1 : 6));
        var sheets = await db.DailySheets.AsNoTracking()
            .Where(sheet => sheet.RecordedBy == me && projects.Contains(sheet.ProjectId))
            .OrderByDescending(sheet => sheet.WorkDate).ThenByDescending(sheet => sheet.Id)
            .Take(200)
            .Select(sheet => new { sheet.Id, sheet.ProjectId, sheet.WorkDate, sheet.Shift, sheet.Status, sheet.SubmittedAt, sheet.ReviewedAt, sheet.ReviewNote, sheet.CreatedAt, Workers = sheet.Lines.Count(line => !line.IsVoid) })
            .ToListAsync(cancellationToken);
        var settlements = await db.PayrollSettlements.AsNoTracking()
            .Where(settlement => (settlement.PaidByUserId == me || settlement.RecordedBy == me) && projects.Contains(settlement.ProjectId))
            .OrderByDescending(settlement => settlement.Id)
            .Take(50)
            .Select(settlement => new { settlement.Id, settlement.ProjectId, settlement.PeriodFrom, settlement.PeriodTo, settlement.PaidOn, settlement.Amount, settlement.Status, settlement.RecordedAt, settlement.ReviewNote, settlement.ReverseReason })
            .ToListAsync(cancellationToken);
        if (sheets.Count == 0 && settlements.Count == 0)
        {
            return null;
        }

        var items = sheets.Take(query.Limit).Select(sheet => new PersonalOverviewItem(
                "LaborSheet", sheet.Id, sheet.ProjectId, sheet.Status.ToString(), sheet.WorkDate, sheet.SubmittedAt ?? sheet.CreatedAt,
                $"تقرير عمالة {sheet.WorkDate:yyyy-MM-dd} ({sheet.Workers} عامل)", $"Daily labor sheet {sheet.WorkDate:yyyy-MM-dd} ({sheet.Workers} workers, {sheet.Shift})", null,
                sheet.Status == SheetStatus.Returned ? sheet.ReviewNote : null, $"/api/projects/{sheet.ProjectId}/labor/sheets/{sheet.Id}"))
            .Concat(settlements.Take(query.Limit).Select(settlement => new PersonalOverviewItem(
                "PayrollSettlement", settlement.Id, settlement.ProjectId, settlement.Status.ToString(), settlement.PaidOn, settlement.RecordedAt,
                $"تسوية أجور الأسبوع {settlement.PeriodFrom:yyyy-MM-dd} – {settlement.PeriodTo:yyyy-MM-dd}", $"Weekly labor settlement {settlement.PeriodFrom:yyyy-MM-dd} – {settlement.PeriodTo:yyyy-MM-dd}",
                settlement.Amount, settlement.ReviewNote ?? settlement.ReverseReason, $"/api/projects/{settlement.ProjectId}/labor/settlements/{settlement.Id}")))
            .OrderByDescending(item => item.Since).Take(query.Limit).ToList();

        var figures = new Dictionary<string, decimal>
        {
            ["sheetsDraft"] = sheets.Count(sheet => sheet.Status == SheetStatus.Draft),
            ["sheetsSubmitted"] = sheets.Count(sheet => sheet.Status == SheetStatus.Submitted),
            ["sheetsReturned"] = sheets.Count(sheet => sheet.Status == SheetStatus.Returned),
            ["sheetsApprovedThisWeek"] = sheets.Count(sheet => sheet.Status == SheetStatus.Approved && sheet.WorkDate >= weekStart),
            ["settlementsPending"] = settlements.Count(settlement => settlement.Status == SettlementStatus.Pending),
            ["settlementsPendingAmount"] = settlements.Where(settlement => settlement.Status == SettlementStatus.Pending).Sum(settlement => settlement.Amount),
            ["settlementsConfirmedThisWeekAmount"] = settlements.Where(settlement => settlement.Status == SettlementStatus.Confirmed && settlement.PaidOn >= weekStart).Sum(settlement => settlement.Amount),
        };
        return new PersonalOverviewSection(Section, figures, items);
    }
}
