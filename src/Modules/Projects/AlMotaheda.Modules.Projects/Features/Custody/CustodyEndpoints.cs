using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Projects.Features.Custody;

/// <summary>Who sees which custody of a project.</summary>
internal static class CustodyAccess
{
    /// <summary>
    /// Every holder's custody on a visible project: custody Approve or See money (the accountant, the Executive), or
    /// projects See money (the project manager). Anyone else with custody View sees only their own.
    /// </summary>
    public static bool SeesAll(ClaimsPrincipal user) =>
        user.HasPermission(CustodyPermissions.Module, PermissionAction.SeeMoney)
        || user.HasPermission(CustodyPermissions.Module, PermissionAction.Approve)
        || user.HasPermission(ProjectPermissions.Module, PermissionAction.SeeMoney);
}

/// <summary>One custody on the project list, with the holder's name.</summary>
internal sealed record CustodySummaryView(
    int HolderUserId,
    string? HolderNameAr,
    string? HolderNameEn,
    decimal Received,
    decimal Returned,
    decimal SpentApproved,
    decimal SpentPending,
    decimal Balance,
    int PendingCount,
    DateOnly? LastMovementDate)
{
    public static CustodySummaryView From(int holderUserId, UserSummary? holder, IReadOnlyList<CustodyMovement> movements)
    {
        var figures = CustodyBalance.Compute(movements);
        return new CustodySummaryView(
            holderUserId, holder?.NameAr, holder?.NameEn, figures.Received, figures.Returned, figures.SpentApproved, figures.SpentPending,
            figures.Balance, figures.PendingCount, movements.Count == 0 ? null : movements.Max(movement => movement.Date));
    }
}

/// <summary>One line of a custody's statement, oldest first, with the balance after it.</summary>
/// <param name="Amount">Positive: cash in (a funding). Negative: cash out (a purchase, an expense, wages, a return).</param>
/// <param name="SourceType">The record behind the line (<c>ProjectExpense</c>, <c>PaymentRequest</c>, <c>PayrollSettlement</c> or <c>CustodyEntry</c>).</param>
internal sealed record CustodyLineView(
    DateOnly Date,
    string Type,
    decimal Amount,
    decimal Balance,
    bool Pending,
    string Status,
    string SourceType,
    int SourceId,
    string? Description,
    int? ProjectItemId,
    string? ProjectItemNameAr,
    string? ProjectItemNameEn,
    int RecordedBy,
    DateTime RecordedAt);

/// <summary>
/// The custody endpoints of a project ("العهدة"). A funding is asked from Finance (the Executive decides, the
/// accountant pays); a return is recorded by a custody approver; purchases and expenses paid from custody are the
/// project records with channel Custody; weekly wages paid in cash come from Workforce through <see cref="ICustodyLedger"/>.
/// </summary>
internal static class CustodyEndpoints
{
    public static void Map(IEndpointRouteBuilder projects)
    {
        projects.MapGet("/{projectId:int}/custody", ListAsync)
            .RequirePermission(CustodyPermissions.Module, PermissionAction.View)
            .WithName("ListProjectCustodies")
            .WithSummary("The cash custodies of a project with their balances (your own only, unless you review custody or see the project's money)");

        projects.MapGet("/{projectId:int}/custody/{holderUserId:int}", GetAsync)
            .RequirePermission(CustodyPermissions.Module, PermissionAction.View)
            .WithName("GetProjectCustody")
            .WithSummary("One person's custody on a project: figures and the statement with its running balance");

        projects.MapPost("/{projectId:int}/custody/fundings", RequestFundingAsync)
            .RequirePermission(CustodyPermissions.Module, PermissionAction.Create)
            .WithName("RequestCustodyFunding")
            .WithSummary("Ask Finance to fund a custody: creates a payment request for the Executive (idempotent by idempotencyKey)");

        projects.MapPost("/{projectId:int}/custody/returns", RecordReturnAsync)
            .RequirePermission(CustodyPermissions.Module, PermissionAction.Approve)
            .WithName("RecordCustodyReturn")
            .WithSummary("Record cash a holder handed back (custody Approve; never the holder; idempotent by idempotencyKey)");
    }

    internal sealed record ListResponse(
        IReadOnlyList<CustodySummaryView> Items,
        decimal Received,
        decimal Returned,
        decimal SpentApproved,
        decimal SpentPending,
        decimal Balance,
        string BalancePolicy,
        bool SeesAll,
        bool CanRequestFunding,
        bool CanRecordReturn);

