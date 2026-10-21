using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Finance.Security;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Finance.Integration;

/// <summary>
/// Finance's part of the work inbox (section 18, one approval queue): payment requests and custody transfers waiting
/// for a decision (payments Approve, never the requester), cash disbursed to the caller's custody waiting to be
/// confirmed (the holder), and the caller's own transfers that were rejected. Company-wide records carry the
/// project only when they fund a project custody. Amounts appear with payments See money or custody See money.
/// </summary>
internal sealed class FinanceInboxSource(FinanceDbContext db) : IWorkInboxSource
{
    public async Task<IReadOnlyList<WorkInboxItem>> ListAsync(WorkInboxQuery query, CancellationToken cancellationToken)
    {
        var me = query.UserId;
        var seesMoney = query.Caller.HasPermission(FinancePermissions.Payments, PermissionAction.SeeMoney) || query.Caller.HasPermission(FinancePermissions.Custody, PermissionAction.SeeMoney);
        var items = new List<WorkInboxItem>();

        if (query.Caller.HasPermission(FinancePermissions.Payments, PermissionAction.Approve))
        {
            var transfers = await db.CustodyTransfers.AsNoTracking()
                .Where(transfer => transfer.Status == CustodyTransferStatus.Requested && transfer.RequestedBy != me)
                .OrderBy(transfer => transfer.RequestedAt)
                .Take(query.Limit)
                .Select(transfer => new { transfer.Id, transfer.Number, transfer.ProjectId, transfer.Amount, transfer.Purpose, transfer.RequestedAt, transfer.RequestedBy, transfer.Notes })
                .ToListAsync(cancellationToken);
            items.AddRange(transfers.Select(transfer => new WorkInboxItem(
                WorkInboxReasons.Review, WorkInboxRecordTypes.CustodyTransfer, transfer.ProjectId, transfer.Id, null, DateOnly.FromDateTime(transfer.RequestedAt),
                transfer.RequestedAt, $"{transfer.Number} · {transfer.Purpose}", seesMoney ? transfer.Amount : null, transfer.RequestedBy, transfer.Notes)));

            var requests = await db.PaymentRequests.AsNoTracking().Include(request => request.Events)
                .Where(request => request.Status == PaymentRequestStatus.Submitted)
                .OrderBy(request => request.RequestedDate).ThenBy(request => request.Id)
                .Take(query.Limit)
                .ToListAsync(cancellationToken);
            items.AddRange(requests
                .Select(request => (Request: request, SubmittedBy: request.Events.OrderBy(step => step.Id).FirstOrDefault()?.ActorUserId))
                .Where(row => row.SubmittedBy != me)
                .Select(row => new WorkInboxItem(
                    WorkInboxReasons.Review, WorkInboxRecordTypes.PaymentRequest, row.Request.ProjectId, row.Request.Id, null, row.Request.RequestedDate,
                    row.Request.Events.OrderBy(step => step.Id).FirstOrDefault()?.At ?? row.Request.RequestedDate.ToDateTime(TimeOnly.MinValue),
                    $"{row.Request.Number} · {row.Request.Purpose} · {row.Request.PayeeType}", seesMoney ? row.Request.Amount : null, row.SubmittedBy, row.Request.Notes)));
        }

        // Cash handed out to a custody the caller holds: they confirm receiving it.
        var receivable = await db.CustodyTransfers.AsNoTracking()
            .Where(transfer => transfer.Status == CustodyTransferStatus.Disbursed && transfer.DestinationAccountId != null
                               && db.CustodyAccounts.Any(account => account.Id == transfer.DestinationAccountId && account.HolderUserId == me))
            .OrderBy(transfer => transfer.DisbursedAt)
            .Take(query.Limit)
            .Select(transfer => new { transfer.Id, transfer.Number, transfer.ProjectId, transfer.Amount, transfer.Purpose, transfer.DisbursedAt, transfer.DisbursedBy, transfer.DisbursementReference })
            .ToListAsync(cancellationToken);
        items.AddRange(receivable.Select(transfer => new WorkInboxItem(
            WorkInboxReasons.Requested, WorkInboxRecordTypes.CustodyTransfer, transfer.ProjectId, transfer.Id, null, DateOnly.FromDateTime(transfer.DisbursedAt ?? DateTime.UtcNow),
            transfer.DisbursedAt ?? DateTime.UtcNow, $"{transfer.Number} · cash to confirm receiving", transfer.Amount, transfer.DisbursedBy, transfer.DisbursementReference)));

        // The caller's own transfers that were rejected: told why, so they can ask again.
        var rejected = await db.CustodyTransfers.AsNoTracking()
            .Where(transfer => transfer.Status == CustodyTransferStatus.Rejected && transfer.RequestedBy == me)
            .OrderByDescending(transfer => transfer.DecidedAt)
            .Take(query.Limit)
            .Select(transfer => new { transfer.Id, transfer.Number, transfer.ProjectId, transfer.Amount, transfer.Purpose, transfer.DecidedAt, transfer.DecidedBy, transfer.DecisionNote })
            .ToListAsync(cancellationToken);
        items.AddRange(rejected.Select(transfer => new WorkInboxItem(
            WorkInboxReasons.Returned, WorkInboxRecordTypes.CustodyTransfer, transfer.ProjectId, transfer.Id, null, DateOnly.FromDateTime(transfer.DecidedAt ?? DateTime.UtcNow),
            transfer.DecidedAt ?? DateTime.UtcNow, $"{transfer.Number} · {transfer.Purpose} rejected", transfer.Amount, transfer.DecidedBy, transfer.DecisionNote)));

        return items;
    }
}
