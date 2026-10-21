using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;

namespace AlMotaheda.Modules.Finance.Requests;

/// <summary>The ledger entries of a paid request and of its reversal, with no database access.</summary>
internal static class LedgerPosting
{
    /// <summary>
    /// One entry per project: the lines say which project each part of the money was for; what no line covers (a
    /// subcontract advance) goes to the request's project. Money out is negative.
    /// </summary>
    public static IReadOnlyList<LedgerEntry> Paid(PaymentRequest request, DateTime now, int confirmedBy)
    {
        // Bank for transfers and cheques; cash from the treasury safe; or cash out of a custody already drawn from it.
        var account = PaymentMethods.GoesThroughBank(request.Method)
            ? LedgerAccount.Bank
            : request.SourceCustodyAccountId is not null ? LedgerAccount.Custody : LedgerAccount.Cash;

        var byProject = request.Lines
            .GroupBy(line => line.ProjectId)
            .Select(group => (ProjectId: (int?)group.Key, Amount: group.Sum(line => line.Amount)))
            .ToList();

        var unallocated = request.Amount - byProject.Sum(part => part.Amount);
        if (unallocated < 0)
        {
            throw new InvalidOperationException($"Request {request.Id}: its lines add up to more than its amount.");
        }

        if (unallocated > 0)
        {
            var same = byProject.FindIndex(part => part.ProjectId == request.ProjectId);
            if (same >= 0)
            {
                byProject[same] = (request.ProjectId, byProject[same].Amount + unallocated);
            }
            else
            {
                byProject.Add((request.ProjectId, unallocated));
            }
        }

        return byProject
            .OrderBy(part => part.ProjectId)
            .Select(part => new LedgerEntry
            {
                EntryDate = request.PaidOn ?? throw new InvalidOperationException("A request is posted only once it has a paid date."),
                Account = account,
                Amount = -part.Amount,
                CounterpartyType = request.PayeeType,
                CounterpartyId = request.PayeeId,
                ProjectId = part.ProjectId,
                Purpose = request.Purpose,
                PaymentRequestId = request.Id,
                CustodyAccountId = account == LedgerAccount.Custody ? request.SourceCustodyAccountId : null,
                SourceType = request.SourceType,
                SourcePaymentId = request.SourcePaymentId,
                CreatedAt = now,
                CreatedBy = confirmedBy,
            })
            .ToList();
    }

    /// <summary>
    /// The treasury side of a custody transfer: money out of Bank or Cash to a custody account (an advance), or back
    /// in from one (a return). Never a payment request, never an expense.
    /// </summary>
    public static LedgerEntry Transfer(CustodyTransfer transfer, LedgerAccount account, bool moneyOut, int counterpartyAccountId, DateOnly on, DateTime now, int actorUserId) =>
        new()
        {
            EntryDate = on,
            Account = account,
            Amount = moneyOut ? -transfer.Amount : transfer.Amount,
            CounterpartyType = PayeeTypes.CustodyAccount,
            CounterpartyId = counterpartyAccountId,
            ProjectId = transfer.ProjectId,
            Purpose = transfer.Purpose,
            CustodyTransferId = transfer.Id,
            SourceType = PaymentSourceTypes.CustodyTransfer,
            CreatedAt = now,
            CreatedBy = actorUserId,
        };

    /// <summary>Entries that cancel <paramref name="originals"/>, dated <paramref name="reversedOn"/>.</summary>
    public static IReadOnlyList<LedgerEntry> Reversal(IEnumerable<LedgerEntry> originals, DateOnly reversedOn, DateTime now, int reversedBy) =>
        originals
            .Where(original => original.ReversesEntryId is null)
            .Select(original => new LedgerEntry
            {
                EntryDate = reversedOn,
                Account = original.Account,
                Amount = -original.Amount,
                CounterpartyType = original.CounterpartyType,
                CounterpartyId = original.CounterpartyId,
                ProjectId = original.ProjectId,
                Purpose = original.Purpose,
                PaymentRequestId = original.PaymentRequestId,
                CustodyTransferId = original.CustodyTransferId,
                CustodyAccountId = original.CustodyAccountId,
                SourceType = original.SourceType,
                SourcePaymentId = original.SourcePaymentId,
                ReversesEntryId = original.Id,
                CreatedAt = now,
                CreatedBy = reversedBy,
            })
            .ToList();
}
