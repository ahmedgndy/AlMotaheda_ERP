using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Finance.Common;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Finance.Data.Configurations;
using AlMotaheda.Modules.Finance.Features.Payroll;
using AlMotaheda.Modules.Finance.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Finance.Features.Custody;

/// <summary>
/// Custody transfers: <c>GET /api/custody-transfers</c> and <c>/{id}</c> (custody View: the ones the caller is part
/// of, or every one for a reviewer), <c>POST /api/custody-transfers</c> (custody Create or Approve; treasury-sourced
/// also needs finance Create), then <c>/approve</c>, <c>/reject</c> (payments Approve, never the requester), <c>/disburse</c>,
/// <c>/receive</c>, <c>/cancel</c> and <c>/reverse</c> (custody Approve). See <see cref="CustodyTransferWorkflow"/>.
/// </summary>
internal static class CustodyTransferEndpoints
{
    /// <summary>Where the money goes: an existing account, or one to open on the way (an engineer's project custody, a vehicle).</summary>
    internal sealed record EndpointRequest(string? Kind, string? TreasuryAccount, int? AccountId, string? AccountKind, int? HolderUserId, int? ReferenceId);

    internal sealed record RequestBody(
        EndpointRequest? Source,
        EndpointRequest? Destination,
        decimal? Amount,
        string? Purpose,
        int? PayrollBatchId,
        string? Reference,
        string? Notes,
        int? EvidenceDocumentId,
        string? EvidenceReference,
        string? IdempotencyKey);

    internal sealed record DecisionRequest(string? Note, string? RowVersion);

    internal sealed record MoveRequest(string? On, string? Reference, string? Note, string? RowVersion);

    internal sealed record ReasonRequest(string? Reason, string? RowVersion);

    internal sealed record ListResponse(IReadOnlyList<CustodyTransferView> Items, int Page, int PageSize, int TotalCount, IReadOnlyDictionary<string, int> CountsByStatus);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/custody-transfers", ListAsync)
            .RequirePermission(FinancePermissions.Custody, PermissionAction.View)
            .WithName("ListCustodyTransfers")
            .WithSummary("Custody transfers (status, accountId, payrollBatchId; page, pageSize): yours, or every one for a reviewer, with counts per status");

        api.MapGet("/custody-transfers/{transferId:int}", GetAsync)
            .RequirePermission(FinancePermissions.Custody, PermissionAction.View)
            .WithName("GetCustodyTransfer")
            .WithSummary("One custody transfer with its history");

        api.MapPost("/custody-transfers", RequestAsync)
            .RequireAnyPermission((FinancePermissions.Custody, PermissionAction.Create), (FinancePermissions.Custody, PermissionAction.Approve))
            .WithName("RequestCustodyTransfer")
            .WithSummary("Ask for a transfer: treasury → custody (an advance), custody → custody (a distribution), custody → parent or treasury (a return). Idempotent by idempotencyKey");

        var transfers = api.MapGroup("/custody-transfers/{transferId:int}");

        transfers.MapPost("/approve", ApproveAsync)
            .RequirePermission(FinancePermissions.Payments, PermissionAction.Approve)
            .WithName("ApproveCustodyTransfer")
            .WithSummary("Approve a requested transfer (payments Approve; never the requester): the amount is reserved on the source");

        transfers.MapPost("/reject", RejectAsync)
            .RequirePermission(FinancePermissions.Payments, PermissionAction.Approve)
            .WithName("RejectCustodyTransfer")
            .WithSummary("Reject a requested transfer with a note (payments Approve; never the requester). Final");

        transfers.MapPost("/disburse", DisburseAsync)
            .RequirePermission(FinancePermissions.Custody, PermissionAction.View)
            .WithName("DisburseCustodyTransfer")
            .WithSummary("The cash leaves the source: finance Edit for the treasury, the holder or a custody reviewer for a custody. Posts the source side");

        transfers.MapPost("/receive", ReceiveAsync)
            .RequirePermission(FinancePermissions.Custody, PermissionAction.View)
            .WithName("ReceiveCustodyTransfer")
            .WithSummary("The destination confirms the cash is in hand (its holder, a custody reviewer, finance Edit for the treasury). Posts the destination side");

        transfers.MapPost("/cancel", CancelAsync)
            .RequirePermission(FinancePermissions.Custody, PermissionAction.View)
            .WithName("CancelCustodyTransfer")
            .WithSummary("Cancel a requested or approved transfer with a reason (the requester, a custody reviewer or an Executive). Frees the reservation");

