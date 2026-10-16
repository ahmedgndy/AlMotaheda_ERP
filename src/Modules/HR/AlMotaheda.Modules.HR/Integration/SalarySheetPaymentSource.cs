using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.HR.Common;
using AlMotaheda.Modules.HR.Data;
using AlMotaheda.Modules.HR.Features.Payroll;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.HR.Integration;

/// <summary>
/// Marks a salary sheet Paid when its Finance request is paid, in Finance's transaction and under the payroll lock.
/// The sheet is checked again: still Approved, the same total. The sheet's id is the payment's id: a sheet is paid
/// once, as one batch.
/// </summary>
internal sealed class SalarySheetPaymentSource(
    DbContextOptions<HrDbContext> options,
    HrDbContext db,
    CompanyClock clock,
    HrLog log) : IPaymentSource
{
    public string SourceType => PaymentSourceTypes.SalarySheetPayment;

    public async Task<int> SettleAsync(PaymentSettlement settlement, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        await using var joined = await transaction.JoinAsync(options, HrDatabase.Configure, joinedOptions => new HrDbContext(joinedOptions), cancellationToken);

        if (!await AppLock.TryAcquireAsync(joined, AppLock.Payroll, cancellationToken))
        {
            throw new PaymentNotPayableException("The payroll is busy. Try again in a moment.");
        }

        var sheet = await joined.SalarySheets.SingleOrDefaultAsync(candidate => candidate.Id == settlement.PayeeId, cancellationToken);
        if (sheet is null || sheet.Status != SalarySheetStatus.Approved || sheet.Total != settlement.Amount)
        {
            throw new PaymentNotPayableException($"Salary sheet {settlement.PayeeId} can no longer be paid as requested.");
        }

        sheet.MarkPaid(settlement.RequestId, settlement.PaidOn, settlement.ConfirmedBy, clock.UtcNow);
        await joined.SaveChangesAsync(cancellationToken);

        log.SheetPaid(settlement.ConfirmedBy, sheet.Id, settlement.RequestId);
        return sheet.Id;
    }

    public async Task<IReadOnlyDictionary<int, PayeeName>> PayeeNamesAsync(IReadOnlyCollection<int> payeeIds, CancellationToken cancellationToken) =>
        (await db.SalarySheets.AsNoTracking()
            .Where(sheet => payeeIds.Contains(sheet.Id))
            .Select(sheet => new { sheet.Id, sheet.Month })
            .ToListAsync(cancellationToken))
        .ToDictionary(
            sheet => sheet.Id,
            sheet => new PayeeName($"رواتب {PayrollMonths.Format(sheet.Month)}", $"Salaries {PayrollMonths.Format(sheet.Month)}"));
}
