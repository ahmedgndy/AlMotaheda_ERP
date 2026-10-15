using AlMotaheda.Modules.Subcontractors.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Features.Accounts;

/// <summary>The two kinds of subcontract work that can be cost.</summary>
internal enum WorkKind
{
    Measurement,
    Manpower,
}

/// <summary>
/// One measurement or manpower sheet as every calculation sees it: the columns that decide what it costs and what
/// of it is paid. Loaded flat, per project or per contract, by <see cref="WorkQueries"/>.
/// </summary>
/// <param name="Date">The business date: the measurement date, or the last day of a manpower sheet.</param>
/// <param name="Allocated">The allocations of payments that are not voided.</param>
internal sealed record WorkRow(
    WorkKind Kind,
    int Id,
    int ContractId,
    int SubcontractorId,
    int? ContractItemId,
    int? ProjectItemId,
    DateOnly Date,
    decimal Amount,
    WorkStatus Status,
    decimal Allocated)
{
    public bool IsApproved => Status == WorkStatus.Approved;
}

/// <summary>One contract as the calculations see it.</summary>
internal sealed record ContractRow(int Id, int SubcontractorId, decimal Value, decimal RetentionPercent, ContractStatus Status);

/// <summary>One payment as the calculations see it.</summary>
/// <param name="Applied">The sum of its allocations. Meaningless once voided: a voided payment applies nothing.</param>
internal sealed record PaymentRow(int Id, int ContractId, DateOnly PaymentDate, decimal Amount, bool IsVoided, decimal Applied);

/// <summary>The flat queries behind every calculation, as <see cref="IQueryable{T}"/> so tests can prove they translate.</summary>
internal static class WorkQueries
{
    /// <summary>The measurements of a project (or of one contract of it), every status.</summary>
    public static IQueryable<WorkRow> Measurements(SubcontractorsDbContext db, int projectId, int? contractId = null)
    {
        var query = db.ProgressMeasurements.AsNoTracking().Where(measurement => measurement.ContractItem.Contract.ProjectId == projectId);
        if (contractId is { } id)
        {
            query = query.Where(measurement => measurement.ContractItem.ContractId == id);
        }

        return query.Select(measurement => new WorkRow(
            WorkKind.Measurement,
            measurement.Id,
            measurement.ContractItem.ContractId,
            measurement.ContractItem.Contract.SubcontractorId,
            measurement.ContractItemId,
            measurement.ContractItem.ProjectItemId,
            measurement.MeasuredOn,
            measurement.Amount,
            measurement.Status,
            db.SubPaymentAllocations
                .Where(allocation => allocation.MeasurementId == measurement.Id && allocation.Payment.VoidedAt == null)
                .Sum(allocation => allocation.Amount)));
    }

    /// <summary>The manpower sheets of a project (or of one contract of it), every status.</summary>
    public static IQueryable<WorkRow> ManpowerSheets(SubcontractorsDbContext db, int projectId, int? contractId = null)
    {
        var query = db.ManpowerSheets.AsNoTracking().Where(sheet => sheet.Contract.ProjectId == projectId);
        if (contractId is { } id)
        {
            query = query.Where(sheet => sheet.ContractId == id);
        }

        return query.Select(sheet => new WorkRow(
            WorkKind.Manpower,
            sheet.Id,
            sheet.ContractId,
            sheet.Contract.SubcontractorId,
            null,
            sheet.ProjectItemId,
            sheet.ToDate,
            sheet.Amount,
            sheet.Status,
            db.SubPaymentAllocations
                .Where(allocation => allocation.ManpowerSheetId == sheet.Id && allocation.Payment.VoidedAt == null)
                .Sum(allocation => allocation.Amount)));
    }

    /// <summary>The contracts of a project.</summary>
    public static IQueryable<ContractRow> Contracts(SubcontractorsDbContext db, int projectId) =>
        db.SubContracts.AsNoTracking()
            .Where(contract => contract.ProjectId == projectId)
            .Select(contract => new ContractRow(contract.Id, contract.SubcontractorId, contract.Value, contract.RetentionPercent, contract.Status));

    /// <summary>The payments of a project (or of one contract of it), voided ones included.</summary>
    public static IQueryable<PaymentRow> Payments(SubcontractorsDbContext db, int projectId, int? contractId = null)
    {
        var query = db.SubPayments.AsNoTracking().Where(payment => payment.Contract.ProjectId == projectId);
        if (contractId is { } id)
        {
            query = query.Where(payment => payment.ContractId == id);
        }

        return query.Select(payment => new PaymentRow(
            payment.Id,
            payment.ContractId,
            payment.PaymentDate,
            payment.Amount,
            payment.VoidedAt != null,
            payment.Allocations.Sum(allocation => allocation.Amount)));
    }

    /// <summary>All work of a project (or of one contract), both kinds, in two queries.</summary>
    public static async Task<List<WorkRow>> LoadWorkAsync(SubcontractorsDbContext db, int projectId, int? contractId, CancellationToken cancellationToken)
    {
        var work = await Measurements(db, projectId, contractId).ToListAsync(cancellationToken);
        work.AddRange(await ManpowerSheets(db, projectId, contractId).ToListAsync(cancellationToken));
        return work;
    }
}
