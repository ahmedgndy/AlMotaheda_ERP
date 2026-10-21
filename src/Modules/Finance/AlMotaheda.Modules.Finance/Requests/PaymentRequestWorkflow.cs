using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Finance.Bank;
using AlMotaheda.Modules.Finance.Common;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Finance.Security;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Finance.Requests;

/// <summary>The result of one step: nothing to report, or the problem to answer with (and nothing is saved).</summary>
internal sealed record StepOutcome(ProblemHttpResult? Problem)
{
    public static readonly StepOutcome Done = new((ProblemHttpResult?)null);

    public static StepOutcome From(RequestDecision decision, PaymentRequest request) => decision switch
    {
        RequestDecision.Allowed => Done,
        RequestDecision.OwnRequest => new(FinanceProblems.OwnRequest()),
        RequestDecision.WrongMethod => new(FinanceProblems.WrongPaymentMethod(request.Method)),
        _ => new(FinanceProblems.WrongStatus(request.Status.ToString())),
    };
}

/// <summary>
/// Runs one step of a payment request (approve, reject, send to the bank, confirm, record a failure, cancel) the same
/// safe way every time: the caller's permission re-checked in the database, the request locked, its row version
/// checked, and everything (including the source module's payment and the ledger when it is paid) saved in one
/// transaction.
/// </summary>
internal sealed class PaymentRequestWorkflow(
    FinanceDbContext db,
    IEnumerable<IPaymentSource> sources,
    IBankPaymentProvider bank,
    IVisibleProjects projects,
    IPermissionCheck permissions,
    PaymentRequestViews views,
    Features.Custody.CustodyLedgers custody,
    CompanyClock clock,
    FinanceLog log)
{
    public static string LockName(int requestId) => $"AlMotaheda.Finance.Request.{requestId}";

    public async Task<Results<Ok<PaymentRequestDetails>, ValidationProblem, ProblemHttpResult>> RunAsync(
        int requestId,
        byte[] rowVersion,
        FinanceCaller caller,
        string actionName,
        (string Module, PermissionAction Action) needs,
        Func<PaymentRequest, CancellationToken, Task<StepOutcome>> step,
        CancellationToken cancellationToken)
    {
        if (!await caller.StillHoldsAsync(permissions, needs.Module, needs.Action, cancellationToken))
        {
            log.Refused(caller.UserId, requestId, actionName, "permission no longer held");
            return FinanceProblems.Revoked();
        }

        var visible = await caller.VisibleProjectsAsync(projects, cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await SqlAppLock.TryAcquireAsync(db, LockName(requestId), cancellationToken))
        {
            return FinanceProblems.TryAgainLater();
        }

        var request = await db.PaymentRequests
            .Include(candidate => candidate.Lines)
            .SingleOrDefaultAsync(candidate => candidate.Id == requestId, cancellationToken);
        if (request is null || !FinanceCaller.Sees(visible, request.ProjectId))
        {
            return FinanceProblems.RequestNotFound();
        }

        db.Entry(request).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion;
        var before = request.Status;

        var outcome = await step(request, cancellationToken);
        if (outcome.Problem is { } problem)
        {
            log.Refused(caller.UserId, requestId, actionName, problem.ProblemDetails.Title ?? "refused");
            return problem;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.Moved(caller.UserId, requestId, actionName, before.ToString(), request.Status.ToString());

        var details = await views.DetailsAsync(requestId, caller.SeesAmounts, cancellationToken);
        return details is null ? FinanceProblems.RequestNotFound() : TypedResults.Ok(details);
    }

    /// <summary>
    /// Sends a bank transfer or cheque to the bank and moves the request to SentToBank. A refused order changes
    /// nothing (the whole step is answered 409).
    /// </summary>
    public async Task<StepOutcome> SendToBankAsync(PaymentRequest request, int userId, bool alsoApprove, string? note, CancellationToken cancellationToken)
    {
        var action = alsoApprove ? RequestAction.ApproveAndPay : RequestAction.SendToBank;
        var check = StepOutcome.From(PaymentRequestRules.Check(action, request.Status, request.Method, request.CreatedBy, userId), request);
        if (check.Problem is not null)
        {
            return check;
        }

        var submission = await bank.SubmitAsync(
            new BankOrder(request.Number, request.Method, request.Amount, request.PayeeType, request.PayeeId, request.RequestedDate),
            cancellationToken);
        if (!submission.Accepted || submission.BankReference is null)
        {
            return new StepOutcome(FinanceProblems.BankRefusedOrder(submission.Message));
        }

        var now = clock.UtcNow;
        return StepOutcome.From(
            alsoApprove
                ? request.ApproveAndPay(userId, now, note, submission.BankReference)
                : request.SendToBank(userId, now, submission.BankReference),
            request);
    }

    /// <summary>
    /// The money left: the source module writes its payment row (in this transaction), the request becomes Paid,
    /// and the ledger entries are added. Anything the source refuses rolls the whole step back.
    /// </summary>
    public async Task<StepOutcome> PayAsync(PaymentRequest request, int userId, DateOnly paidOn, string? reference, CancellationToken cancellationToken)
    {
        var check = StepOutcome.From(
            PaymentRequestRules.Check(RequestAction.ConfirmPayment, request.Status, request.Method, request.CreatedBy, userId), request);
        if (check.Problem is not null)
        {
            return check;
        }

        var source = sources.SingleOrDefault(candidate => candidate.SourceType == request.SourceType)
                     ?? throw new InvalidOperationException($"No module settles {request.SourceType} payments.");

        int sourcePaymentId;
        try
        {
            sourcePaymentId = await source.SettleAsync(
                new PaymentSettlement(
                    request.Id,
                    request.Number,
                    request.PayeeId,
                    request.ProjectId,
                    request.Amount,
                    request.Method,
                    paidOn,
                    // The payment row keeps the reference the requester gave (the invoice or transfer number they know);
                    // the confirmation's reference stays on the request.
                    request.Reference ?? reference ?? request.BankReference,
                    request.Notes,
                    request.IdempotencyKey,
                    request.CreatedBy,
                    userId,
                    request.Lines.Select(line => new PaymentLine(line.RecordType, line.RecordId, line.ProjectId, line.Amount)).ToList(),
                    request.Payload),
                SharedTransaction.Of(db),
                cancellationToken);
        }
        catch (PaymentNotPayableException exception)
        {
            return new StepOutcome(FinanceProblems.NotPayableAnymore(exception.Message));
        }

        var now = clock.UtcNow;
        var paid = StepOutcome.From(request.MarkPaid(userId, now, paidOn, reference, sourcePaymentId), request);
        if (paid.Problem is not null)
        {
            return paid;
        }

        db.LedgerEntries.AddRange(LedgerPosting.Paid(request, now, userId));

        // Paid out of a custody: the custody's own movement, under its lock, within what it holds under policy Block.
        if (request.SourceCustodyAccountId is { } accountId)
        {
            if (!await Features.Custody.CustodyLedgers.TryLockAsync(db, accountId, cancellationToken))
            {
                return new StepOutcome(FinanceProblems.CustodyIsBusy());
            }

            var account = await db.CustodyAccounts.AsNoTracking().SingleAsync(candidate => candidate.Id == accountId, cancellationToken);
            if (account.Status == CustodyAccountStatus.Closed)
            {
                return new StepOutcome(FinanceProblems.AccountClosed());
            }

            var figures = await custody.FiguresAsync(account, cancellationToken);
            // This request's own reservation is what it spends: add it back before comparing.
            var available = figures.Available + request.Amount;
            if (custody.Policy.Blocks && request.Amount > available)
            {
                return new StepOutcome(FinanceProblems.NotEnoughCustody(available, request.Amount));
            }

            if (await custody.ClosedPeriodOfAsync(accountId, paidOn, cancellationToken) is { } closed)
            {
                return new StepOutcome(FinanceProblems.ClosedPeriod(closed));
            }

            db.CustodyMovements.Add(new CustodyMovement
            {
                AccountId = accountId,
                EntryDate = paidOn,
                Amount = -request.Amount,
                Kind = CustodyMovementKind.Payment,
                Status = CustodyMovementStatus.Posted,
                SourceType = CustodyMovementSources.PaymentRequest,
                SourceId = request.Id,
                PaymentRequestId = request.Id,
                Reference = request.Reference ?? request.Number,
                Reason = request.Notes,
                CreatedAt = now,
                CreatedBy = userId,
            });
            log.PaidFromCustody(userId, request.Id, accountId);
        }

        return paid;
    }
}
