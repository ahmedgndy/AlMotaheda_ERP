using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Features.Payments;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Integration;

/// <summary>
/// Writes a worker payment when its Finance request is paid, in Finance's transaction and under the worker's lock.
/// Every day is checked again: still the worker's, approved, not void, and still owed at least the planned amount.
/// </summary>
internal sealed class WorkerPaymentSource(
    DbContextOptions<WorkforceDbContext> options,
    WorkforceDbContext db,
    CompanyClock clock,
    WorkforceLog log) : IPaymentSource
{
    public string SourceType => PaymentSourceTypes.WorkerPayment;

    public async Task<int> SettleAsync(PaymentSettlement settlement, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        var workerId = settlement.PayeeId;

        await using var joined = await transaction.JoinAsync(options, WorkforceDatabase.Configure, joinedOptions => new WorkforceDbContext(joinedOptions), cancellationToken);

        if (!await AppLock.TryAcquireAsync(joined, AppLock.Worker(workerId), cancellationToken))
        {
            throw new PaymentNotPayableException("The worker's account is busy. Try again in a moment.");
        }

        var payable = await WorkerPaymentQueries.PayableLines(joined, workerId).ToListAsync(cancellationToken);
        var paidParts = await WorkerPaymentQueries.PaidParts(joined, workerId).ToListAsync(cancellationToken);
        var owed = PaymentFifo.Settle(payable, paidParts).ToDictionary(line => line.LineId, line => line.Outstanding);

        foreach (var line in settlement.Lines)
        {
            if (!owed.TryGetValue(line.RecordId, out var outstanding) || line.Amount > outstanding)
            {
                throw new PaymentNotPayableException($"Day {line.RecordId} is no longer owed to this worker as planned.");
            }
        }

        var payment = new WorkerPayment
        {
            WorkerId = workerId,
            PaymentDate = settlement.PaidOn,
            Amount = settlement.Amount,
            Method = Enum.Parse<WorkerPaymentMethod>(settlement.Method),
            Reference = settlement.Reference,
            Notes = settlement.Notes,
            IdempotencyKey = settlement.IdempotencyKey,
            CreatedAt = clock.UtcNow,
            CreatedBy = settlement.RequestedBy,
        };
        payment.Allocations.AddRange(settlement.Lines.Select(line => new WorkerPaymentAllocation { LineId = line.RecordId, Amount = line.Amount }));
        joined.WorkerPayments.Add(payment);
        await joined.SaveChangesAsync(cancellationToken);

        log.PaymentRecorded(settlement.ConfirmedBy, workerId, payment.Id, payment.Allocations.Count);
        return payment.Id;
    }

    public async Task<IReadOnlyDictionary<int, PayeeName>> PayeeNamesAsync(IReadOnlyCollection<int> payeeIds, CancellationToken cancellationToken) =>
        await db.Workers.AsNoTracking()
            .Where(worker => payeeIds.Contains(worker.Id))
            .ToDictionaryAsync(worker => worker.Id, worker => new PayeeName(worker.NameAr, worker.NameEn), cancellationToken);
}
