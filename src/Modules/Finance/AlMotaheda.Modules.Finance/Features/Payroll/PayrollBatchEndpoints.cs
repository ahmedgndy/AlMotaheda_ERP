using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Finance.Common;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Finance.Data.Configurations;
using AlMotaheda.Modules.Finance.Features.Custody;
using AlMotaheda.Modules.Finance.Requests;
using AlMotaheda.Modules.Finance.Security;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Finance.Features.Payroll;

/// <summary>One project of a batch: what it was allocated, what was distributed to its engineers, and what Workforce says is owed and paid.</summary>
internal sealed record BatchProjectView(
    int ProjectId, string? ProjectCode, string? ProjectNameAr, string? ProjectNameEn, decimal Allocated, decimal EntitlementAtAllocation,
    decimal Distributed, decimal Remaining, decimal Approved, decimal Paid, decimal Held, decimal Outstanding, int Transfers);

/// <summary>One custodian the batch distributed to: what they received, what their custody still holds.</summary>
internal sealed record BatchCustodianView(int AccountId, string AccountNumber, int? HolderUserId, PersonView? Holder, int? ProjectId, decimal Received, decimal Returned, decimal? CustodyPosted, decimal? CustodyPending);

/// <summary>A payroll batch with its reconciliation: funded, distributed, paid, remaining, returned, discrepancy.</summary>
internal sealed record PayrollBatchView(
    int Id,
    string Number,
    string Kind,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    int? SalarySheetId,
    int AccountId,
    string AccountNumber,
    int HolderUserId,
    PersonView? Holder,
    string NameAr,
    string NameEn,
    string Status,
    string? Notes,
    decimal Funded,
    decimal Distributed,
    decimal ReturnedToBatch,
    decimal ReturnedToTreasury,
    decimal PaidFromBatch,
    decimal BatchBalance,
    decimal Discrepancy,
    decimal EntitlementApproved,
    decimal EntitlementPaid,
    decimal EntitlementOutstanding,
    IReadOnlyList<BatchProjectView> Projects,
    IReadOnlyList<BatchCustodianView> Custodians,
    DateTime CreatedAt,
    PersonView? CreatedBy,
    DateTime? ClosedAt,
    PersonView? ClosedBy,
    string? CloseNote,
    string RowVersion);

/// <summary>
/// The rules of a payroll batch, with the owing module's figures: a distribution to a project never goes above the
/// project's allocation in the batch, nor above what Workforce still says is owed on it.
/// </summary>
internal sealed class PayrollBatchRules(FinanceDbContext db, IEnumerable<IPayrollEntitlementSource> sources)
{
    public IPayrollEntitlementSource? SourceOf(string kind) => sources.SingleOrDefault(source => source.Kind == kind);

    public async Task<PayrollEntitlement> EntitlementAsync(PayrollBatch batch, int? projectId, CancellationToken cancellationToken) =>
        SourceOf(batch.Kind) is { } source
            ? await source.EntitlementAsync(new PayrollEntitlementQuery(projectId, batch.PeriodFrom, batch.PeriodTo, batch.SalarySheetId), cancellationToken)
            : PayrollEntitlement.Unknown;

    /// <summary>Transfers out of the batch's account to a project's custodies that still count (not rejected, cancelled or reversed).</summary>
    public IQueryable<CustodyTransfer> Distributions(PayrollBatch batch, int? projectId) =>
        db.CustodyTransfers.AsNoTracking()
            .Where(transfer => transfer.PayrollBatchId == batch.Id && transfer.SourceAccountId == batch.AccountId && transfer.Purpose == PaymentPurposes.CustodyAdvance
                               && transfer.Status != CustodyTransferStatus.Rejected && transfer.Status != CustodyTransferStatus.Cancelled && transfer.Status != CustodyTransferStatus.Reversed
                               && (projectId == null || transfer.ProjectId == projectId));

