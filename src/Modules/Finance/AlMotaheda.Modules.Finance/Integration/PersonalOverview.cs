using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Finance.Integration;

/// <summary>
/// Finance's section of a person's home page (<see cref="IPersonalOverviewSource"/>, section 12.A): the funding
/// requests they asked for and what became of them, cash handed out to a custody they hold and not confirmed yet,
/// and the custody accounts Finance registers for them. A request is never a transfer: a pending funding is listed
/// as pending, and only a received transfer is in any balance.
/// </summary>
internal sealed class FinancePersonalOverview(FinanceDbContext db) : IPersonalOverviewSource
{
    private static readonly PaymentRequestStatus[] OpenStatuses = [PaymentRequestStatus.Submitted, PaymentRequestStatus.Approved, PaymentRequestStatus.SentToBank];

    public string Section => "Funding";

    public async Task<PersonalOverviewSection?> LoadAsync(PersonalOverviewQuery query, CancellationToken cancellationToken)
    {
        var me = query.UserId;
        var items = new List<PersonalOverviewItem>();

        // Project custody fundings asked through Projects: Finance payment requests paid to the holder.
        var fundings = await db.PaymentRequests.AsNoTracking()
            .Where(request => request.SourceType == PaymentSourceTypes.CustodyFunding && request.PayeeType == PayeeTypes.CustodyHolder && request.PayeeId == me)
            .OrderByDescending(request => request.Id)
            .Take(query.Limit)
            .Select(request => new { request.Id, request.Number, request.ProjectId, request.Amount, request.Status, request.RequestedDate, request.PaidOn, request.DecisionNote, request.CloseReason })
            .ToListAsync(cancellationToken);
        items.AddRange(fundings.Select(request => new PersonalOverviewItem(
            "FundingRequest", request.Id, request.ProjectId, request.Status.ToString(), request.PaidOn ?? request.RequestedDate, request.RequestedDate.ToDateTime(TimeOnly.MinValue),
            $"طلب تمويل عهدة {request.Number}", $"Custody funding request {request.Number}", request.Amount, request.DecisionNote ?? request.CloseReason, $"/api/payment-requests/{request.Id}")));

        // Custody transfers the person asked for, and cash handed out to a custody they hold (to confirm).
        var myAccounts = db.CustodyAccounts.Where(account => account.HolderUserId == me).Select(account => account.Id);
        var transfers = await db.CustodyTransfers.AsNoTracking()
            .Where(transfer => transfer.RequestedBy == me
                               || (transfer.DestinationAccountId != null && myAccounts.Contains(transfer.DestinationAccountId.Value) && transfer.Status != CustodyTransferStatus.Received
                                   && transfer.Status != CustodyTransferStatus.Rejected && transfer.Status != CustodyTransferStatus.Cancelled && transfer.Status != CustodyTransferStatus.Reversed))
            .OrderByDescending(transfer => transfer.Id)
            .Take(query.Limit)
            .Select(transfer => new { transfer.Id, transfer.Number, transfer.ProjectId, transfer.Amount, transfer.Status, transfer.Purpose, transfer.RequestedAt, transfer.DisbursedOn, transfer.DecisionNote, transfer.CloseReason })
            .ToListAsync(cancellationToken);
        items.AddRange(transfers.Select(transfer => new PersonalOverviewItem(
            "CustodyTransfer", transfer.Id, transfer.ProjectId, transfer.Status.ToString(), transfer.DisbursedOn ?? DateOnly.FromDateTime(transfer.RequestedAt), transfer.RequestedAt,
            $"تحويل عهدة {transfer.Number}", $"Custody transfer {transfer.Number} ({transfer.Purpose})", transfer.Amount, transfer.DecisionNote ?? transfer.CloseReason, $"/api/custody-transfers/{transfer.Id}")));

        if (items.Count == 0)
        {
            return null;
        }

        var figures = new Dictionary<string, decimal>
        {
            ["pendingFundingCount"] = fundings.Count(request => OpenStatuses.Contains(request.Status)),
            ["pendingFundingAmount"] = fundings.Where(request => OpenStatuses.Contains(request.Status)).Sum(request => request.Amount),
            ["transfersToReceiveCount"] = transfers.Count(transfer => transfer.Status == CustodyTransferStatus.Disbursed),
            ["transfersToReceiveAmount"] = transfers.Where(transfer => transfer.Status == CustodyTransferStatus.Disbursed).Sum(transfer => transfer.Amount),
            ["transfersRequestedCount"] = transfers.Count(transfer => transfer.Status is CustodyTransferStatus.Requested or CustodyTransferStatus.Approved),
        };
        return new PersonalOverviewSection(Section, figures, items.OrderByDescending(item => item.Since).Take(query.Limit).ToList());
    }
}
