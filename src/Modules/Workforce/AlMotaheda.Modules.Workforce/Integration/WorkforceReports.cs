using System.Globalization;
using System.Security.Claims;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Reports.Contracts;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Features.Payments;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Integration;

/// <summary>
/// The worker weekly payroll: approved days per worker per week, their pay, what is paid on them and what is still
/// owed. Paid amounts come from the same oldest-day-first rule as the worker account (<see cref="PaymentFifo"/>).
/// </summary>
internal sealed class WorkforceReports(WorkforceDbContext db, IVisibleProjects projects, IProjectAccess access) : IReportSource
{
    public const string WeeklyPayroll = "WRK-PAYROLL";

    public IReadOnlyList<ReportDefinition> Reports { get; } =
    [
        new(WeeklyPayroll, "رواتب العمال الأسبوعية", "Worker weekly payroll", ReportCategories.Labor, [new("workers", ReportActions.SeeMoney)],
            [
                new("from", ReportFilterTypes.Date, true, "من تاريخ", "From"),
                new("to", ReportFilterTypes.Date, true, "إلى تاريخ", "To"),
                new("projectId", ReportFilterTypes.Id, false, "المشروع", "Project"),
            ],
            [
                new("week", "الأسبوع", "Week", ReportColumnTypes.Text),
                new("worker", "العامل", "Worker", ReportColumnTypes.Text),
                new("days", "الأيام", "Days", ReportColumnTypes.Quantity, Total: true),
                new("pay", "الأجر المستحق", "Pay", ReportColumnTypes.Money, Total: true),
                new("paid", "المدفوع", "Paid", ReportColumnTypes.Money, Total: true),
                new("outstanding", "المتبقي", "Outstanding", ReportColumnTypes.Money, Total: true),
            ]),
    ];

    public async Task<ReportData?> RunAsync(string code, ReportQuery query, ClaimsPrincipal caller, CancellationToken cancellationToken)
    {
        if (code != WeeklyPayroll)
        {
            throw new ArgumentOutOfRangeException(nameof(code), code, "Not a Workforce report.");
        }

        var projectId = query.Id("projectId");
        if (projectId is { } id && await access.FindVisibleAsync(caller, id, cancellationToken) is null)
        {
            return null;
        }

        var from = query.Date("from")!.Value;
        var to = query.Date("to")!.Value;
        var visible = await projects.IdsAsync(caller, cancellationToken);

        // The workers with approved days in the period; then all their days and payments, so the FIFO is exact.
        var workerIds = await db.AttendanceLines.AsNoTracking()
            .Where(line => !line.IsVoid && line.Sheet.Status == SheetStatus.Approved && line.NetAmount != null && line.WorkDate >= from && line.WorkDate <= to)
            .Select(line => line.WorkerId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var payable = await db.AttendanceLines.AsNoTracking()
            .Where(line => workerIds.Contains(line.WorkerId) && !line.IsVoid && line.Sheet.Status == SheetStatus.Approved && line.NetAmount != null)
            .Select(line => new { line.WorkerId, Line = new PayableLine(line.Id, line.Sheet.ProjectId, line.WorkDate, line.Days, line.NetAmount!.Value) })
            .ToListAsync(cancellationToken);
        var paid = await db.WorkerPaymentAllocations.AsNoTracking()
            .Where(allocation => workerIds.Contains(allocation.Payment.WorkerId))
            .Select(allocation => new { allocation.Payment.WorkerId, Part = new PaidPart(allocation.LineId, allocation.Amount, allocation.Payment.VoidedAt != null) })
            .ToListAsync(cancellationToken);
        var names = await db.Workers.AsNoTracking().Where(worker => workerIds.Contains(worker.Id))
            .ToDictionaryAsync(worker => worker.Id, worker => worker.NameEn, cancellationToken);

        var rows = new List<(DateOnly Week, string Worker, int WorkerId, SettledLine Line)>();
        foreach (var workerId in workerIds)
        {
            var settled = PaymentFifo.Settle(
                payable.Where(entry => entry.WorkerId == workerId).Select(entry => entry.Line),
                paid.Where(entry => entry.WorkerId == workerId).Select(entry => entry.Part));
            rows.AddRange(settled
                .Where(line => line.WorkDate >= from && line.WorkDate <= to)
                .Where(line => visible is null || visible.Contains(line.ProjectId))
                .Where(line => projectId is null || line.ProjectId == projectId)
                .Select(line => (WeekStart(line.WorkDate), names.GetValueOrDefault(workerId, $"#{workerId}"), workerId, line)));
        }

        return new ReportData(rows
            .GroupBy(row => (row.Week, row.WorkerId, row.Worker))
            .OrderBy(group => group.Key.Week).ThenBy(group => group.Key.Worker)
            .Select(group => new ReportRow(new Dictionary<string, object?>
            {
                ["week"] = $"{Iso(group.Key.Week)} → {Iso(group.Key.Week.AddDays(6))}",
                ["worker"] = group.Key.Worker,
                ["days"] = group.Sum(row => row.Line.Days),
                ["pay"] = group.Sum(row => row.Line.Net),
                ["paid"] = group.Sum(row => row.Line.Paid),
                ["outstanding"] = group.Sum(row => row.Line.Net - row.Line.Paid),
            }, new ReportLink("worker", group.Key.WorkerId))).ToList(),
            "Weeks start on Saturday. Workers are paid per worker, oldest day first; a weekly payroll batch is not built yet.");
    }

    private static DateOnly WeekStart(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek - (int)DayOfWeek.Saturday + 7) % 7));

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