    /// <summary>The refusal of a new distribution of <paramref name="amount"/> to <paramref name="projectId"/>, or <c>null</c> when it fits.</summary>
    public async Task<ProblemHttpResult?> CheckDistributionAsync(PayrollBatch batch, int projectId, decimal amount, int? exceptTransferId, CancellationToken cancellationToken)
    {
        var allocation = batch.Allocations.SingleOrDefault(candidate => candidate.ProjectId == projectId);
        if (allocation is null)
        {
            return FinanceProblems.OverDistributed(0, amount);
        }

        var distributed = await Distributions(batch, projectId)
            .Where(transfer => exceptTransferId == null || transfer.Id != exceptTransferId)
            .SumAsync(transfer => (decimal?)transfer.Amount, cancellationToken) ?? 0;
        var remainingAllocation = allocation.Amount - distributed;
        if (amount > remainingAllocation)
        {
            return FinanceProblems.OverDistributed(remainingAllocation, amount);
        }

        // Also what the owing module still says is owed (approved − paid): payments since the allocation count.
        var entitlement = await EntitlementAsync(batch, projectId, cancellationToken);
        var owed = entitlement.Approved - entitlement.Paid - distributed;
        return amount > owed ? FinanceProblems.OverDistributed(owed, amount) : null;
    }
}

/// <summary>
/// Payroll batches: <c>GET /api/payroll-batches</c> and <c>/{id}</c> (custody View + custody See money: the
/// reconciliation), <c>POST /api/payroll-batches</c> (custody Approve: the batch and its custody account, with an
/// allocation per project checked against Workforce, or the HR salary sheet), <c>POST /{id}/close</c>.
/// Funding and distributing are custody transfers naming the batch (<c>payrollBatchId</c>).
/// </summary>
internal static class PayrollBatchEndpoints
{
    internal sealed record AllocationRequest(int? ProjectId, decimal? Amount);

    internal sealed record CreateRequest(
        string? Kind, string? PeriodFrom, string? PeriodTo, int? SalarySheetId, int? HolderUserId, int? ParentAccountId, string? NameAr, string? NameEn, string? Notes,
        IReadOnlyList<AllocationRequest>? Allocations);

    internal sealed record CloseRequest(string? Note, string? RowVersion);

    internal sealed record ListResponse(IReadOnlyList<PayrollBatchView> Items);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/payroll-batches", ListAsync)
            .RequirePermission(FinancePermissions.Custody, PermissionAction.View)
            .RequirePermission(FinancePermissions.Custody, PermissionAction.SeeMoney)
            .WithName("ListPayrollBatches")
            .WithSummary("The payroll batches with their reconciliation (status)");

        api.MapGet("/payroll-batches/{batchId:int}", GetAsync)
            .RequirePermission(FinancePermissions.Custody, PermissionAction.View)
            .RequirePermission(FinancePermissions.Custody, PermissionAction.SeeMoney)
            .WithName("GetPayrollBatch")
            .WithSummary("One payroll batch: funded, distributed, paid, remaining per project and custodian, returned, discrepancy");

        api.MapPost("/payroll-batches", CreateAsync)
            .RequirePermission(FinancePermissions.Custody, PermissionAction.Approve)
            .WithName("CreatePayrollBatch")
            .WithSummary("Create a payroll batch and its custody account (custody Approve). Allocations are checked against what Workforce or HR says is owed");

