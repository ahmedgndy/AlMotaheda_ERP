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

    public async Task<PaymentRequestRef> SubmitAsync(NewPaymentRequest draft, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        Validate(draft);

        await using var joined = await JoinAsync(transaction, cancellationToken);
        var request = PaymentRequest.Submit(
            draft.SourceType, draft.PayeeType, draft.PayeeId, draft.ProjectId, draft.Purpose, draft.Amount, draft.Method,
            draft.RequestedDate, draft.Reference, draft.Notes, draft.Payload, draft.IdempotencyKey, draft.CreatedBy, clock.UtcNow,
            draft.Lines.Select(line => new PaymentRequestLine
            {
                RecordType = line.RecordType,
                RecordId = line.RecordId,
                ProjectId = line.ProjectId,
                Amount = line.Amount,
            }));
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
        request.Lines.OrderBy(line => line.Id).Select(line => new PaymentLine(line.RecordType, line.RecordId, line.ProjectId, line.Amount)).ToList());

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
