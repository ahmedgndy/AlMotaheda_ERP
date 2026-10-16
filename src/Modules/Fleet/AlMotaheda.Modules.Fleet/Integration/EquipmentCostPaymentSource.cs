using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features.Costs;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Integration;

/// <summary>
/// Marks an equipment cost paid when its Finance request is paid, in Finance's transaction and under the project's
/// lock. The cost is checked again: approved, on credit, still unpaid, the same supplier and amount. The cost's id is
/// the payment's id: a credit cost has one payment for its full amount (decision F2).
/// </summary>
internal sealed class EquipmentCostPaymentSource(
    DbContextOptions<FleetDbContext> options,
    ISupplierDirectory suppliers,
    CompanyClock clock,
    FleetLog log) : IPaymentSource
{
    public string SourceType => PaymentSourceTypes.EquipmentCostPayment;

    public async Task<int> SettleAsync(PaymentSettlement settlement, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        var projectId = settlement.ProjectId ?? throw new PaymentNotPayableException("An equipment cost belongs to one project.");
        var line = settlement.Lines.SingleOrDefault(candidate => candidate.RecordType == PaidRecordTypes.EquipmentCost)
                   ?? throw new PaymentNotPayableException("The request doesn't name its equipment cost.");

        await using var joined = await transaction.JoinAsync(options, FleetDatabase.Configure, joinedOptions => new FleetDbContext(joinedOptions), cancellationToken);

        if (!await AppLock.TryAcquireAsync(joined, AppLock.Project(projectId), cancellationToken))
        {
            throw new PaymentNotPayableException("The project is busy. Try again in a moment.");
        }

        var cost = await joined.EquipmentCosts
            .SingleOrDefaultAsync(candidate => candidate.Id == line.RecordId && candidate.ProjectId == projectId, cancellationToken);
        var adjusted = cost is null
            ? 0
            : cost.Amount + (await joined.EquipmentCostAdjustments
                .Where(adjustment => adjustment.CostId == cost.Id && adjustment.Status == AdjustmentStatus.Approved)
                .SumAsync(adjustment => (decimal?)adjustment.Amount, cancellationToken) ?? 0);
        if (cost is null
            || cost.SupplierId != settlement.PayeeId
            || adjusted != settlement.Amount
            || EquipmentCostRules.Pay(cost.Status, cost.Channel, cost.PaidOn is not null) != CostDecision.Allowed)
        {
            throw new PaymentNotPayableException($"Equipment cost {line.RecordId} can no longer be paid as requested.");
        }

        var method = Enum.Parse<PaymentMethod>(settlement.Method);
        cost.PaidOn = settlement.PaidOn;
        cost.PaymentMethod = method;
        cost.PaymentReference = settlement.Reference;
        cost.PaymentRecordedAt = clock.UtcNow;
        cost.PaymentRecordedBy = settlement.RequestedBy;
        await joined.SaveChangesAsync(cancellationToken);

        log.CostPaid(settlement.ConfirmedBy, projectId, cost.Id, method.ToString());
        return cost.Id;
    }

    public async Task<IReadOnlyDictionary<int, PayeeName>> PayeeNamesAsync(IReadOnlyCollection<int> payeeIds, CancellationToken cancellationToken) =>
        (await suppliers.FindAsync(payeeIds, cancellationToken))
            .ToDictionary(entry => entry.Key, entry => new PayeeName(entry.Value.NameAr, entry.Value.NameEn));
}
