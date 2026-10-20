using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Integration;

/// <summary>
/// Writes a supplier payment when its Finance request is paid, in Finance's transaction and under this project's
/// lock. The records are checked again: still approved, on credit, this supplier, and the amount still unpaid.
/// </summary>
internal sealed class SupplierPaymentSource(
    DbContextOptions<ProjectsDbContext> options,
    ProjectsDbContext db,
    Microsoft.Extensions.Options.IOptions<ProjectsOptions> settings,
    CompanyClock clock,
    ProjectsLog log) : IPaymentSource
{
    public string SourceType => PaymentSourceTypes.SupplierPayment;

    public async Task<int> SettleAsync(PaymentSettlement settlement, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        var projectId = settlement.ProjectId ?? throw new PaymentNotPayableException("A supplier payment belongs to one project.");

        await using var joined = await transaction.JoinAsync(options, ProjectsDatabase.Configure, joinedOptions => new ProjectsDbContext(joinedOptions), cancellationToken);

        if (!await ProjectLock.TryAcquireAsync(joined, projectId, cancellationToken))
        {
            throw new PaymentNotPayableException("The project is busy. Try again in a moment.");
        }

        var expenseIds = settlement.Lines.Select(line => line.RecordId).ToArray();
        var targets = await ExpenseQueries.SettlementRows(joined, projectId, expenseIds).ToListAsync(cancellationToken);

        // Price Control: a record whose price case is pending or rejected is not paid, even from Finance's own screens.
        var cases = await Features.PriceControl.PriceExceptionCases.NewestAsync(joined, PriceExceptionSources.ProjectExpense, expenseIds, cancellationToken);
        if (cases.Values.FirstOrDefault(exception => Features.Prices.PriceGate.PaymentBlocked(exception.Status, settings.Value.PriceCheckPolicy)) is { } blocked)
        {
            throw new PaymentNotPayableException($"Record {blocked.SourceId} waits for the Executive's decision on price exception #{blocked.Id} ({blocked.Status}).");
        }

        foreach (var line in settlement.Lines)
        {
            var target = targets.FirstOrDefault(row => row.ExpenseId == line.RecordId);
            if (target is null || target.SupplierId != settlement.PayeeId || !ExpenseRules.CanBePaid(target.Status, target.Channel))
            {
                throw new PaymentNotPayableException($"Record {line.RecordId} can no longer be paid to this supplier.");
            }

            if (line.Amount > target.Amount - target.Allocated)
            {
                throw new PaymentNotPayableException($"Record {line.RecordId} has less left to pay than this payment.");
            }
        }

        var payment = new SupplierPayment
        {
            ProjectId = projectId,
            SupplierId = settlement.PayeeId,
            PaymentDate = settlement.PaidOn,
            Amount = settlement.Amount,
            Method = Enum.Parse<PaymentMethod>(settlement.Method),
            Reference = settlement.Reference,
            Notes = settlement.Notes,
            IdempotencyKey = settlement.IdempotencyKey,
            CreatedAt = clock.UtcNow,
            CreatedBy = settlement.RequestedBy,
        };
        payment.Allocations.AddRange(settlement.Lines.Select(line => new SupplierPaymentAllocation { ExpenseId = line.RecordId, Amount = line.Amount }));
        joined.SupplierPayments.Add(payment);
        await joined.SaveChangesAsync(cancellationToken);

        log.SupplierPaymentRecorded(settlement.ConfirmedBy, projectId, payment.Id, settlement.PayeeId, payment.Allocations.Count);
        return payment.Id;
    }

    public async Task<IReadOnlyDictionary<int, PayeeName>> PayeeNamesAsync(IReadOnlyCollection<int> payeeIds, CancellationToken cancellationToken) =>
        await db.Suppliers.AsNoTracking()
            .Where(supplier => payeeIds.Contains(supplier.Id))
            .ToDictionaryAsync(supplier => supplier.Id, supplier => new PayeeName(supplier.NameAr, supplier.NameEn), cancellationToken);
}