        transfers.MapPost("/reverse", ReverseAsync)
            .RequirePermission(FinancePermissions.Custody, PermissionAction.Approve)
            .WithName("ReverseCustodyTransfer")
            .WithSummary("Reverse a disbursed or received transfer with a reason (custody Approve): opposite movements pointing to the originals");
    }

    internal static async Task<Results<Ok<ListResponse>, ValidationProblem, ProblemHttpResult>> ListAsync(
        HttpContext httpContext,
        FinanceDbContext db,
        CustodyTransferWorkflow workflow,
        CancellationToken cancellationToken,
        string? status = null,
        int? accountId = null,
        int? payrollBatchId = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var wantedStatus = Input.OptionalName<CustodyTransferStatus>(status, errors, "status");
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        var query = Query(db, caller, accountId, payrollBatchId);
        var counts = await query.GroupBy(transfer => transfer.Status).Select(group => new { group.Key, Count = group.Count() }).ToListAsync(cancellationToken);
        if (wantedStatus is { } wanted)
        {
            query = query.Where(transfer => transfer.Status == wanted);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(transfer => transfer.Id).Skip((page - 1) * pageSize).Take(pageSize).Include(transfer => transfer.Events).ToListAsync(cancellationToken);
        var items = new List<CustodyTransferView>();
        foreach (var transfer in rows)
        {
            items.Add(await workflow.ViewAsync(transfer, caller, cancellationToken));
        }

        return TypedResults.Ok(new ListResponse(items, page, pageSize, totalCount, counts.ToDictionary(entry => entry.Key.ToString(), entry => entry.Count)));
    }

    /// <summary>The transfers the caller may see, in SQL: every one for a reviewer, else those they requested or hold an end of.</summary>
    internal static IQueryable<CustodyTransfer> Query(FinanceDbContext db, FinanceCaller caller, int? accountId, int? payrollBatchId)
    {
        var query = db.CustodyTransfers.AsNoTracking();
        if (!caller.SeesAllCustody)
        {
            var me = caller.UserId;
            var treasuryOperator = caller.Can(FinancePermissions.Finance, PermissionAction.Edit);
            query = query.Where(transfer =>
                transfer.RequestedBy == me
                || db.CustodyAccounts.Any(account => account.HolderUserId == me && (account.Id == transfer.SourceAccountId || account.Id == transfer.DestinationAccountId))
                || (treasuryOperator && (transfer.SourceKind == TransferEndpointKind.Treasury || transfer.DestinationKind == TransferEndpointKind.Treasury)));
        }

        if (accountId is { } id)
        {
            query = query.Where(transfer => transfer.SourceAccountId == id || transfer.DestinationAccountId == id);
        }

        if (payrollBatchId is { } batchId)
        {
            query = query.Where(transfer => transfer.PayrollBatchId == batchId);
        }

        return query;
    }

    internal static async Task<Results<Ok<CustodyTransferView>, ProblemHttpResult>> GetAsync(
        int transferId, HttpContext httpContext, FinanceDbContext db, CustodyTransferWorkflow workflow, CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var transfer = await Query(db, caller, null, null).Include(candidate => candidate.Events).SingleOrDefaultAsync(candidate => candidate.Id == transferId, cancellationToken);
        return transfer is null ? FinanceProblems.TransferNotFound() : TypedResults.Ok(await workflow.ViewAsync(transfer, caller, cancellationToken));
    }

    /// <summary>
    /// Requests a transfer. The caller must hold the source (or review custody); a treasury source needs finance
    /// Create. A destination account of kind EngineerProject or Vehicle is opened on the way when it doesn't exist,
    /// under the source account. A payroll distribution is checked against the batch's allocation and the owing
    /// module's entitlement (409 <c>over_distribution</c>). Nothing moves until it is disbursed and received.
    /// </summary>
    internal static async Task<Results<Created<CustodyTransferView>, Ok<CustodyTransferView>, ValidationProblem, ProblemHttpResult>> RequestAsync(
        RequestBody request,
        HttpContext httpContext,
        FinanceDbContext db,
        CustodyLedgers ledgers,
        CustodyTransferWorkflow workflow,
        PayrollBatchRules batches,
        IUserDirectory users,
        CompanyClock clock,
        FinanceLog log,
        CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var amount = NumberInput.RequiredMoney(request.Amount, errors, "amount", "The amount");
        var purpose = Input.Clean(request.Purpose) ?? PaymentPurposes.CustodyAdvance;
        if (!PaymentPurposes.IsInternalTransfer(purpose))
        {
            errors.Add("purpose", "purpose must be CustodyAdvance or CustodyReturn.");
        }

        var reference = Input.OptionalText(request.Reference, CustodyTransferConfiguration.ReferenceMaxLength, errors, "reference", "The reference");
        var notes = Input.OptionalText(request.Notes, CustodyTransferConfiguration.NotesMaxLength, errors, "notes", "Notes");
        var evidence = Input.OptionalText(request.EvidenceReference, CustodyTransferConfiguration.EvidenceMaxLength, errors, "evidenceReference", "The evidence reference");
        if (request.EvidenceDocumentId is < 1)
        {
            errors.Add("evidenceDocumentId", "evidenceDocumentId must be a document id.");
        }

        if (request.PayrollBatchId is < 1)
        {
            errors.Add("payrollBatchId", "payrollBatchId must be a batch id.");
        }

        if (!Guid.TryParse(Input.Clean(request.IdempotencyKey), out var idempotencyKey) || idempotencyKey == Guid.Empty)
        {
            errors.Add("idempotencyKey", "idempotencyKey must be a GUID made by the client for this transfer.");
        }

        var source = TransferEnd.Parse(request.Source, "source", errors);
        var destination = TransferEnd.Parse(request.Destination, "destination", errors);
        if (source?.Kind == TransferEndpointKind.Treasury && destination?.Kind == TransferEndpointKind.Treasury)
        {
            errors.Add("destination", "A transfer between the two treasury accounts is not a custody transfer.");
        }

        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        if (source!.Kind == TransferEndpointKind.Treasury && !caller.Can(FinancePermissions.Finance, PermissionAction.Create))
        {
            return ApiProblem.ForbiddenResult("Asking for cash out of the treasury needs finance Create.");
        }

        if (await db.CustodyTransfers.AsNoTracking().Include(candidate => candidate.Events).SingleOrDefaultAsync(candidate => candidate.IdempotencyKey == idempotencyKey, cancellationToken) is { } repeat)
        {
            return repeat.RequestedBy == caller.UserId && repeat.Amount == amount
                ? TypedResults.Ok(await workflow.ViewAsync(repeat, caller, cancellationToken))
                : FinanceProblems.KeyUsedElsewhere();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The source: an account the caller holds or reviews, active; a treasury account exists.
        CustodyAccount? sourceAccount = null;
        if (source.Kind == TransferEndpointKind.Custody)
        {
            sourceAccount = await db.CustodyAccounts.SingleOrDefaultAsync(account => account.Id == source.AccountId, cancellationToken);
            if (sourceAccount is null || !CustodyAccountViews.MaySee(caller, sourceAccount))
            {
                errors.Add("source.accountId", "The source custody account was not found.");
                return FinanceProblems.Validation(errors);
            }

            if (!CustodyAccountViews.MayOperate(caller, sourceAccount))
            {
                return FinanceProblems.OnlyTheHolder("ask for a transfer out of it");
            }

            if (sourceAccount.Status == CustodyAccountStatus.Closed)
            {
                return FinanceProblems.AccountClosed();
            }

            if (!await CustodyLedgers.TryLockAsync(db, sourceAccount.Id, cancellationToken))
            {
                return FinanceProblems.CustodyIsBusy();
            }
        }

        // The destination: an existing account, or one opened now under the source.
        CustodyAccount? destinationAccount = null;
        if (destination!.Kind == TransferEndpointKind.Custody)
        {
            destinationAccount = await ResolveDestinationAsync(db, ledgers, users, destination, sourceAccount, caller, clock.UtcNow, errors, cancellationToken);
            if (errors.HasErrors)
            {
                return FinanceProblems.Validation(errors);
            }

            if (destinationAccount!.Status == CustodyAccountStatus.Closed)
            {
                return FinanceProblems.AccountClosed();
            }

            if (sourceAccount is not null && sourceAccount.Id == destinationAccount.Id)
            {
                return FinanceProblems.SourceIsDestination();
            }
        }

        // A return goes up: to the parent, the treasury, or any account a reviewer names.
        if (purpose == PaymentPurposes.CustodyReturn && source.Kind == TransferEndpointKind.Treasury)
        {
            errors.Add("purpose", "A return comes out of a custody, never out of the treasury.");
            return FinanceProblems.Validation(errors);
        }

        // Payroll: the batch must be open; a distribution out of its account is checked against its allocation and entitlement.
        var batchId = request.PayrollBatchId;
        if (batchId is { } wantedBatch)
        {
            var batch = await db.PayrollBatches.AsNoTracking().Include(candidate => candidate.Allocations).SingleOrDefaultAsync(candidate => candidate.Id == wantedBatch, cancellationToken);
            if (batch is null)
            {
                errors.Add("payrollBatchId", "The payroll batch was not found.");
                return FinanceProblems.Validation(errors);
            }

            if (batch.Status == PayrollBatchStatus.Closed)
            {
                return FinanceProblems.ClosedBatch();
            }

            if (sourceAccount?.Id == batch.AccountId && destinationAccount is { Kind: CustodyAccountKinds.EngineerProject })
            {
                var check = await batches.CheckDistributionAsync(batch, destinationAccount.ReferenceId!.Value, amount!.Value, null, cancellationToken);
                if (check is { } problem)
                {
                    return problem;
                }
            }
        }

        var now = clock.UtcNow;
        var transfer = new CustodyTransfer
        {
            SourceKind = source.Kind,
            SourceTreasuryAccount = source.Treasury,
            SourceAccountId = sourceAccount?.Id,
            DestinationKind = destination.Kind,
            DestinationTreasuryAccount = destination.Treasury,
            DestinationAccountId = destinationAccount?.Id,
            Amount = amount!.Value,
            Purpose = purpose,
            PayrollBatchId = batchId,
            ProjectId = destinationAccount is { Kind: CustodyAccountKinds.EngineerProject } ? destinationAccount.ReferenceId
                : sourceAccount is { Kind: CustodyAccountKinds.EngineerProject } ? sourceAccount.ReferenceId : null,
            Reference = reference,
            Notes = notes,
            EvidenceDocumentId = request.EvidenceDocumentId,
            EvidenceReference = evidence,
            RequestedAt = now,
            RequestedBy = caller.UserId,
            IdempotencyKey = idempotencyKey,
        };
        transfer.Events.Add(new CustodyTransferEvent { Action = "Request", FromStatus = null, ToStatus = CustodyTransferStatus.Requested, ActorUserId = caller.UserId, At = now, Note = notes });
        db.CustodyTransfers.Add(transfer);

        // The Executives who decide hear of it; never the requester.
        var approvers = await users.ListActiveAsync([AccessRoles.Executive], cancellationToken);
        db.CustodyTransferAlerts.AddRange(approvers.Where(user => user.Id != caller.UserId).Select(user => new CustodyTransferAlert
        {
            Transfer = transfer, RecipientUserId = user.Id, Stage = "Requested", CreatedAt = now,
        }));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, CustodyTransferConfiguration.IdempotencyKeyIndex))
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var first = await db.CustodyTransfers.AsNoTracking().Include(candidate => candidate.Events).SingleAsync(candidate => candidate.IdempotencyKey == idempotencyKey, cancellationToken);
            return first.RequestedBy == caller.UserId ? TypedResults.Ok(await workflow.ViewAsync(first, caller, cancellationToken)) : FinanceProblems.KeyUsedElsewhere();
        }

        await transaction.CommitAsync(cancellationToken);
        log.TransferRequested(caller.UserId, transfer.Id, transfer.Purpose);
        return TypedResults.Created($"/api/custody-transfers/{transfer.Id}", await workflow.ViewAsync(transfer, caller, cancellationToken));
    }

    private static async Task<CustodyAccount?> ResolveDestinationAsync(
        FinanceDbContext db, CustodyLedgers ledgers, IUserDirectory users, TransferEnd destination, CustodyAccount? sourceAccount, FinanceCaller caller,
        DateTime now, ValidationErrors errors, CancellationToken cancellationToken)
    {
        if (destination.AccountId is { } accountId)
        {
            var existing = await db.CustodyAccounts.SingleOrDefaultAsync(account => account.Id == accountId, cancellationToken);
            if (existing is null)
            {
                errors.Add("destination.accountId", "The destination custody account was not found.");
            }

            return existing;
        }

        var kind = destination.AccountKind!;
        var found = await db.CustodyAccounts.SingleOrDefaultAsync(
            account => account.Kind == kind && account.HolderUserId == destination.HolderUserId && account.ReferenceId == destination.ReferenceId && account.Status == CustodyAccountStatus.Active,
            cancellationToken);
        if (found is not null)
        {
            return found;
        }

        if (kind is not (CustodyAccountKinds.EngineerProject or CustodyAccountKinds.Vehicle or CustodyAccountKinds.Accountant))
        {
            errors.Add("destination.accountKind", "Only an engineer's project custody, a vehicle custody or an accountant's custody can be opened on the way.");
            return null;
        }

        if (kind == CustodyAccountKinds.Accountant && !caller.ReviewsCustody)
        {
            errors.Add("destination.accountKind", "An accountant's custody is opened by a custody reviewer first.");
            return null;
        }

        var opened = await CustodyAccountOpening.PrepareAsync(
            db, ledgers, users, kind, destination.HolderUserId, destination.ReferenceId, sourceAccount?.Id, null, null, null, caller.UserId, now, errors, cancellationToken);
        if (opened is null)
        {
            return null;
        }

        db.CustodyAccounts.Add(opened);
        await db.SaveChangesAsync(cancellationToken);
        return opened;
    }

    internal static Task<Results<Ok<CustodyTransferView>, ValidationProblem, ProblemHttpResult>> ApproveAsync(
        int transferId, DecisionRequest body, HttpContext httpContext, CustodyTransferWorkflow workflow, CancellationToken cancellationToken) =>
        DecideAsync(transferId, body.Note, body.RowVersion, httpContext, workflow, noteRequired: false, "Approve",
            (transfer, source, _, caller, note, token) => workflow.ApproveAsync(transfer, source, caller, note, token), cancellationToken);

    internal static Task<Results<Ok<CustodyTransferView>, ValidationProblem, ProblemHttpResult>> RejectAsync(
        int transferId, DecisionRequest body, HttpContext httpContext, CustodyTransferWorkflow workflow, CancellationToken cancellationToken) =>
        DecideAsync(transferId, body.Note, body.RowVersion, httpContext, workflow, noteRequired: true, "Reject",
            (transfer, _, _, caller, note, _) => Task.FromResult(workflow.Reject(transfer, caller, note!)), cancellationToken);

    internal static async Task<Results<Ok<CustodyTransferView>, ValidationProblem, ProblemHttpResult>> DisburseAsync(
        int transferId, MoveRequest body, HttpContext httpContext, CustodyTransferWorkflow workflow, IPermissionCheck permissions, CompanyClock clock, CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var on = Date(body.On, clock, errors, "on");
        var reference = Input.OptionalText(body.Reference, CustodyTransferConfiguration.ReferenceMaxLength, errors, "reference", "The reference");
        var rowVersion = RowVersions.Parse(body.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        // Money leaves: whoever does it must still hold their permission in the database.
        if (!await caller.StillHoldsAsync(permissions, FinancePermissions.Custody, PermissionAction.View, cancellationToken))
        {
            return FinanceProblems.Revoked();
        }

        return await workflow.RunAsync(transferId, rowVersion!, caller, "Disburse", null,
            (transfer, source, destination, token) => workflow.DisburseAsync(transfer, source, destination, caller, on!.Value, reference, token), cancellationToken);
    }

    internal static async Task<Results<Ok<CustodyTransferView>, ValidationProblem, ProblemHttpResult>> ReceiveAsync(
        int transferId, MoveRequest body, HttpContext httpContext, CustodyTransferWorkflow workflow, IPermissionCheck permissions, CompanyClock clock, CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var on = Date(body.On, clock, errors, "on");
        var note = Input.OptionalText(body.Note, CustodyTransferConfiguration.NoteMaxLength, errors, "note", "The note");
        var rowVersion = RowVersions.Parse(body.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        if (!await caller.StillHoldsAsync(permissions, FinancePermissions.Custody, PermissionAction.View, cancellationToken))
        {
            return FinanceProblems.Revoked();
        }

        return await workflow.RunAsync(transferId, rowVersion!, caller, "Receive", null,
            (transfer, _, destination, token) => workflow.ReceiveAsync(transfer, destination, caller, on!.Value, note, token), cancellationToken);
    }

    internal static async Task<Results<Ok<CustodyTransferView>, ValidationProblem, ProblemHttpResult>> CancelAsync(
        int transferId, ReasonRequest body, HttpContext httpContext, CustodyTransferWorkflow workflow, CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var reason = Input.RequiredText(body.Reason, CustodyTransferConfiguration.NoteMaxLength, errors, "reason", "The reason");
        var rowVersion = RowVersions.Parse(body.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        return await workflow.RunAsync(transferId, rowVersion!, caller, "Cancel", null,
            (transfer, _, _, _) => Task.FromResult(workflow.Cancel(transfer, caller, reason!)), cancellationToken);
    }

    internal static async Task<Results<Ok<CustodyTransferView>, ValidationProblem, ProblemHttpResult>> ReverseAsync(
        int transferId, ReasonRequest body, HttpContext httpContext, CustodyTransferWorkflow workflow, CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var reason = Input.RequiredText(body.Reason, CustodyTransferConfiguration.NoteMaxLength, errors, "reason", "The reason");
        var rowVersion = RowVersions.Parse(body.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        return await workflow.RunAsync(transferId, rowVersion!, caller, "Reverse", (FinancePermissions.Custody, PermissionAction.Approve),
            (transfer, source, destination, token) => workflow.ReverseAsync(transfer, source, destination, caller, reason!, token), cancellationToken);
    }

    private static async Task<Results<Ok<CustodyTransferView>, ValidationProblem, ProblemHttpResult>> DecideAsync(
        int transferId,
        string? noteText,
        string? rowVersionText,
        HttpContext httpContext,
        CustodyTransferWorkflow workflow,
        bool noteRequired,
        string actionName,
        Func<CustodyTransfer, CustodyAccount?, CustodyAccount?, FinanceCaller, string?, CancellationToken, Task<TransferOutcome>> step,
        CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = noteRequired
            ? Input.RequiredText(noteText, CustodyTransferConfiguration.NoteMaxLength, errors, "note", "The note")
            : Input.OptionalText(noteText, CustodyTransferConfiguration.NoteMaxLength, errors, "note", "The note");
        var rowVersion = RowVersions.Parse(rowVersionText, errors);
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        return await workflow.RunAsync(transferId, rowVersion!, caller, actionName, (FinancePermissions.Payments, PermissionAction.Approve),
            (transfer, source, destination, token) => step(transfer, source, destination, caller, note, token), cancellationToken);
    }

    /// <summary>The business date of a step: today when not given, never in the future.</summary>
    private static DateOnly? Date(string? value, CompanyClock clock, ValidationErrors errors, string field)
    {
        var date = Input.OptionalDate(value, errors, field, "The date") ?? clock.Today;
        if (date > clock.Today)
        {
            errors.Add(field, "The date can't be in the future.");
        }

        return date;
    }

    /// <summary>One end of a transfer after validation.</summary>
    internal sealed record TransferEnd(TransferEndpointKind Kind, LedgerAccount? Treasury, int? AccountId, string? AccountKind, int? HolderUserId, int? ReferenceId)
    {
        public static TransferEnd? Parse(EndpointRequest? request, string field, ValidationErrors errors)
        {
            if (request is null)
            {
                errors.Add(field, $"{field} is required.");
                return null;
            }

            var kind = Input.OptionalName<TransferEndpointKind>(request.Kind, errors, $"{field}.kind");
            if (kind is null)
            {
                if (!errors.Has($"{field}.kind"))
                {
                    errors.Add($"{field}.kind", $"{field}.kind is required (Treasury or Custody).");
                }

                return null;
            }

            if (kind == TransferEndpointKind.Treasury)
            {
                var treasury = Input.OptionalName<LedgerAccount>(request.TreasuryAccount, errors, $"{field}.treasuryAccount");
                if (treasury is null or LedgerAccount.Custody)
                {
                    errors.Add($"{field}.treasuryAccount", $"{field}.treasuryAccount must be Bank or Cash.");
                    return null;
                }

                return new TransferEnd(kind.Value, treasury, null, null, null, null);
            }

            if (request.AccountId is { } accountId)
            {
                if (accountId < 1)
                {
                    errors.Add($"{field}.accountId", $"{field}.accountId must be an account id.");
                    return null;
                }

                return new TransferEnd(kind.Value, null, accountId, null, null, null);
            }

            var accountKind = Input.Clean(request.AccountKind);
            if (accountKind is null || !CustodyAccountKinds.All.Contains(accountKind))
            {
                errors.Add($"{field}.accountKind", $"Give {field}.accountId, or {field}.accountKind with holderUserId and referenceId.");
                return null;
            }

            if (request.HolderUserId is < 1 || request.ReferenceId is < 1)
            {
                errors.Add($"{field}.holderUserId", "holderUserId and referenceId must be ids.");
                return null;
            }

            return new TransferEnd(kind.Value, null, null, accountKind, request.HolderUserId, request.ReferenceId);
        }
    }
}