    internal static async Task<Results<Ok<ListResponse>, ProblemHttpResult>> ListAsync(
        int projectId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        IOptions<ProjectsOptions> options,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var seesAll = CustodyAccess.SeesAll(httpContext.User);
        var movements = await CustodyQueries.MovementsAsync(db, projectId, seesAll ? null : caller.UserId, cancellationToken);
        var byHolder = movements.GroupBy(row => row.HolderUserId).ToDictionary(group => group.Key, group => group.Select(row => row.Movement).ToList());

        // The caller's own custody is listed even before it has a movement, so they can ask for its first funding.
        var canRequest = httpContext.User.HasPermission(CustodyPermissions.Module, PermissionAction.Create);
        if (canRequest && !byHolder.ContainsKey(caller.UserId))
        {
            byHolder[caller.UserId] = [];
        }

        var names = await users.FindAsync(byHolder.Keys.ToList(), cancellationToken);
        var items = byHolder
            .Select(pair => CustodySummaryView.From(pair.Key, names.GetValueOrDefault(pair.Key), pair.Value))
            .OrderByDescending(item => item.HolderUserId == caller.UserId)
            .ThenBy(item => item.HolderNameEn)
            .ToList();
        var all = CustodyBalance.Compute(movements.Select(row => row.Movement));

        return TypedResults.Ok(new ListResponse(
            items, all.Received, all.Returned, all.SpentApproved, all.SpentPending, all.Balance, options.Value.CustodyBalanceCheck, seesAll, canRequest,
            httpContext.User.HasPermission(CustodyPermissions.Module, PermissionAction.Approve)));
    }

    internal sealed record AccountResponse(CustodySummaryView Custody, IReadOnlyList<CustodyLineView> Lines, string BalancePolicy);

    internal static async Task<Results<Ok<AccountResponse>, ProblemHttpResult>> GetAsync(
        int projectId,
        int holderUserId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        IOptions<ProjectsOptions> options,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        // Someone else's custody is not found for a caller who only sees their own: it says nothing about it.
        if (holderUserId != caller.UserId && !CustodyAccess.SeesAll(httpContext.User))
        {
            return CustodyProblems.CustodyNotFound();
        }

        var movements = (await CustodyQueries.MovementsAsync(db, projectId, holderUserId, cancellationToken)).Select(row => row.Movement).ToList();
        var names = await users.FindAsync([holderUserId], cancellationToken);
        if (movements.Count == 0 && holderUserId != caller.UserId && !names.ContainsKey(holderUserId))
        {
            return CustodyProblems.CustodyNotFound();
        }

        var itemIds = movements.Where(movement => movement.ProjectItemId is not null).Select(movement => movement.ProjectItemId!.Value).Distinct().ToList();
        var items = await db.ProjectItems.AsNoTracking()
            .Where(item => item.ProjectId == projectId && itemIds.Contains(item.Id))
            .Select(item => new { item.Id, item.NameAr, item.NameEn })
            .ToDictionaryAsync(item => item.Id, cancellationToken);

        var lines = CustodyBalance.Run(movements)
            .Select(line =>
            {
                var item = line.Movement.ProjectItemId is { } itemId ? items.GetValueOrDefault(itemId) : null;
                var signed = line.Movement.Kind is { } kind && CustodyEntryKinds.IsCashIn(kind) ? line.Movement.Amount : -line.Movement.Amount;
                return new CustodyLineView(
                    line.Movement.Date, line.Movement.Type, signed, line.Balance, line.Movement.Pending, line.Movement.Status, line.Movement.SourceType,
                    line.Movement.SourceId, line.Movement.Description, line.Movement.ProjectItemId, item?.NameAr, item?.NameEn, line.Movement.RecordedBy,
                    line.Movement.RecordedAt);
            })
            .ToList();

        return TypedResults.Ok(new AccountResponse(
            CustodySummaryView.From(holderUserId, names.GetValueOrDefault(holderUserId), movements), lines, options.Value.CustodyBalanceCheck));
    }

    internal sealed record FundingRequest(
        int? HolderUserId, decimal? Amount, string? Method, string? RequestedDate, string? Reference, string? Notes, string? IdempotencyKey);

