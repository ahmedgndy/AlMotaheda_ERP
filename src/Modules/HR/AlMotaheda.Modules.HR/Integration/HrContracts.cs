using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.HR.Common;
using AlMotaheda.Modules.HR.Contracts;
using AlMotaheda.Modules.HR.Data;
using AlMotaheda.Modules.HR.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.HR.Integration;

/// <summary>The HR module's answer to <see cref="IEmployeeDirectory"/>: employees as other modules may know them.</summary>
internal sealed class EmployeeDirectoryContract(HrDbContext db) : IEmployeeDirectory
{
    public async Task<IReadOnlyDictionary<int, EmployeeRef>> FindAsync(IReadOnlyCollection<int> employeeIds, CancellationToken cancellationToken)
    {
        if (employeeIds.Count == 0)
        {
            return new Dictionary<int, EmployeeRef>();
        }

        var ids = employeeIds.Distinct().ToArray();
        return await db.Employees.AsNoTracking()
            .Where(employee => ids.Contains(employee.Id))
            .Select(employee => new EmployeeRef(employee.Id, employee.EmployeeNo, employee.NameAr, employee.NameEn, employee.Kind.ToString(), employee.UserId, employee.Status == EmployeeStatus.Active))
            .ToDictionaryAsync(employee => employee.Id, cancellationToken);
    }

    public Task<EmployeeRef?> FindByUserAsync(int userId, CancellationToken cancellationToken) =>
        db.Employees.AsNoTracking()
            .Where(employee => employee.UserId == userId)
            .Select(employee => new EmployeeRef(employee.Id, employee.EmployeeNo, employee.NameAr, employee.NameEn, employee.Kind.ToString(), employee.UserId, employee.Status == EmployeeStatus.Active))
            .SingleOrDefaultAsync(cancellationToken);
}

/// <summary>
/// The HR module's answer to <see cref="IHrReferrals"/>: a referral opened in the referring module's transaction,
/// once per source record. It changes nothing in payroll.
/// </summary>
internal sealed class HrReferralsContract(DbContextOptions<HrDbContext> options, HrDbContext db, CompanyClock clock, HrLog log) : IHrReferrals
{
    public async Task<int> ReferAsync(HrReferralDraft draft, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        if (!HrReferralSources.All.Contains(draft.SourceType))
        {
            throw new ArgumentException($"Unknown referral source {draft.SourceType}.", nameof(draft));
        }

        await using var joined = await transaction.JoinAsync(options, HrDatabase.Configure, joinedOptions => new HrDbContext(joinedOptions), cancellationToken);
        if (!await joined.Employees.AnyAsync(employee => employee.Id == draft.EmployeeId, cancellationToken))
        {
            throw new ArgumentException($"Employee {draft.EmployeeId} doesn't exist.", nameof(draft));
        }

        var referral = new HrReferral
        {
            SourceType = draft.SourceType,
            SourceId = draft.SourceId,
            EmployeeId = draft.EmployeeId,
            VehicleId = draft.VehicleId,
            Subject = Clip(draft.Subject, HrReferralConfiguration.SubjectMaxLength),
            Details = Clip(draft.Details, HrReferralConfiguration.DetailsMaxLength),
            Amount = draft.Amount is > 0 ? draft.Amount : null,
            ReferenceNo = draft.ReferenceNo is null ? null : Clip(draft.ReferenceNo, HrReferralConfiguration.ReferenceMaxLength),
            ReferredAt = clock.UtcNow,
            ReferredBy = draft.ReferredBy,
        };
        joined.Referrals.Add(referral);
        await joined.SaveChangesAsync(cancellationToken);

        log.Referred(draft.ReferredBy, draft.EmployeeId, referral.Id, draft.SourceType, draft.SourceId);
        return referral.Id;
    }

    public async Task<IReadOnlyDictionary<int, HrReferralState>> StatesAsync(string sourceType, IReadOnlyCollection<int> sourceIds, CancellationToken cancellationToken)
    {
        if (sourceIds.Count == 0)
        {
            return new Dictionary<int, HrReferralState>();
        }

        var ids = sourceIds.Distinct().ToArray();
        return await db.Referrals.AsNoTracking()
            .Where(referral => referral.SourceType == sourceType && ids.Contains(referral.SourceId))
            .Select(referral => new HrReferralState(referral.Id, referral.SourceId, referral.Status.ToString(), referral.Outcome.ToString(), referral.DeductionInputId))
            .ToDictionaryAsync(state => state.SourceId, cancellationToken);
    }

    private static string Clip(string text, int maxLength) => text.Length <= maxLength ? text : text[..maxLength];
}

/// <summary>
/// What a salary sheet is owed, for Finance's payroll batches (<see cref="IPayrollEntitlementSource"/>): the
/// approved sheet's total, paid once Finance paid it, held while a request is open. Finance never recalculates a salary.
/// </summary>
internal sealed class SalaryEntitlementSource(HrDbContext db, IPaymentRequests requests) : IPayrollEntitlementSource
{
    public string Kind => PayrollBatchKinds.Salaries;

    public async Task<PayrollEntitlement> EntitlementAsync(PayrollEntitlementQuery query, CancellationToken cancellationToken)
    {
        if (query.SalarySheetId is not { } sheetId)
        {
            return PayrollEntitlement.Unknown;
        }

        var sheet = await db.SalarySheets.AsNoTracking()
            .Where(candidate => candidate.Id == sheetId)
            .Select(candidate => new { candidate.Status, candidate.Total })
            .SingleOrDefaultAsync(cancellationToken);
        if (sheet is null)
        {
            return PayrollEntitlement.Unknown;
        }

        var approved = sheet.Status is SalarySheetStatus.Approved or SalarySheetStatus.Paid ? sheet.Total : 0;
        var paid = sheet.Status == SalarySheetStatus.Paid ? sheet.Total : 0;

        decimal held = 0;
        if (sheet.Status == SalarySheetStatus.Approved)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var holds = await requests.HeldForPayeesAsync(PaymentSourceTypes.SalarySheetPayment, PayeeTypes.Employees, [sheetId], SharedTransaction.Of(db), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            held = holds.GetValueOrDefault(sheetId);
        }

        return new PayrollEntitlement(true, approved, paid, held, approved - paid - held, sheet.Status.ToString());
    }
}
