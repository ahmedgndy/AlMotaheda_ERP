using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Finance.Common;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Finance.Requests;
using AlMotaheda.Modules.Finance.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Finance.Features.Custody;

/// <summary>A transfer as the screens show it. Amounts are <c>null</c> for a caller who may not see money.</summary>
internal sealed record CustodyTransferView(
    int Id,
    string Number,
    string Status,
    string Purpose,
    string SourceKind,
    string? SourceTreasuryAccount,
    int? SourceAccountId,
    string? SourceName,
    string DestinationKind,
    string? DestinationTreasuryAccount,
    int? DestinationAccountId,
    string? DestinationName,
    decimal? Amount,
    int? PayrollBatchId,
    int? ProjectId,
    string? Reference,
    string? Notes,
    int? EvidenceDocumentId,
    string? EvidenceReference,
    DateTime RequestedAt,
    PersonView? RequestedBy,
    DateTime? DecidedAt,
    PersonView? DecidedBy,
    string? DecisionNote,
    DateTime? DisbursedAt,
    PersonView? DisbursedBy,
    DateOnly? DisbursedOn,
    string? DisbursementReference,
    DateTime? ReceivedAt,
    PersonView? ReceivedBy,
    DateOnly? ReceivedOn,
    DateTime? ClosedAt,
    PersonView? ClosedBy,
    string? CloseReason,
    bool Overdrawn,
    IReadOnlyList<PaymentEventView> Events,
    string RowVersion);

/// <summary>The result of one step: nothing to report, or the problem to answer with (nothing is saved).</summary>
internal sealed record TransferOutcome(ProblemHttpResult? Problem)
{
    public static readonly TransferOutcome Done = new((ProblemHttpResult?)null);
}