        api.MapPost("/payroll-batches/{batchId:int}/close", CloseAsync)
            .RequirePermission(FinancePermissions.Custody, PermissionAction.Approve)
            .WithName("ClosePayrollBatch")
            .WithSummary("Close a batch (custody Approve): no more transfers name it. Its account must be empty. 409 custody_not_empty");
    }

    internal static async Task<Results<Ok<ListResponse>, ValidationProblem, ProblemHttpResult>> ListAsync(
        HttpContext httpContext, FinanceDbContext db, PayrollBatchViews views, CancellationToken cancellationToken, string? status = null)
    {
        if (FinanceCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var wanted = Input.OptionalName<PayrollBatchStatus>(status, errors, "status");
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        var batches = await db.PayrollBatches.AsNoTracking().Include(batch => batch.Allocations)
            .Where(batch => wanted == null || batch.Status == wanted)
            .OrderByDescending(batch => batch.Id).Take(100).ToListAsync(cancellationToken);
        var items = new List<PayrollBatchView>();
        foreach (var batch in batches)
        {
            items.Add(await views.ViewAsync(batch, cancellationToken));
        }

        return TypedResults.Ok(new ListResponse(items));
    }

    internal static async Task<Results<Ok<PayrollBatchView>, ProblemHttpResult>> GetAsync(
        int batchId, HttpContext httpContext, FinanceDbContext db, PayrollBatchViews views, CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var batch = await db.PayrollBatches.AsNoTracking().Include(candidate => candidate.Allocations).SingleOrDefaultAsync(candidate => candidate.Id == batchId, cancellationToken);
        return batch is null ? FinanceProblems.BatchNotFound() : TypedResults.Ok(await views.ViewAsync(batch, cancellationToken));
    }

    internal static async Task<Results<Created<PayrollBatchView>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateRequest request,
        HttpContext httpContext,
        FinanceDbContext db,
        PayrollBatchRules rules,
        PayrollBatchViews views,
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
        var kind = Input.Clean(request.Kind);
        if (kind is null || !PayrollBatchKinds.All.Contains(kind))
        {
            errors.Add("kind", $"kind must be one of: {string.Join(", ", PayrollBatchKinds.All)}.");
        }

        var periodFrom = Input.RequiredDate(request.PeriodFrom, errors, "periodFrom", "The first day");
        var periodTo = Input.RequiredDate(request.PeriodTo, errors, "periodTo", "The last day");
        if (periodFrom is { } first && periodTo is { } last && last < first)
        {
            errors.Add("periodTo", "The last day can't be before the first.");
        }

        if (request.HolderUserId is < 1)
        {
            errors.Add("holderUserId", "holderUserId must be a user id.");
        }

        if (request.ParentAccountId is < 1)
        {
            errors.Add("parentAccountId", "parentAccountId must be an account id.");
        }

        var nameAr = Input.OptionalText(request.NameAr, PayrollBatchConfiguration.NameMaxLength, errors, "nameAr", "The Arabic name");
        var nameEn = Input.OptionalText(request.NameEn, PayrollBatchConfiguration.NameMaxLength, errors, "nameEn", "The English name");
        var notes = Input.OptionalText(request.Notes, PayrollBatchConfiguration.NotesMaxLength, errors, "notes", "Notes");

        var allocations = new Dictionary<int, decimal>();
        if (kind == PayrollBatchKinds.DailyLabor)
        {
            if (request.Allocations is not { Count: > 0 })
            {
                errors.Add("allocations", "A daily-labor batch needs one allocation per project.");
            }
            else
            {
                foreach (var line in request.Allocations)
                {
                    if (line.ProjectId is not { } projectId || projectId < 1)
                    {
                        errors.Add("allocations", "Every allocation needs a projectId.");
                        break;
                    }

                    if (NumberInput.RequiredMoney(line.Amount, errors, "allocations", "Every allocation amount") is not { } amount)
                    {
                        break;
                    }

                    if (!allocations.TryAdd(projectId, amount))
                    {
                        errors.Add("allocations", $"Project {projectId} is listed twice.");
                        break;
                    }
                }
            }

            if (request.SalarySheetId is not null)
            {
                errors.Add("salarySheetId", "A daily-labor batch names no salary sheet.");
            }
        }
        else if (kind == PayrollBatchKinds.Salaries)
        {
            if (request.SalarySheetId is null or < 1)
            {
                errors.Add("salarySheetId", "A salaries batch needs its salary sheet.");
            }

            if (request.Allocations is { Count: > 0 })
            {
                errors.Add("allocations", "A salaries batch has no project allocations: the sheet is paid as one batch.");
            }
        }

        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        var holderUserId = request.HolderUserId ?? caller.UserId;
        var holder = (await users.FindAsync([holderUserId], cancellationToken)).GetValueOrDefault(holderUserId);
        if (holder is not { IsActive: true })
        {
            errors.Add("holderUserId", "The holder must be an active user.");
            return FinanceProblems.Validation(errors);
        }

        if (request.ParentAccountId is { } parentId && !await db.CustodyAccounts.AnyAsync(account => account.Id == parentId && account.Status == CustodyAccountStatus.Active, cancellationToken))
        {
            errors.Add("parentAccountId", "The parent account was not found or is closed.");
            return FinanceProblems.Validation(errors);
        }

        if (rules.SourceOf(kind!) is null)
        {
            errors.Add("kind", $"No module calculates {kind} payroll.");
            return FinanceProblems.Validation(errors);
        }

        var now = clock.UtcNow;
        var batch = new PayrollBatch
        {
            Kind = kind!,
            PeriodFrom = periodFrom!.Value,
            PeriodTo = periodTo!.Value,
            SalarySheetId = request.SalarySheetId,
            HolderUserId = holderUserId,
            NameAr = nameAr ?? $"دفعة أجور {periodFrom:yyyy-MM-dd} – {periodTo:yyyy-MM-dd}",
            NameEn = nameEn ?? $"Payroll {periodFrom:yyyy-MM-dd} – {periodTo:yyyy-MM-dd}",
            Notes = notes,
            CreatedAt = now,
            CreatedBy = caller.UserId,
        };

        // Each allocation is checked against the owing module: never more than approved − paid on the project.
        foreach (var (projectId, amount) in allocations.OrderBy(pair => pair.Key))
        {
            var entitlement = await rules.EntitlementAsync(batch, projectId, cancellationToken);
            if (!entitlement.Exists)
            {
                errors.Add("allocations", $"Project {projectId} was not found or has no approved labor in the period.");
                return FinanceProblems.Validation(errors);
            }

            var owed = entitlement.Approved - entitlement.Paid;
            if (amount > owed)
            {
                return FinanceProblems.OverDistributed(owed, amount);
            }

            batch.Allocations.Add(new PayrollBatchAllocation { ProjectId = projectId, Amount = amount, EntitlementAtAllocation = owed });
        }

        if (kind == PayrollBatchKinds.Salaries)
        {
            var entitlement = await rules.EntitlementAsync(batch, null, cancellationToken);
            if (!entitlement.Exists)
            {
                errors.Add("salarySheetId", "The salary sheet was not found.");
                return FinanceProblems.Validation(errors);
            }

            if (entitlement.Status != "Approved")
            {
                return FinanceProblems.WrongStatus(entitlement.Status ?? "unknown");
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The batch's own custody account, under the accountant's account when given.
        var account = new CustodyAccount
        {
            Kind = CustodyAccountKinds.PayrollBatch,
            HolderUserId = holderUserId,
            ReferenceId = 0,
            ParentAccountId = request.ParentAccountId,
            NameAr = batch.NameAr,
            NameEn = batch.NameEn,
            OpenedAt = now,
            OpenedBy = caller.UserId,
        };
        db.CustodyAccounts.Add(account);
        await db.SaveChangesAsync(cancellationToken);

        batch.AccountId = account.Id;
        db.PayrollBatches.Add(batch);
        await db.SaveChangesAsync(cancellationToken);

        account.ReferenceId = batch.Id;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, CustodyAccountConfiguration.ActiveIndex) || SqlViolations.IsUnique(exception, PayrollBatchConfiguration.SalarySheetIndex))
        {
            return FinanceProblems.AccountExists();
        }

        await transaction.CommitAsync(cancellationToken);
        log.BatchCreated(caller.UserId, batch.Id, batch.Kind);
        return TypedResults.Created($"/api/payroll-batches/{batch.Id}", await views.ViewAsync(batch, cancellationToken));
    }

    internal static async Task<Results<Ok<PayrollBatchView>, ValidationProblem, ProblemHttpResult>> CloseAsync(
        int batchId,
        CloseRequest request,
        HttpContext httpContext,
        FinanceDbContext db,
        CustodyLedgers ledgers,
        PayrollBatchViews views,
        CompanyClock clock,
        FinanceLog log,
        CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = Input.OptionalText(request.Note, PayrollBatchConfiguration.NotesMaxLength, errors, "note", "The note");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        var batch = await db.PayrollBatches.Include(candidate => candidate.Allocations).SingleOrDefaultAsync(candidate => candidate.Id == batchId, cancellationToken);
        if (batch is null)
        {
            return FinanceProblems.BatchNotFound();
        }

        if (batch.Status == PayrollBatchStatus.Closed)
        {
            return FinanceProblems.ClosedBatch();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await CustodyLedgers.TryLockAsync(db, batch.AccountId, cancellationToken))
        {
            return FinanceProblems.CustodyIsBusy();
        }

        var account = await db.CustodyAccounts.SingleAsync(candidate => candidate.Id == batch.AccountId, cancellationToken);
        var figures = await ledgers.FiguresAsync(account, cancellationToken);
        if (figures.Posted != 0 || figures.Reserved != 0 || figures.InTransit != 0)
        {
            return FinanceProblems.AccountNotEmpty(figures.Posted);
        }

        var now = clock.UtcNow;
        batch.Status = PayrollBatchStatus.Closed;
        batch.ClosedAt = now;
        batch.ClosedBy = caller.UserId;
        batch.CloseNote = note;
        db.Entry(batch).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        account.Status = CustodyAccountStatus.Closed;
        account.ClosedAt = now;
        account.ClosedBy = caller.UserId;
        account.CloseReason = note ?? "Payroll batch closed";

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.BatchClosed(caller.UserId, batch.Id);
        return TypedResults.Ok(await views.ViewAsync(batch, cancellationToken));
    }
}