    /// <summary>
    /// Asks Finance to fund a custody. The holder is the caller unless the caller approves custody; the holder must be
    /// an active user and an active member of the project. Nothing changes in the custody until the money is paid.
    /// </summary>
    internal static async Task<Results<Created<PaymentRequestRef>, Ok<PaymentRequestRef>, ValidationProblem, ProblemHttpResult>> RequestFundingAsync(
        int projectId,
        FundingRequest request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        IPaymentRequests requests,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var amount = NumberInput.RequiredMoney(request.Amount, errors, "amount", "The amount");
        var method = Input.Clean(request.Method) ?? PaymentMethods.Cash;
        if (!PaymentMethods.All.Contains(method))
        {
            errors.Add("method", $"method must be one of: {string.Join(", ", PaymentMethods.All)}.");
        }

        var requestedDate = Input.Clean(request.RequestedDate) is null
            ? clock.Today
            : Input.RequiredDate(request.RequestedDate, errors, "requestedDate", "The date");
        var reference = Input.OptionalText(request.Reference, CustodyEntryConfiguration.ReferenceMaxLength, errors, "reference", "The reference");
        var notes = Input.OptionalText(request.Notes, CustodyEntryConfiguration.TextMaxLength, errors, "notes", "Notes");
        if (!Guid.TryParse(Input.Clean(request.IdempotencyKey), out var idempotencyKey) || idempotencyKey == Guid.Empty)
        {
            errors.Add("idempotencyKey", "idempotencyKey must be a GUID made by the client for this request.");
        }

        if (request.HolderUserId is < 1)
        {
            errors.Add("holderUserId", "holderUserId must be a user id.");
        }

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var holderUserId = request.HolderUserId ?? caller.UserId;
        if (holderUserId != caller.UserId && !httpContext.User.HasPermission(CustodyPermissions.Module, PermissionAction.Approve))
        {
            return CustodyProblems.OnlyOwnCustody();
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return ProjectProblems.CompletedProject();
        }

        if (await requests.FindByKeyAsync(idempotencyKey, cancellationToken) is { } repeat)
        {
            return repeat.SourceType == PaymentSourceTypes.CustodyFunding && repeat.ProjectId == projectId && repeat.PayeeId == holderUserId
                   && repeat.Amount == amount && repeat.Method == method
                ? TypedResults.Ok(repeat)
                : CoreProblems.KeyUsedElsewhere();
        }

        var holder = (await users.FindAsync([holderUserId], cancellationToken)).GetValueOrDefault(holderUserId);
        var member = await db.ProjectMembers.AsNoTracking()
            .AnyAsync(row => row.ProjectId == projectId && row.UserId == holderUserId && row.EndDate == null, cancellationToken);
        if (holder is not { IsActive: true } || !member)
        {
            errors.Add("holderUserId", "The holder must be an active member of this project.");
            return ProjectProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        PaymentRequestRef created;
        try
        {
            created = await requests.SubmitAsync(
                new NewPaymentRequest(
                    PaymentSourceTypes.CustodyFunding,
                    PayeeTypes.CustodyHolder,
                    holderUserId,
                    projectId,
                    PaymentPurposes.CustodyAdvance,
                    amount!.Value,
                    method,
                    requestedDate!.Value,
                    reference,
                    notes,
                    idempotencyKey,
                    caller.UserId,
                    []),
                SharedTransaction.Of(db),
                cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, PaymentRequestIndexes.IdempotencyKey))
        {
            await transaction.RollbackAsync(cancellationToken);
            return await requests.FindByKeyAsync(idempotencyKey, cancellationToken) is { } other && other.PayeeId == holderUserId
                ? TypedResults.Ok(other)
                : CoreProblems.KeyUsedElsewhere();
        }

        await transaction.CommitAsync(cancellationToken);
        log.CustodyFundingRequested(caller.UserId, projectId, holderUserId, created.Id);
        return TypedResults.Created($"/api/payment-requests/{created.Id}", created);
    }

    internal sealed record ReturnRequest(
        int? HolderUserId, decimal? Amount, string? ReturnDate, string? Method, string? Reference, string? Notes, string? IdempotencyKey);

    internal sealed record ReturnView(int Id, int HolderUserId, decimal Amount, DateOnly ReturnDate, string Method, decimal BalanceAfter);

    /// <summary>
    /// Cash handed back to the company. Recorded by a custody approver who is not the holder, under the custody lock,
    /// and never more than the custody holds.
    /// </summary>
    internal static async Task<Results<Created<ReturnView>, Ok<ReturnView>, ValidationProblem, ProblemHttpResult>> RecordReturnAsync(
        int projectId,
        ReturnRequest request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        if (request.HolderUserId is null or < 1)
        {
            errors.Add("holderUserId", "holderUserId is required.");
        }

        var amount = NumberInput.RequiredMoney(request.Amount, errors, "amount", "The amount");
        var returnDate = Input.RequiredDate(request.ReturnDate, errors, "returnDate", "The return date");
        if (returnDate > clock.Today)
        {
            errors.Add("returnDate", "The return date can't be in the future.");
        }

        var method = Input.Clean(request.Method) ?? PaymentMethods.Cash;
        if (!PaymentMethods.All.Contains(method))
        {
            errors.Add("method", $"method must be one of: {string.Join(", ", PaymentMethods.All)}.");
        }

        var reference = Input.OptionalText(request.Reference, CustodyEntryConfiguration.ReferenceMaxLength, errors, "reference", "The reference");
        var notes = Input.OptionalText(request.Notes, CustodyEntryConfiguration.TextMaxLength, errors, "notes", "Notes");
        if (!Guid.TryParse(Input.Clean(request.IdempotencyKey), out var idempotencyKey) || idempotencyKey == Guid.Empty)
        {
            errors.Add("idempotencyKey", "idempotencyKey must be a GUID made by the client for this return.");
        }

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var holderUserId = request.HolderUserId!.Value;
        if (holderUserId == caller.UserId)
        {
            return WorkspaceProblems.ReviewerIsRecorder();
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        if (await RepeatAsync(db, projectId, idempotencyKey, cancellationToken) is { } earlier)
        {
            return earlier.HolderUserId == holderUserId && earlier.Amount == amount
                ? TypedResults.Ok(earlier)
                : CoreProblems.KeyUsedElsewhere();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await CustodyLock.TryAcquireAsync(db, projectId, holderUserId, cancellationToken))
        {
            return ProjectProblems.TryAgainLater();
        }

        var balance = await CustodyQueries.BalanceAsync(db, projectId, holderUserId, cancellationToken);
        if (amount > balance)
        {
            return CustodyProblems.ReturnAboveBalance(balance);
        }

        var entry = new CustodyEntry
        {
            ProjectId = projectId,
            HolderUserId = holderUserId,
            Kind = CustodyEntryKind.Return,
            Amount = amount!.Value,
            EntryDate = returnDate!.Value,
            Method = method,
            Reference = reference,
            Notes = notes,
            Status = CustodyEntryStatus.Posted,
            IdempotencyKey = idempotencyKey,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };
        db.CustodyEntries.Add(entry);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, CustodyEntryConfiguration.IdempotencyIndex))
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return await RepeatAsync(db, projectId, idempotencyKey, cancellationToken) is { } raced ? TypedResults.Ok(raced) : CoreProblems.KeyUsedElsewhere();
        }

