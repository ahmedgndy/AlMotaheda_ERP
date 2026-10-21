using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Office.Common;
using AlMotaheda.Modules.Office.Data;
using AlMotaheda.Modules.Office.Features.Alerts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Office.Integration;

/// <summary>
/// Finance's call when the payment request of a custody funding is paid: the funding becomes Disbursed (with the
/// amount, date and reference Finance paid), in Finance's transaction, and the custodian is told to confirm the cash.
/// Nothing reaches the ledger here: the balance grows only when the custodian confirms receiving it.
/// </summary>
internal sealed class OfficeFundingSource(
    DbContextOptions<OfficeDbContext> options,
    OfficeDbContext db,
    OfficeAlerts alerts,
    CompanyClock clock,
    OfficeLog log) : IPaymentSource
{
    public string SourceType => PaymentSourceTypes.OfficeCustodyFunding;

    public async Task<int> SettleAsync(PaymentSettlement settlement, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        await using var joined = await transaction.JoinAsync(options, OfficeDatabase.Configure, joinedOptions => new OfficeDbContext(joinedOptions), cancellationToken);

        var funding = await joined.FundingRequests.SingleOrDefaultAsync(candidate => candidate.Id == settlement.PayeeId, cancellationToken);
        if (funding is null)
        {
            throw new PaymentNotPayableException($"Office funding request {settlement.PayeeId} doesn't exist.");
        }

        if (!await AppLock.TryAcquireAsync(joined, AppLock.Custody(funding.CustodyId), cancellationToken))
        {
            throw new PaymentNotPayableException("The custody is busy. Try again in a moment.");
        }

        await joined.Entry(funding).ReloadAsync(cancellationToken);
        if (funding.Status != OfficeFundingStatus.Approved || funding.ApprovedAmount != settlement.Amount || funding.PaymentRequestKey != settlement.IdempotencyKey)
        {
            throw new PaymentNotPayableException($"Office funding request {funding.Id} can no longer be paid as requested.");
        }

        var custody = await joined.Custodies.AsNoTracking().SingleAsync(candidate => candidate.Id == funding.CustodyId, cancellationToken);
        var now = clock.UtcNow;
        funding.MoveTo(OfficeFundingStatus.Disbursed);
        funding.DisbursedAmount = settlement.Amount;
        funding.DisbursedOn = settlement.PaidOn;
        funding.DisbursementReference = settlement.Reference;
        funding.DisbursedBy = settlement.ConfirmedBy;
        funding.PaymentRequestId ??= settlement.RequestId;
        funding.PaymentRequestNumber ??= settlement.RequestNumber;
        alerts.Add<OfficeFundsDisbursedAlert>(joined, [custody.HolderUserId], custody.Id, OfficeAttachmentOwners.FundingRequest, funding.Id, now);
        await joined.SaveChangesAsync(cancellationToken);

        log.FundingMoved(settlement.ConfirmedBy, funding.Id, funding.Status.ToString());
        return funding.Id;
    }

    public async Task<IReadOnlyDictionary<int, PayeeName>> PayeeNamesAsync(IReadOnlyCollection<int> payeeIds, CancellationToken cancellationToken) =>
        (await db.FundingRequests.AsNoTracking()
            .Where(funding => payeeIds.Contains(funding.Id))
            .Join(db.Custodies, funding => funding.CustodyId, custody => custody.Id, (funding, custody) => new { funding.Id, custody.NameAr, custody.NameEn })
            .ToListAsync(cancellationToken))
        .ToDictionary(row => row.Id, row => new PayeeName($"عهدة {row.NameAr}", $"Custody {row.NameEn}"));
}