/// <summary>
/// Runs the steps of a custody transfer the same safe way every time: the caller's permission re-checked in the
/// database for money steps, the source and destination accounts locked (lowest id first), the row version
/// checked, and everything (both ends of the movement, the ledger, the alerts) saved in one transaction.
/// </summary>
/// <remarks>
/// Balances change only here: <b>disburse</b> posts the source side (the treasury's ledger entry, or the source
/// custody's movement out), <b>receive</b> posts the destination side. Nothing is written on request or approval:
/// the amount is only reserved on the source until then. A reversal writes the opposite movements, pointing to the
/// originals, with a mandatory reason; nothing is ever edited or deleted.
/// </remarks>
internal sealed class CustodyTransferWorkflow(
    FinanceDbContext db,
    CustodyLedgers ledgers,
    CustodyBalancePolicy policy,
    IPermissionCheck permissions,
    IUserDirectory users,
    CompanyClock clock,
    FinanceLog log)
{
    public static string LockName(int transferId) => $"AlMotaheda.Finance.Transfer.{transferId}";

    /// <summary>What the transfer may do next, for the caller's own screens.</summary>
    public async Task<Results<Ok<CustodyTransferView>, ValidationProblem, ProblemHttpResult>> RunAsync(
        int transferId,
        byte[] rowVersion,
        FinanceCaller caller,
        string actionName,
        (string Module, PermissionAction Action)? recheck,
        Func<CustodyTransfer, CustodyAccount?, CustodyAccount?, CancellationToken, Task<TransferOutcome>> step,
        CancellationToken cancellationToken)
    {
        if (recheck is { } needs && !await caller.StillHoldsAsync(permissions, needs.Module, needs.Action, cancellationToken))
        {
            log.TransferRefused(caller.UserId, transferId, actionName, "permission no longer held");
            return FinanceProblems.Revoked();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await SqlAppLock.TryAcquireAsync(db, LockName(transferId), cancellationToken))
        {
            return FinanceProblems.TryAgainLater();
        }

        var transfer = await db.CustodyTransfers.Include(candidate => candidate.Events).SingleOrDefaultAsync(candidate => candidate.Id == transferId, cancellationToken);
        if (transfer is null)
        {
            return FinanceProblems.TransferNotFound();
        }

        var source = transfer.SourceAccountId is { } sourceId ? await db.CustodyAccounts.SingleAsync(account => account.Id == sourceId, cancellationToken) : null;
        var destination = transfer.DestinationAccountId is { } destinationId ? await db.CustodyAccounts.SingleAsync(account => account.Id == destinationId, cancellationToken) : null;
        if (!MaySee(caller, transfer, source, destination))
        {
            return FinanceProblems.TransferNotFound();
        }

        // Both accounts are locked, lowest id first, so two transfers between the same accounts never deadlock.
        foreach (var accountId in new[] { transfer.SourceAccountId, transfer.DestinationAccountId }.OfType<int>().Distinct().Order())
        {
            if (!await CustodyLedgers.TryLockAsync(db, accountId, cancellationToken))
            {
                return FinanceProblems.CustodyIsBusy();
            }
        }

        db.Entry(transfer).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion;
        var before = transfer.Status;

        TransferOutcome outcome;
        try
        {
            outcome = await step(transfer, source, destination, cancellationToken);
        }
        catch (CustodyLedgerRefusedException refused)
        {
            await transaction.RollbackAsync(cancellationToken);
            return refused.Code == CustodyLedgerRefusedException.Busy
                ? FinanceProblems.CustodyIsBusy()
                : FinanceProblems.Conflict(refused.Code, refused.Message);
        }

        if (outcome.Problem is { } problem)
        {
            log.TransferRefused(caller.UserId, transferId, actionName, problem.ProblemDetails.Title ?? "refused");
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
        log.TransferMoved(caller.UserId, transferId, actionName, before.ToString(), transfer.Status.ToString());

        var view = await ViewAsync(transfer, caller, cancellationToken);
        return TypedResults.Ok(view);
    }

    /// <summary>The requester, the holders of either end, and whoever sees every custody.</summary>
    public static bool MaySee(FinanceCaller caller, CustodyTransfer transfer, CustodyAccount? source, CustodyAccount? destination) =>
        caller.SeesAllCustody
        || transfer.RequestedBy == caller.UserId
        || source?.HolderUserId == caller.UserId
        || destination?.HolderUserId == caller.UserId
        || (caller.Can(FinancePermissions.Finance, PermissionAction.Edit) && (transfer.SourceKind == TransferEndpointKind.Treasury || transfer.DestinationKind == TransferEndpointKind.Treasury));

    /// <summary>
    /// Approve (payments Approve, never the requester): the source must have the amount available under policy
    /// Block; under Warn an overdrawn transfer is approved and flagged.
    /// </summary>
    public async Task<TransferOutcome> ApproveAsync(CustodyTransfer transfer, CustodyAccount? source, FinanceCaller caller, string? note, CancellationToken cancellationToken)
    {
        if (transfer.RequestedBy == caller.UserId)
        {
            return new TransferOutcome(FinanceProblems.OwnRequest());
        }

        if (!transfer.CanMoveTo(CustodyTransferStatus.Approved))
        {
            return new TransferOutcome(FinanceProblems.WrongStatus(transfer.Status.ToString()));
        }

        if (await AvailableOnSourceAsync(transfer, source, cancellationToken) is { } available && transfer.Amount > available && policy.Blocks)
        {
            return new TransferOutcome(transfer.SourceKind == TransferEndpointKind.Treasury
                ? FinanceProblems.NotEnoughTreasury(available, transfer.Amount)
                : FinanceProblems.NotEnoughCustody(available, transfer.Amount));
        }

        transfer.Approve(caller.UserId, clock.UtcNow, note);
        return TransferOutcome.Done;
    }

    public TransferOutcome Reject(CustodyTransfer transfer, FinanceCaller caller, string note)
    {
        if (transfer.RequestedBy == caller.UserId)
        {
            return new TransferOutcome(FinanceProblems.OwnRequest());
        }

        if (!transfer.CanMoveTo(CustodyTransferStatus.Rejected))
        {
            return new TransferOutcome(FinanceProblems.WrongStatus(transfer.Status.ToString()));
        }

        transfer.Reject(caller.UserId, clock.UtcNow, note);
        return TransferOutcome.Done;
    }

    /// <summary>
    /// Disburse: the cash leaves the source. Treasury: finance Edit (the accountant pays out). Custody: its holder or
    /// a custody reviewer. The source side is posted now; the destination is told it has cash to receive.
    /// </summary>
    public async Task<TransferOutcome> DisburseAsync(
        CustodyTransfer transfer, CustodyAccount? source, CustodyAccount? destination, FinanceCaller caller, DateOnly on, string? reference, CancellationToken cancellationToken)
    {
        if (!transfer.CanMoveTo(CustodyTransferStatus.Disbursed))
        {
            return new TransferOutcome(FinanceProblems.WrongStatus(transfer.Status.ToString()));
        }

        if (transfer.SourceKind == TransferEndpointKind.Treasury)
        {
            if (!caller.Can(FinancePermissions.Finance, PermissionAction.Edit))
            {
                return new TransferOutcome(ApiProblem.ForbiddenResult("Paying cash out of the treasury needs finance Edit."));
            }

            // What the treasury can give, with this transfer's own reservation counted as available to it.
            var available = await AvailableOnSourceAsync(transfer, null, cancellationToken) ?? 0;
            if (policy.Blocks && transfer.Amount > available)
            {
                return new TransferOutcome(FinanceProblems.NotEnoughTreasury(available, transfer.Amount));
            }

            transfer.Disburse(caller.UserId, clock.UtcNow, on, reference);
            db.LedgerEntries.Add(LedgerPosting.Transfer(transfer, transfer.SourceTreasuryAccount!.Value, moneyOut: true, transfer.DestinationAccountId!.Value, on, clock.UtcNow, caller.UserId));
        }
        else
        {
            if (!CustodyAccountViews.MayOperate(caller, source!))
            {
                return new TransferOutcome(FinanceProblems.OnlyTheHolder("hand out its cash"));
            }

            if (await ledgers.ClosedPeriodOfAsync(source!.Id, on, cancellationToken) is { } closed)
            {
                return new TransferOutcome(FinanceProblems.ClosedPeriod(closed));
            }

            // Saved first, so the transfer no longer counts as reserved when the withdrawal checks what is available.
            transfer.Disburse(caller.UserId, clock.UtcNow, on, reference);
            await db.SaveChangesAsync(cancellationToken);
            await ledgers.WithdrawAsync(
                source, CustodyMovementKind.TransferOut,
                new CustodyLedgerWrite(source.ReferenceId ?? 0, source.HolderUserId, transfer.Amount, on, CustodyLedgerSources.Transfer, transfer.Id, reference ?? transfer.Number, transfer.Notes, caller.UserId),
                transfer.Id, null, SharedTransaction.Of(db), cancellationToken);
        }

        // The destination's holder hears there is cash to receive (a vehicle has no holder user).
        if (destination?.HolderUserId is { } holder && holder != caller.UserId)
        {
            db.CustodyTransferAlerts.Add(new CustodyTransferAlert { TransferId = transfer.Id, RecipientUserId = holder, Stage = "Disbursed", CreatedAt = clock.UtcNow });
        }

        return TransferOutcome.Done;
    }

    /// <summary>
    /// Receive: the destination confirms the cash is in hand. Custody: its holder, or a custody reviewer on behalf of
    /// a holder without a login (a vehicle's driver). Treasury (a return): finance Edit. The destination side is posted now.
    /// </summary>
    public async Task<TransferOutcome> ReceiveAsync(
        CustodyTransfer transfer, CustodyAccount? destination, FinanceCaller caller, DateOnly on, string? note, CancellationToken cancellationToken)
    {
        if (!transfer.CanMoveTo(CustodyTransferStatus.Received))
        {
            return new TransferOutcome(FinanceProblems.WrongStatus(transfer.Status.ToString()));
        }

        if (transfer.DestinationKind == TransferEndpointKind.Treasury)
        {
            if (!caller.Can(FinancePermissions.Finance, PermissionAction.Edit))
            {
                return new TransferOutcome(ApiProblem.ForbiddenResult("Receiving cash into the treasury needs finance Edit."));
            }

            transfer.Receive(caller.UserId, clock.UtcNow, on, note);
            db.LedgerEntries.Add(LedgerPosting.Transfer(transfer, transfer.DestinationTreasuryAccount!.Value, moneyOut: false, transfer.SourceAccountId!.Value, on, clock.UtcNow, caller.UserId));
            return TransferOutcome.Done;
        }

        if (!CustodyAccountViews.MayOperate(caller, destination!))
        {
            return new TransferOutcome(FinanceProblems.OnlyTheHolder("confirm receiving its cash"));
        }

        if (destination!.Status == CustodyAccountStatus.Closed)
        {
            return new TransferOutcome(FinanceProblems.AccountClosed());
        }

        if (await ledgers.ClosedPeriodOfAsync(destination.Id, on, cancellationToken) is { } closed)
        {
            return new TransferOutcome(FinanceProblems.ClosedPeriod(closed));
        }

        transfer.Receive(caller.UserId, clock.UtcNow, on, note);
        await db.SaveChangesAsync(cancellationToken);
        await ledgers.ReceiveAsync(
            destination, CustodyMovementKind.TransferIn,
            new CustodyLedgerWrite(destination.ReferenceId ?? 0, destination.HolderUserId, transfer.Amount, on, CustodyLedgerSources.Transfer, transfer.Id, transfer.DisbursementReference ?? transfer.Number, transfer.Notes, caller.UserId),
            transfer.Id, SharedTransaction.Of(db), cancellationToken);
        return TransferOutcome.Done;
    }

    public TransferOutcome Cancel(CustodyTransfer transfer, FinanceCaller caller, string reason)
    {
        if (transfer.RequestedBy != caller.UserId && !caller.IsExecutive && !caller.ReviewsCustody)
        {
            return new TransferOutcome(ApiProblem.ForbiddenResult("Only the person who requested this transfer, a custody reviewer or an Executive can cancel it."));
        }

        if (!transfer.CanMoveTo(CustodyTransferStatus.Cancelled))
        {
            return new TransferOutcome(FinanceProblems.WrongStatus(transfer.Status.ToString()));
        }

        transfer.Cancel(caller.UserId, clock.UtcNow, reason);
        return TransferOutcome.Done;
    }

    /// <summary>
    /// Reverse (custody Approve, with a reason): the opposite of every movement the transfer posted, each pointing
    /// to the original. A disbursed transfer gives the cash back to its source; a received one takes it back from
    /// the destination too. Under policy Block the destination must still hold it.
    /// </summary>
    public async Task<TransferOutcome> ReverseAsync(
        CustodyTransfer transfer, CustodyAccount? source, CustodyAccount? destination, FinanceCaller caller, string reason, CancellationToken cancellationToken)
    {
        if (!transfer.CanMoveTo(CustodyTransferStatus.Reversed))
        {
            return new TransferOutcome(FinanceProblems.WrongStatus(transfer.Status.ToString()));
        }

        var received = transfer.Status == CustodyTransferStatus.Received;
        var now = clock.UtcNow;
        var today = clock.Today;
        transfer.Reverse(caller.UserId, now, reason);
        await db.SaveChangesAsync(cancellationToken);
        var shared = SharedTransaction.Of(db);

        // The destination side first: the cash comes back out of where it went.
        if (received)
        {
            if (transfer.DestinationKind == TransferEndpointKind.Treasury)
            {
                await ReverseLedgerAsync(transfer, moneyIn: true, today, now, caller.UserId, reason, cancellationToken);
            }
            else if (destination!.IsLedgeredByFinance)
            {
                var original = await db.CustodyMovements.SingleAsync(
                    movement => movement.AccountId == destination.Id && movement.SourceType == CustodyLedgerSources.Transfer && movement.SourceId == transfer.Id, cancellationToken);
                await ledgers.ReverseMovementAsync(original, CustodyLedgerSources.TransferReversal, reason, today, caller.UserId, cancellationToken);
            }
            else
            {
                await ledgers.WithdrawAsync(
                    destination, CustodyMovementKind.Reversal,
                    new CustodyLedgerWrite(destination.ReferenceId ?? 0, destination.HolderUserId, transfer.Amount, today, CustodyLedgerSources.TransferReversal, transfer.Id, transfer.Number, reason, caller.UserId),
                    transfer.Id, null, shared, cancellationToken);
            }
        }

        // Then the source gets it back.
        if (transfer.SourceKind == TransferEndpointKind.Treasury)
        {
            await ReverseLedgerAsync(transfer, moneyIn: false, today, now, caller.UserId, reason, cancellationToken);
        }
        else if (source!.IsLedgeredByFinance)
        {
            var original = await db.CustodyMovements.SingleAsync(
                movement => movement.AccountId == source.Id && movement.SourceType == CustodyLedgerSources.Transfer && movement.SourceId == transfer.Id, cancellationToken);
            await ledgers.ReverseMovementAsync(original, CustodyLedgerSources.TransferReversal, reason, today, caller.UserId, cancellationToken);
        }
        else
        {
            await ledgers.ReceiveAsync(
                source, CustodyMovementKind.Reversal,
                new CustodyLedgerWrite(source.ReferenceId ?? 0, source.HolderUserId, transfer.Amount, today, CustodyLedgerSources.TransferReversal, transfer.Id, transfer.Number, reason, caller.UserId),
                transfer.Id, shared, cancellationToken);
        }

        return TransferOutcome.Done;
    }

    private async Task ReverseLedgerAsync(CustodyTransfer transfer, bool moneyIn, DateOnly on, DateTime now, int actorUserId, string reason, CancellationToken cancellationToken)
    {
        var originals = await db.LedgerEntries
            .Where(entry => entry.CustodyTransferId == transfer.Id && entry.ReversesEntryId == null && (moneyIn ? entry.Amount > 0 : entry.Amount < 0))
            .ToListAsync(cancellationToken);
        db.LedgerEntries.AddRange(LedgerPosting.Reversal(originals, on, now, actorUserId));
        await db.SaveChangesAsync(cancellationToken);
        _ = reason;
    }

    /// <summary>What the source can still give: treasury or custody available, with the transfer's own reservation added back.</summary>
    private async Task<decimal?> AvailableOnSourceAsync(CustodyTransfer transfer, CustodyAccount? source, CancellationToken cancellationToken)
    {
        if (transfer.SourceKind == TransferEndpointKind.Treasury)
        {
            var treasury = await db.TreasuryAccounts.AsNoTracking().SingleAsync(account => account.Account == transfer.SourceTreasuryAccount, cancellationToken);
            var figures = await TreasuryQueries.FiguresAsync(db, treasury, cancellationToken);
            return figures.Available + (transfer.ReservesSource ? transfer.Amount : 0);
        }

        var custody = await ledgers.FiguresAsync(source!, cancellationToken);
        return custody.Available + (transfer.ReservesSource ? transfer.Amount : 0);
    }

    public async Task<CustodyTransferView> ViewAsync(CustodyTransfer transfer, FinanceCaller caller, CancellationToken cancellationToken)
    {
        var accountIds = new[] { transfer.SourceAccountId, transfer.DestinationAccountId }.OfType<int>().Distinct().ToList();
        var accounts = await db.CustodyAccounts.AsNoTracking().Where(account => accountIds.Contains(account.Id)).ToDictionaryAsync(account => account.Id, cancellationToken);
        var people = await users.FindAsync(
            new[] { transfer.RequestedBy, transfer.DecidedBy, transfer.DisbursedBy, transfer.ReceivedBy, transfer.ClosedBy }.OfType<int>()
                .Concat(transfer.Events.Select(entry => entry.ActorUserId)).Distinct().ToList(), cancellationToken);
        var seesMoney = caller.SeesAllCustody || caller.SeesAmounts
                        || accounts.Values.Any(account => account.HolderUserId == caller.UserId)
                        || transfer.RequestedBy == caller.UserId;

        var overdrawn = false;
        if (transfer.ReservesSource)
        {
            var available = await AvailableOnSourceAsync(transfer, transfer.SourceAccountId is { } id ? accounts[id] : null, cancellationToken);
            overdrawn = available is { } value && transfer.Amount > value;
        }

        return new CustodyTransferView(
            transfer.Id, transfer.Number, transfer.Status.ToString(), transfer.Purpose,
            transfer.SourceKind.ToString(), transfer.SourceTreasuryAccount?.ToString(), transfer.SourceAccountId, NameOf(transfer.SourceKind, transfer.SourceTreasuryAccount, transfer.SourceAccountId, accounts),
            transfer.DestinationKind.ToString(), transfer.DestinationTreasuryAccount?.ToString(), transfer.DestinationAccountId, NameOf(transfer.DestinationKind, transfer.DestinationTreasuryAccount, transfer.DestinationAccountId, accounts),
            seesMoney ? transfer.Amount : null, transfer.PayrollBatchId, transfer.ProjectId, transfer.Reference, transfer.Notes, transfer.EvidenceDocumentId, transfer.EvidenceReference,
            transfer.RequestedAt, CustodyAccountViews.Person(transfer.RequestedBy, people),
            transfer.DecidedAt, CustodyAccountViews.Person(transfer.DecidedBy, people), transfer.DecisionNote,
            transfer.DisbursedAt, CustodyAccountViews.Person(transfer.DisbursedBy, people), transfer.DisbursedOn, transfer.DisbursementReference,
            transfer.ReceivedAt, CustodyAccountViews.Person(transfer.ReceivedBy, people), transfer.ReceivedOn,
            transfer.ClosedAt, CustodyAccountViews.Person(transfer.ClosedBy, people), transfer.CloseReason,
            overdrawn,
            transfer.Events.OrderBy(entry => entry.Id)
                .Select(entry => new PaymentEventView(entry.Action, entry.FromStatus?.ToString(), entry.ToStatus.ToString(), CustodyAccountViews.Person(entry.ActorUserId, people)!, entry.At, entry.Note))
                .ToList(),
            RowVersions.Format(transfer.RowVersion));
    }

    private static string? NameOf(TransferEndpointKind kind, LedgerAccount? treasury, int? accountId, IReadOnlyDictionary<int, CustodyAccount> accounts) =>
        kind == TransferEndpointKind.Treasury
            ? treasury?.ToString()
            : accountId is { } id && accounts.TryGetValue(id, out var account) ? $"{account.Number} {account.NameEn}" : null;
}