        await transaction.CommitAsync(cancellationToken);
        log.CustodyReturned(caller.UserId, projectId, entry.Id, holderUserId);
        return TypedResults.Created(
            $"/api/projects/{projectId}/custody/{holderUserId}",
            new ReturnView(entry.Id, holderUserId, entry.Amount, entry.EntryDate, entry.Method, balance - entry.Amount));
    }

    private static async Task<ReturnView?> RepeatAsync(ProjectsDbContext db, int projectId, Guid key, CancellationToken cancellationToken)
    {
        var entry = await db.CustodyEntries.AsNoTracking()
            .SingleOrDefaultAsync(entry => entry.IdempotencyKey == key && entry.ProjectId == projectId, cancellationToken);
        return entry is null
            ? null
            : new ReturnView(entry.Id, entry.HolderUserId, entry.Amount, entry.EntryDate, entry.Method,
                await CustodyQueries.BalanceAsync(db, projectId, entry.HolderUserId, cancellationToken));
    }
}

/// <summary>Error responses of cash custody.</summary>
internal static class CustodyProblems
{
    public const string Insufficient = CustodyRefusedException.Insufficient;
    public const string ReturnTooLarge = "custody_return_above_balance";

    public static ProblemHttpResult CustodyNotFound() => WorkspaceProblems.NotFound("custody");

    public static ProblemHttpResult OnlyOwnCustody() =>
        ApiProblem.ForbiddenResult("You can ask for funding of your own custody only.");

    public static ProblemHttpResult NotEnough(decimal balance, decimal amount) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, Insufficient,
            $"The custody holds {Math.Max(balance, 0):0.00}, less than {amount:0.00}. Ask for a funding first.");

    public static ProblemHttpResult ReturnAboveBalance(decimal balance) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, ReturnTooLarge, $"The custody holds {Math.Max(balance, 0):0.00}: a return can't be more.");

    public static ProblemHttpResult Refused(CustodyRefusedException refused) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, refused.Code, refused.Message);
}
