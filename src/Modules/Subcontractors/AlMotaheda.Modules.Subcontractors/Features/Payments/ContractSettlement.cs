using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Features.Accounts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Features.Payments;

/// <summary>
/// Runs <see cref="SubSettlement"/> for one contract against the database: loads the contract's payments that are
/// not voided (tracked, with their allocations) and its approved work, and adds the new allocations.
/// </summary>
/// <remarks>
/// Called inside the transaction that holds the project lock, after the change that triggered it (a payment, an
/// approval, a void) was saved, so the rows it reads already include that change. The caller saves again.
/// </remarks>
internal static class ContractSettlement
{
    /// <returns>How many allocations were added or grown.</returns>
    public static async Task<int> ApplyAsync(SubcontractorsDbContext db, int contractId, DateTime now, CancellationToken cancellationToken)
    {
        var payments = await SettlementQueries.OpenPayments(db, contractId).ToListAsync(cancellationToken);
        if (payments.Count == 0)
        {
            return 0;
        }

        var work = await SettlementQueries.ApprovedWork(db, contractId).ToListAsync(cancellationToken);

        var existing = payments
            .SelectMany(payment => payment.Allocations.Select(allocation => new SettlementAllocation(payment.Id, KeyOf(allocation), allocation.Amount)))
            .ToList();

        var lines = SubSettlement.Apply(
            payments.Select(payment => new SettlementPayment(payment.Id, payment.PaymentDate, payment.Amount)).ToList(),
            work.Select(piece => new SettlementWork(new WorkKey(piece.Kind, piece.Id), piece.Date, piece.Amount)).ToList(),
            existing);

        foreach (var line in lines)
        {
            var payment = payments.Single(candidate => candidate.Id == line.PaymentId);
            var row = payment.Allocations.FirstOrDefault(allocation => KeyOf(allocation) == line.Work);

            if (row is null)
            {
                payment.Allocations.Add(new SubPaymentAllocation
                {
                    MeasurementId = line.Work.Kind == WorkKind.Measurement ? line.Work.Id : null,
                    ManpowerSheetId = line.Work.Kind == WorkKind.Manpower ? line.Work.Id : null,
                    Amount = line.Amount,
                    CreatedAt = now,
                });
            }
            else
            {
                // The same payment applied again to the same work (after another payment was voided): one row grows.
                row.Amount += line.Amount;
            }
        }

        return lines.Count;
    }

    private static WorkKey KeyOf(SubPaymentAllocation allocation) =>
        allocation.MeasurementId is { } measurementId
            ? new WorkKey(WorkKind.Measurement, measurementId)
            : new WorkKey(WorkKind.Manpower, allocation.ManpowerSheetId!.Value);
}

/// <summary>One piece of approved work, flat, for the settlement.</summary>
/// <remarks>Set by property, not by constructor: EF Core can only build a <c>UNION ALL</c> of member-init projections.</remarks>
internal sealed class ApprovedWorkRow
{
    public WorkKind Kind { get; init; }
    public int Id { get; init; }
    public DateOnly Date { get; init; }
    public decimal Amount { get; init; }
}

/// <summary>The settlement queries, as <see cref="IQueryable{T}"/> so tests can prove they translate.</summary>
internal static class SettlementQueries
{
    /// <summary>The contract's payments that are not voided, tracked, with their allocations.</summary>
    public static IQueryable<SubPayment> OpenPayments(SubcontractorsDbContext db, int contractId) =>
        db.SubPayments
            .Include(payment => payment.Allocations)
            .Where(payment => payment.ContractId == contractId && payment.VoidedAt == null);

    /// <summary>The contract's approved measurements and manpower sheets, both kinds in one query.</summary>
    public static IQueryable<ApprovedWorkRow> ApprovedWork(SubcontractorsDbContext db, int contractId) =>
        db.ProgressMeasurements.AsNoTracking()
            .Where(measurement => measurement.ContractItem.ContractId == contractId && measurement.Status == WorkStatus.Approved)
            .Select(measurement => new ApprovedWorkRow
            {
                Kind = WorkKind.Measurement,
                Id = measurement.Id,
                Date = measurement.MeasuredOn,
                Amount = measurement.Amount,
            })
            .Concat(db.ManpowerSheets.AsNoTracking()
                .Where(sheet => sheet.ContractId == contractId && sheet.Status == WorkStatus.Approved)
                .Select(sheet => new ApprovedWorkRow
                {
                    Kind = WorkKind.Manpower,
                    Id = sheet.Id,
                    Date = sheet.ToDate,
                    Amount = sheet.Amount,
                }));
}
