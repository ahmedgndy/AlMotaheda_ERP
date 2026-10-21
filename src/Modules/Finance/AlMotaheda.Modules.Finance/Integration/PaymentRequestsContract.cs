using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Finance.Common;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Finance.Requests;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Finance.Integration;

/// <summary>
/// The Finance module's answer to <see cref="IPaymentRequests"/>. The writes join the source module's transaction
/// (<see cref="SharedTransaction"/>), so a request and the source's checks commit or roll back together.
/// </summary>
internal sealed class PaymentRequestsContract(
    FinanceDbContext db,
    DbContextOptions<FinanceDbContext> options,
    CompanyClock clock,
    FinanceLog log) : IPaymentRequests
{
    public async Task<PaymentRequestRef?> FindByKeyAsync(Guid idempotencyKey, CancellationToken cancellationToken)
    {
        var request = await db.PaymentRequests.AsNoTracking()
            .Include(candidate => candidate.Lines)
            .SingleOrDefaultAsync(candidate => candidate.IdempotencyKey == idempotencyKey, cancellationToken);
        return request is null ? null : Ref(request);
    }

    public async Task<IReadOnlyDictionary<int, decimal>> ReservedAsync(
        string recordType, IReadOnlyCollection<int> recordIds, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        if (recordIds.Count == 0)
        {
            return new Dictionary<int, decimal>();
        }

        await using var joined = await JoinAsync(transaction, cancellationToken);
        var open = PaymentRequestRules.Open;

        return await joined.PaymentRequestLines.AsNoTracking()
            .Where(line => line.RecordType == recordType && recordIds.Contains(line.RecordId))
            .Join(joined.PaymentRequests.Where(request => open.Contains(request.Status)), line => line.RequestId, request => request.Id, (line, _) => line)
            .GroupBy(line => line.RecordId)
            .Select(group => new { RecordId = group.Key, Amount = group.Sum(line => line.Amount) })
            .ToDictionaryAsync(entry => entry.RecordId, entry => entry.Amount, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<int, decimal>> HeldForPayeesAsync(
        string sourceType, string payeeType, IReadOnlyCollection<int> payeeIds, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        if (payeeIds.Count == 0)
        {
            return new Dictionary<int, decimal>();
        }

        await using var joined = await JoinAsync(transaction, cancellationToken);
        var open = PaymentRequestRules.Open;

        return await joined.PaymentRequests.AsNoTracking()
            .Where(request => request.SourceType == sourceType && request.PayeeType == payeeType
                              && payeeIds.Contains(request.PayeeId) && open.Contains(request.Status))
            .GroupBy(request => request.PayeeId)
            .Select(group => new { PayeeId = group.Key, Amount = group.Sum(request => request.Amount) })
            .ToDictionaryAsync(entry => entry.PayeeId, entry => entry.Amount, cancellationToken);
    }

    public async Task<CustodySourceCheck> CheckCustodySourceAsync(int accountId, int payerUserId, CancellationToken cancellationToken)
    {
        var account = await db.CustodyAccounts.AsNoTracking()
            .Where(candidate => candidate.Id == accountId && candidate.Status == CustodyAccountStatus.Active)
            .Select(candidate => new { candidate.Kind, candidate.HolderUserId })
            .SingleOrDefaultAsync(cancellationToken);
        if (account is null)
        {
            return CustodySourceCheck.NotFound;
        }

        if (!CustodyAccountKinds.IsLedgeredByFinance(account.Kind))
        {
            return CustodySourceCheck.NotLedgered;
        }

        return account.HolderUserId == payerUserId ? CustodySourceCheck.Ok : CustodySourceCheck.NotHolder;
    }

    public async Task<PaymentRequestRef> SubmitAsync(NewPaymentRequest draft, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        Validate(draft);

        await using var joined = await JoinAsync(transaction, cancellationToken);
        if (draft.SourceCustodyAccountId is { } accountId)
        {
            // The source module asked CheckCustodySourceAsync first; here it is a bug, not a client error.
            var check = await CheckCustodySourceAsync(accountId, draft.CreatedBy, cancellationToken);
            if (check != CustodySourceCheck.Ok || draft.Method != PaymentMethods.Cash)
            {
                throw new ArgumentException($"Custody account {accountId} can't be the source of this request ({check}, {draft.Method}).", nameof(draft));
            }
        }

        var request = PaymentRequest.Submit(
            draft.SourceType, draft.PayeeType, draft.PayeeId, draft.ProjectId, draft.Purpose, draft.Amount, draft.Method,
            draft.RequestedDate, draft.Reference, draft.Notes, draft.Payload, draft.IdempotencyKey, draft.CreatedBy, clock.UtcNow,
            draft.Lines.Select(line => new PaymentRequestLine
            {
                RecordType = line.RecordType,
                RecordId = line.RecordId,
                ProjectId = line.ProjectId,
                Amount = line.Amount,
            }),
            draft.SourceCustodyAccountId);
        joined.PaymentRequests.Add(request);
        await joined.SaveChangesAsync(cancellationToken);

        log.Submitted(draft.CreatedBy, request.Id, draft.SourceType);
        return Ref(request);
    }

    public async Task ReverseAsync(
        string sourceType, int sourcePaymentId, string reason, int actorUserId, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        await using var joined = await JoinAsync(transaction, cancellationToken);

        var request = await joined.PaymentRequests
            .SingleOrDefaultAsync(
                candidate => candidate.SourceType == sourceType && candidate.SourcePaymentId == sourcePaymentId && candidate.Status == PaymentRequestStatus.Paid,
                cancellationToken)
            ?? throw new InvalidOperationException($"No payment request settled {sourceType} payment {sourcePaymentId}.");

        var now = clock.UtcNow;
        if (request.Reverse(actorUserId, now, reason) != RequestDecision.Allowed)
        {
            throw new InvalidOperationException($"Payment request {request.Id} is {request.Status} and can't be reversed.");
        }

        var originals = await joined.LedgerEntries
            .Where(entry => entry.PaymentRequestId == request.Id && entry.ReversesEntryId == null)
            .ToListAsync(cancellationToken);
        joined.LedgerEntries.AddRange(LedgerPosting.Reversal(originals, clock.Today, now, actorUserId));

        // Paid out of a custody: the custody gets the cash back, as a reversal pointing to the payment's movement.
        if (request.SourceCustodyAccountId is { } accountId)
        {
            if (!await SqlAppLock.TryAcquireAsync(joined, Features.Custody.CustodyLedgers.LockName(accountId), cancellationToken))
            {
                throw new InvalidOperationException("The custody account is busy. Try again in a moment.");
            }

            var movement = await joined.CustodyMovements.SingleOrDefaultAsync(
                candidate => candidate.AccountId == accountId && candidate.SourceType == CustodyMovementSources.PaymentRequest && candidate.SourceId == request.Id, cancellationToken);
            if (movement is not null)
            {
                joined.CustodyMovements.Add(new CustodyMovement
                {
                    AccountId = accountId,
                    EntryDate = clock.Today,
                    Amount = -movement.Amount,
                    Kind = CustodyMovementKind.Reversal,
                    Status = CustodyMovementStatus.Posted,
                    SourceType = CustodyMovementSources.PaymentReversal,
                    SourceId = request.Id,
                    PaymentRequestId = request.Id,
                    ReversesMovementId = movement.Id,
                    Reason = reason,
                    Reference = request.Number,
                    CreatedAt = now,
                    CreatedBy = actorUserId,
                });
            }
        }

        await joined.SaveChangesAsync(cancellationToken);
        log.Reversed(actorUserId, request.Id, sourceType, sourcePaymentId);
    }

    private Task<FinanceDbContext> JoinAsync(SharedTransaction transaction, CancellationToken cancellationToken) =>
        transaction.JoinAsync(options, FinanceDatabase.Configure, joinedOptions => new FinanceDbContext(joinedOptions), cancellationToken);

    private static PaymentRequestRef Ref(PaymentRequest request) => new(
        request.Id,
        request.Number,
        request.Status.ToString(),
        request.SourceType,
        request.PayeeType,
        request.PayeeId,
        request.ProjectId,
        request.Purpose,
        request.Amount,
        request.Method,
        request.RequestedDate,
        request.IdempotencyKey,
        request.Lines.OrderBy(line => line.Id).Select(line => new PaymentLine(line.RecordType, line.RecordId, line.ProjectId, line.Amount)).ToList(),
        request.SourceCustodyAccountId);

    /// <summary>The source modules build requests from validated input; anything else is a bug, so it throws.</summary>
    private static void Validate(NewPaymentRequest draft)
    {
        if (!PaymentSourceTypes.All.Contains(draft.SourceType)
            || !PayeeTypes.All.Contains(draft.PayeeType)
            || !PaymentPurposes.All.Contains(draft.Purpose)
            || !PaymentMethods.All.Contains(draft.Method))
        {
            throw new ArgumentException("Unknown source type, payee type, purpose or method.", nameof(draft));
        }

        if (draft.Amount <= 0 || draft.Lines.Any(line => line.Amount <= 0) || draft.Lines.Sum(line => line.Amount) > draft.Amount)
        {
            throw new ArgumentException("The amount must be positive and at least the sum of the lines.", nameof(draft));
        }
    }
}