/// <summary>The reconciliation of a batch, from the transfers, the custody ledgers and the owing module's figures.</summary>
internal sealed class PayrollBatchViews(FinanceDbContext db, CustodyLedgers ledgers, PayrollBatchRules rules, IUserDirectory users, IProjectDirectory projects)
{
    public async Task<PayrollBatchView> ViewAsync(PayrollBatch batch, CancellationToken cancellationToken)
    {
        var account = await db.CustodyAccounts.AsNoTracking().SingleAsync(candidate => candidate.Id == batch.AccountId, cancellationToken);
        var transfers = await db.CustodyTransfers.AsNoTracking()
            .Where(transfer => transfer.PayrollBatchId == batch.Id || transfer.SourceAccountId == batch.AccountId || transfer.DestinationAccountId == batch.AccountId)
            .ToListAsync(cancellationToken);
        bool Counts(CustodyTransfer transfer) => transfer.Status is CustodyTransferStatus.Received or CustodyTransferStatus.Disbursed;

        var funded = transfers.Where(transfer => transfer.DestinationAccountId == batch.AccountId && transfer.Purpose == PaymentPurposes.CustodyAdvance && transfer.Status == CustodyTransferStatus.Received).Sum(transfer => transfer.Amount);
        var distributed = transfers.Where(transfer => transfer.SourceAccountId == batch.AccountId && transfer.Purpose == PaymentPurposes.CustodyAdvance && Counts(transfer)).Sum(transfer => transfer.Amount);
        var returnedToBatch = transfers.Where(transfer => transfer.DestinationAccountId == batch.AccountId && transfer.Purpose == PaymentPurposes.CustodyReturn && transfer.Status == CustodyTransferStatus.Received).Sum(transfer => transfer.Amount);
        var returnedToTreasury = transfers.Where(transfer => transfer.SourceAccountId == batch.AccountId && transfer.Purpose == PaymentPurposes.CustodyReturn && Counts(transfer)).Sum(transfer => transfer.Amount);
        var paidFromBatch = -(await db.CustodyMovements.AsNoTracking()
            .Where(movement => movement.AccountId == batch.AccountId && movement.Kind == CustodyMovementKind.Payment && movement.Status == CustodyMovementStatus.Posted)
            .SumAsync(movement => (decimal?)movement.Amount, cancellationToken) ?? 0);
        var figures = await ledgers.FiguresAsync(account, cancellationToken);
        var adjustments = await db.CustodyMovements.AsNoTracking()
            .Where(movement => movement.AccountId == batch.AccountId && (movement.Kind == CustodyMovementKind.Adjustment || movement.Kind == CustodyMovementKind.Reversal) && movement.Status == CustodyMovementStatus.Posted)
            .SumAsync(movement => (decimal?)movement.Amount, cancellationToken) ?? 0;

        // Every pound that came in is still in the account, went down the chain, came back, went back up, was paid, or was adjusted.
        var discrepancy = funded + returnedToBatch + adjustments - distributed - returnedToTreasury - paidFromBatch - figures.Posted;

        var projectIds = batch.Allocations.Select(allocation => allocation.ProjectId).ToList();
        var names = projectIds.Count == 0 ? new Dictionary<int, ProjectName>() : await projects.FindAsync(projectIds, cancellationToken);
        var projectViews = new List<BatchProjectView>();
        decimal approved = 0, paid = 0, outstanding = 0;
        foreach (var allocation in batch.Allocations.OrderBy(allocation => allocation.ProjectId))
        {
            var entitlement = await rules.EntitlementAsync(batch, allocation.ProjectId, cancellationToken);
            var toProject = transfers.Where(transfer => transfer.SourceAccountId == batch.AccountId && transfer.ProjectId == allocation.ProjectId && transfer.Purpose == PaymentPurposes.CustodyAdvance && Counts(transfer)).ToList();
            var sent = toProject.Sum(transfer => transfer.Amount);
            approved += entitlement.Approved;
            paid += entitlement.Paid;
            outstanding += entitlement.Outstanding;
            var name = names.GetValueOrDefault(allocation.ProjectId);
            projectViews.Add(new BatchProjectView(
                allocation.ProjectId, name?.Code, name?.NameAr, name?.NameEn, allocation.Amount, allocation.EntitlementAtAllocation, sent, allocation.Amount - sent,
                entitlement.Approved, entitlement.Paid, entitlement.Held, entitlement.Outstanding, toProject.Count));
        }

        if (batch.Kind == PayrollBatchKinds.Salaries)
        {
            var entitlement = await rules.EntitlementAsync(batch, null, cancellationToken);
            (approved, paid, outstanding) = (entitlement.Approved, entitlement.Paid, entitlement.Outstanding);
        }

        var custodianIds = transfers.Where(transfer => transfer.SourceAccountId == batch.AccountId && Counts(transfer)).Select(transfer => transfer.DestinationAccountId).OfType<int>()
            .Concat(transfers.Where(transfer => transfer.DestinationAccountId == batch.AccountId && transfer.Purpose == PaymentPurposes.CustodyReturn && transfer.Status == CustodyTransferStatus.Received).Select(transfer => transfer.SourceAccountId).OfType<int>())
            .Distinct().ToList();
        var custodianAccounts = await db.CustodyAccounts.AsNoTracking().Where(candidate => custodianIds.Contains(candidate.Id)).ToListAsync(cancellationToken);
        var people = await users.FindAsync(custodianAccounts.Select(candidate => candidate.HolderUserId).OfType<int>().Append(batch.HolderUserId).Append(batch.CreatedBy).Concat(new[] { batch.ClosedBy }.OfType<int>()).Distinct().ToList(), cancellationToken);
        var custodians = new List<BatchCustodianView>();
        foreach (var custodian in custodianAccounts.OrderBy(candidate => candidate.Id))
        {
            var received = transfers.Where(transfer => transfer.SourceAccountId == batch.AccountId && transfer.DestinationAccountId == custodian.Id && transfer.Purpose == PaymentPurposes.CustodyAdvance && Counts(transfer)).Sum(transfer => transfer.Amount);
            var returned = transfers.Where(transfer => transfer.DestinationAccountId == batch.AccountId && transfer.SourceAccountId == custodian.Id && transfer.Purpose == PaymentPurposes.CustodyReturn && transfer.Status == CustodyTransferStatus.Received).Sum(transfer => transfer.Amount);
            var custodyFigures = await ledgers.FiguresAsync(custodian, cancellationToken);
            custodians.Add(new BatchCustodianView(custodian.Id, custodian.Number, custodian.HolderUserId, CustodyAccountViews.Person(custodian.HolderUserId, people), custodian.ReferenceId, received, returned, custodyFigures.Posted, custodyFigures.Pending));
        }

        return new PayrollBatchView(
            batch.Id, batch.Number, batch.Kind, batch.PeriodFrom, batch.PeriodTo, batch.SalarySheetId, batch.AccountId, account.Number, batch.HolderUserId,
            CustodyAccountViews.Person(batch.HolderUserId, people), batch.NameAr, batch.NameEn, batch.Status.ToString(), batch.Notes,
            funded, distributed, returnedToBatch, returnedToTreasury, paidFromBatch, figures.Posted, discrepancy, approved, paid, outstanding, projectViews, custodians,
            batch.CreatedAt, CustodyAccountViews.Person(batch.CreatedBy, people), batch.ClosedAt, CustodyAccountViews.Person(batch.ClosedBy, people), batch.CloseNote,
            RowVersions.Format(batch.RowVersion));
    }
}
