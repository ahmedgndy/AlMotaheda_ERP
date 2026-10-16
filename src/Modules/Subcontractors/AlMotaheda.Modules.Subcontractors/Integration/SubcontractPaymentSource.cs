using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Subcontractors.Common;
using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Features.Payments;
using AlMotaheda.Modules.Subcontractors.Features.Work;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Integration;

/// <summary>
/// Writes a subcontractor payment when its Finance request is paid, in Finance's transaction and under the project's
/// lock, then settles the contract's oldest approved, unpaid work with it (as a payment always did). The contract
/// value is checked again.
/// </summary>
internal sealed class SubcontractPaymentSource(
    DbContextOptions<SubcontractorsDbContext> options,
    SubcontractorsDbContext db,
    CompanyClock clock,
    SubcontractorsLog log) : IPaymentSource
{
    public string SourceType => PaymentSourceTypes.SubcontractPayment;

    public async Task<int> SettleAsync(PaymentSettlement settlement, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        var projectId = settlement.ProjectId ?? throw new PaymentNotPayableException("A subcontract payment belongs to one project.");
        var line = settlement.Lines.SingleOrDefault(candidate => candidate.RecordType == PaidRecordTypes.SubContract)
                   ?? throw new PaymentNotPayableException("The request doesn't name its contract.");
        var purpose = SubcontractPayload.Read(settlement.Payload);

        await using var joined = await transaction.JoinAsync(
            options, SubcontractorsDatabase.Configure, joinedOptions => new SubcontractorsDbContext(joinedOptions), cancellationToken);

        if (!await AppLock.TryAcquireAsync(joined, AppLock.Project(projectId), cancellationToken))
        {
            throw new PaymentNotPayableException("The project is busy. Try again in a moment.");
        }

        var contract = await joined.SubContracts.AsNoTracking()
            .Where(candidate => candidate.Id == line.RecordId && candidate.ProjectId == projectId && candidate.SubcontractorId == settlement.PayeeId)
            .Select(candidate => new { candidate.Value })
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new PaymentNotPayableException("The contract is no longer on this project for this subcontractor.");

        var paid = await joined.SubPayments
            .Where(payment => payment.ContractId == line.RecordId && payment.VoidedAt == null)
            .SumAsync(payment => payment.Amount, cancellationToken);
        if (!WorkRules.FitsContractValue(contract.Value, paid, settlement.Amount))
        {
            throw new PaymentNotPayableException("The payment no longer fits in the contract value.");
        }

        var now = clock.UtcNow;
        var payment = new SubPayment
        {
            ContractId = line.RecordId,
            PaymentDate = settlement.PaidOn,
            Amount = settlement.Amount,
            Method = Enum.Parse<SubPaymentMethod>(settlement.Method),
            Purpose = purpose,
            Reference = settlement.Reference,
            Notes = settlement.Notes,
            IdempotencyKey = settlement.IdempotencyKey,
            CreatedAt = now,
            CreatedBy = settlement.RequestedBy,
        };
        joined.SubPayments.Add(payment);
        await joined.SaveChangesAsync(cancellationToken);

        // Saved first, so the settlement sees the payment with its id and the oldest-first order is exact.
        var allocations = await ContractSettlement.ApplyAsync(joined, line.RecordId, now, cancellationToken);
        await joined.SaveChangesAsync(cancellationToken);

        log.PaymentRecorded(settlement.ConfirmedBy, projectId, line.RecordId, payment.Id, allocations);
        return payment.Id;
    }

    public async Task<IReadOnlyDictionary<int, PayeeName>> PayeeNamesAsync(IReadOnlyCollection<int> payeeIds, CancellationToken cancellationToken) =>
        await db.Subcontractors.AsNoTracking()
            .Where(company => payeeIds.Contains(company.Id))
            .ToDictionaryAsync(company => company.Id, company => new PayeeName(company.NameAr, company.NameEn), cancellationToken);
}
