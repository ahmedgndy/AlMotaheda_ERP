using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Finance.Common;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Finance.Data.Configurations;
using AlMotaheda.Modules.Finance.Requests;
using AlMotaheda.Modules.Finance.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Finance.Features.Custody;

/// <summary>A custody account as the screens show it. Figures are <c>null</c> for a caller who may not see money on it.</summary>
internal sealed record CustodyAccountView(
    int Id,
    string Number,
    string Kind,
    int? HolderUserId,
    PersonView? Holder,
    int? ReferenceId,
    int? ParentAccountId,
    string NameAr,
    string NameEn,
    string Status,
    string? Notes,
    decimal? Posted,
    decimal? Pending,
    decimal? Reserved,
    decimal? InTransit,
    decimal? Available,
    string LedgerOwner,
    string? StatementPath,
    DateOnly? LastPeriodEnd,
    DateTime OpenedAt,
    PersonView? OpenedBy,
    DateTime? ClosedAt,
    PersonView? ClosedBy,
    string? CloseReason,
    string RowVersion);

/// <summary>One movement of an account Finance ledgers, oldest first, with the running posted balance after it.</summary>
internal sealed record CustodyMovementView(
    long Id, DateOnly EntryDate, string Kind, string Status, decimal Amount, decimal BalanceAfter, string SourceType, int SourceId,
    int? TransferId, int? PaymentRequestId, long? ReversesMovementId, string? Reason, string? Reference, DateTime CreatedAt, PersonView? CreatedBy);

/// <summary>Who may see and do what on a custody account, and the views, shared by every custody slice.</summary>
internal sealed class CustodyAccountViews(FinanceDbContext db, CustodyLedgers ledgers, IUserDirectory users)
{
    /// <summary>The holder sees their own account; a custody reviewer or anyone who sees the ledger sees every account.</summary>
    public static bool MaySee(FinanceCaller caller, CustodyAccount account) =>
        caller.SeesAllCustody || account.HolderUserId == caller.UserId;

    /// <summary>The holder, or a custody reviewer, may move money out of the account (ask a transfer, disburse it).</summary>
    public static bool MayOperate(FinanceCaller caller, CustodyAccount account) =>
        caller.ReviewsCustody || account.HolderUserId == caller.UserId;

    public static string LedgerOwnerOf(string kind) => kind switch
    {
        CustodyAccountKinds.EngineerProject => "Projects",
        CustodyAccountKinds.Vehicle => "Fleet",
        CustodyAccountKinds.Office => "Office",
        _ => "Finance",
    };

    public static string? StatementPathOf(CustodyAccount account) => account.Kind switch
    {
        CustodyAccountKinds.EngineerProject => $"/api/projects/{account.ReferenceId}/custody/{account.HolderUserId}",
        CustodyAccountKinds.Vehicle => $"/api/vehicles/{account.ReferenceId}/custody",
        _ => $"/api/custody-accounts/{account.Id}",
    };

    public async Task<CustodyAccount?> FindVisibleAsync(FinanceCaller caller, int accountId, CancellationToken cancellationToken)
    {
        var account = await db.CustodyAccounts.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == accountId, cancellationToken);
        return account is null || !MaySee(caller, account) ? null : account;
    }

    public async Task<CustodyAccountView> ViewAsync(CustodyAccount account, FinanceCaller caller, CancellationToken cancellationToken)
    {
        var people = await users.FindAsync(new[] { account.HolderUserId, account.OpenedBy, account.ClosedBy }.OfType<int>().Distinct().ToList(), cancellationToken);
        var figures = MaySee(caller, account) ? await ledgers.FiguresAsync(account, cancellationToken) : null;
        return View(account, figures, people, await ledgers.LastPeriodEndAsync(account.Id, cancellationToken));
    }

    public async Task<IReadOnlyList<CustodyAccountView>> ViewsAsync(IReadOnlyList<CustodyAccount> accounts, FinanceCaller caller, CancellationToken cancellationToken)
    {
        var people = await users.FindAsync(
            accounts.SelectMany(account => new[] { account.HolderUserId, account.OpenedBy, account.ClosedBy }).OfType<int>().Distinct().ToList(), cancellationToken);
        var views = new List<CustodyAccountView>();
        foreach (var account in accounts)
        {
            var figures = MaySee(caller, account) ? await ledgers.FiguresAsync(account, cancellationToken) : null;
            views.Add(View(account, figures, people, await ledgers.LastPeriodEndAsync(account.Id, cancellationToken)));
        }

        return views;
    }

    public async Task<IReadOnlyList<CustodyMovementView>> StatementAsync(CustodyAccount account, CancellationToken cancellationToken)
    {
        if (!account.IsLedgeredByFinance)
        {
            return [];
        }

        var movements = await ledgers.Movements(account.Id)
            .OrderBy(movement => movement.EntryDate).ThenBy(movement => movement.Id)
            .ToListAsync(cancellationToken);
        var people = await users.FindAsync(movements.Select(movement => movement.CreatedBy).Distinct().ToList(), cancellationToken);

        decimal balance = 0;
        var lines = new List<CustodyMovementView>();
        foreach (var movement in movements)
        {
            if (movement.Status == CustodyMovementStatus.Posted)
            {
                balance += movement.Amount;
            }

            lines.Add(new CustodyMovementView(
                movement.Id, movement.EntryDate, movement.Kind.ToString(), movement.Status.ToString(), movement.Amount, balance, movement.SourceType,
                movement.SourceId, movement.TransferId, movement.PaymentRequestId, movement.ReversesMovementId, movement.Reason, movement.Reference,
                movement.CreatedAt, Person(movement.CreatedBy, people)));
        }

        return lines;
    }

    public static PersonView? Person(int? userId, IReadOnlyDictionary<int, UserSummary> people) =>
        userId is { } id
            ? people.TryGetValue(id, out var user) ? new PersonView(id, user.NameAr, user.NameEn) : new PersonView(id, null, null)
            : null;

    private static CustodyAccountView View(CustodyAccount account, CustodyFigures? figures, IReadOnlyDictionary<int, UserSummary> people, DateOnly? lastPeriodEnd) => new(
        account.Id, account.Number, account.Kind, account.HolderUserId, Person(account.HolderUserId, people), account.ReferenceId, account.ParentAccountId,
        account.NameAr, account.NameEn, account.Status.ToString(), account.Notes,
        figures?.Posted, figures?.Pending, figures?.Reserved, figures?.InTransit, figures?.Available,
        LedgerOwnerOf(account.Kind), StatementPathOf(account), lastPeriodEnd,
        account.OpenedAt, Person(account.OpenedBy, people), account.ClosedAt, Person(account.ClosedBy, people), account.CloseReason,
        RowVersions.Format(account.RowVersion));
}

/// <summary>
/// The custody accounts of the hierarchy: <c>GET /api/custody-accounts</c> (custody View: your own, or every one for
/// a reviewer), <c>GET /api/custody-accounts/{id}</c> (figures and, for an account Finance ledgers, the statement),
/// <c>POST /api/custody-accounts</c> (custody Approve: open an accountant's, an engineer's project or a vehicle
/// account under a parent) and <c>POST /api/custody-accounts/{id}/close</c> (custody Approve: only once empty).
/// </summary>
internal static class CustodyAccountEndpoints
{
    internal sealed record ListResponse(IReadOnlyList<CustodyAccountView> Items, bool SeesAll, string BalancePolicy);

    internal sealed record AccountResponse(CustodyAccountView Account, IReadOnlyList<CustodyMovementView> Movements, string BalancePolicy);

    internal sealed record OpenRequest(string? Kind, int? HolderUserId, int? ReferenceId, int? ParentAccountId, string? NameAr, string? NameEn, string? Notes);

    internal sealed record CloseRequest(string? Reason, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/custody-accounts", ListAsync)
            .RequirePermission(FinancePermissions.Custody, PermissionAction.View)
            .WithName("ListCustodyAccounts")
            .WithSummary("The custody accounts (kind, holderUserId, status): your own, or every one for a custody reviewer");

        api.MapGet("/custody-accounts/{accountId:int}", GetAsync)
            .RequirePermission(FinancePermissions.Custody, PermissionAction.View)
            .WithName("GetCustodyAccount")
            .WithSummary("One custody account: posted, pending, reserved, in transit, available, and the statement when Finance keeps its ledger");

        api.MapPost("/custody-accounts", OpenAsync)
            .RequirePermission(FinancePermissions.Custody, PermissionAction.Approve)
            .WithName("OpenCustodyAccount")
            .WithSummary("Open a custody account (custody Approve): Accountant, EngineerProject or Vehicle, under a parent. 409 custody_account_exists");

        api.MapPost("/custody-accounts/{accountId:int}/close", CloseAsync)
            .RequirePermission(FinancePermissions.Custody, PermissionAction.Approve)
            .WithName("CloseCustodyAccount")
            .WithSummary("Close an empty custody account (custody Approve). 409 custody_not_empty");
    }

    internal static async Task<Results<Ok<ListResponse>, ValidationProblem, ProblemHttpResult>> ListAsync(
        HttpContext httpContext,
        FinanceDbContext db,
        CustodyAccountViews views,
        CustodyBalancePolicy policy,
        CancellationToken cancellationToken,
        string? kind = null,
        int? holderUserId = null,
        string? status = null)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var wantedKind = Input.Clean(kind);
        if (wantedKind is not null && !CustodyAccountKinds.All.Contains(wantedKind))
        {
            errors.Add("kind", $"kind must be one of: {string.Join(", ", CustodyAccountKinds.All)}.");
        }

        var wantedStatus = Input.OptionalName<CustodyAccountStatus>(status, errors, "status");
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        var query = db.CustodyAccounts.AsNoTracking();
        if (!caller.SeesAllCustody)
        {
            query = query.Where(account => account.HolderUserId == caller.UserId);
        }

        if (wantedKind is not null)
        {
            query = query.Where(account => account.Kind == wantedKind);
        }

        if (holderUserId is { } holder)
        {
            query = query.Where(account => account.HolderUserId == holder);
        }

        if (wantedStatus is { } wanted)
        {
            query = query.Where(account => account.Status == wanted);
        }

        var accounts = await query.OrderBy(account => account.Status).ThenBy(account => account.Kind).ThenBy(account => account.Id).Take(500).ToListAsync(cancellationToken);
        return TypedResults.Ok(new ListResponse(await views.ViewsAsync(accounts, caller, cancellationToken), caller.SeesAllCustody, policy.Value));
    }

    internal static async Task<Results<Ok<AccountResponse>, ProblemHttpResult>> GetAsync(
        int accountId, HttpContext httpContext, CustodyAccountViews views, CustodyBalancePolicy policy, CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var account = await views.FindVisibleAsync(caller, accountId, cancellationToken);
        if (account is null)
        {
            return FinanceProblems.CustodyAccountNotFound();
        }

        return TypedResults.Ok(new AccountResponse(
            await views.ViewAsync(account, caller, cancellationToken), await views.StatementAsync(account, cancellationToken), policy.Value));
    }

    internal static async Task<Results<Created<CustodyAccountView>, ValidationProblem, ProblemHttpResult>> OpenAsync(
        OpenRequest request,
        HttpContext httpContext,
        FinanceDbContext db,
        CustodyLedgers ledgers,
        CustodyAccountViews views,
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
        if (kind is null || !CustodyAccountKinds.All.Contains(kind))
        {
            errors.Add("kind", $"kind must be one of: {string.Join(", ", CustodyAccountKinds.All)}.");
        }
        else if (kind is CustodyAccountKinds.PayrollBatch or CustodyAccountKinds.Office)
        {
            errors.Add("kind", "A payroll batch account is opened with its batch; an office custody is opened in the Office module.");
        }

        if (request.HolderUserId is < 1)
        {
            errors.Add("holderUserId", "holderUserId must be a user id.");
        }

        if (request.ReferenceId is < 1)
        {
            errors.Add("referenceId", "referenceId must be an id.");
        }

        if (request.ParentAccountId is < 1)
        {
            errors.Add("parentAccountId", "parentAccountId must be an account id.");
        }

        var nameAr = Input.OptionalText(request.NameAr, CustodyAccountConfiguration.NameMaxLength, errors, "nameAr", "The Arabic name");
        var nameEn = Input.OptionalText(request.NameEn, CustodyAccountConfiguration.NameMaxLength, errors, "nameEn", "The English name");
        var notes = Input.OptionalText(request.Notes, CustodyAccountConfiguration.NotesMaxLength, errors, "notes", "Notes");
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        var draft = await CustodyAccountOpening.PrepareAsync(db, ledgers, users, kind!, request.HolderUserId, request.ReferenceId, request.ParentAccountId, nameAr, nameEn, notes, caller.UserId, clock.UtcNow, errors, cancellationToken);
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        db.CustodyAccounts.Add(draft!);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, CustodyAccountConfiguration.ActiveIndex))
        {
            return FinanceProblems.AccountExists();
        }

        log.CustodyAccountOpened(caller.UserId, draft!.Id, draft.Kind, draft.HolderUserId);
        return TypedResults.Created($"/api/custody-accounts/{draft.Id}", await views.ViewAsync(draft, caller, cancellationToken));
    }

    internal static async Task<Results<Ok<CustodyAccountView>, ValidationProblem, ProblemHttpResult>> CloseAsync(
        int accountId,
        CloseRequest request,
        HttpContext httpContext,
        FinanceDbContext db,
        CustodyLedgers ledgers,
        CustodyAccountViews views,
        CompanyClock clock,
        FinanceLog log,
        CancellationToken cancellationToken)
    {
        if (FinanceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var reason = Input.RequiredText(request.Reason, CustodyAccountConfiguration.ReasonMaxLength, errors, "reason", "The reason");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FinanceProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await CustodyLedgers.TryLockAsync(db, accountId, cancellationToken))
        {
            return FinanceProblems.CustodyIsBusy();
        }

        var account = await db.CustodyAccounts.SingleOrDefaultAsync(candidate => candidate.Id == accountId, cancellationToken);
        if (account is null)
        {
            return FinanceProblems.CustodyAccountNotFound();
        }

        if (account.Status == CustodyAccountStatus.Closed)
        {
            return FinanceProblems.AccountClosed();
        }

        var figures = await ledgers.FiguresAsync(account, cancellationToken);
        if (figures.Posted != 0 || figures.Pending != 0 || figures.Reserved != 0 || figures.InTransit != 0)
        {
            return FinanceProblems.AccountNotEmpty(figures.Posted);
        }

        account.Status = CustodyAccountStatus.Closed;
        account.ClosedAt = clock.UtcNow;
        account.ClosedBy = caller.UserId;
        account.CloseReason = reason;
        db.Entry(account).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.CustodyAccountClosed(caller.UserId, account.Id);
        return TypedResults.Ok(await views.ViewAsync(account, caller, cancellationToken));
    }
}

/// <summary>Builds a new custody account from a validated form, checking what each kind must name. Shared with transfers that open the destination on the way.</summary>
internal static class CustodyAccountOpening
{
    public static async Task<CustodyAccount?> PrepareAsync(
        FinanceDbContext db,
        CustodyLedgers ledgers,
        IUserDirectory users,
        string kind,
        int? holderUserId,
        int? referenceId,
        int? parentAccountId,
        string? nameAr,
        string? nameEn,
        string? notes,
        int actorUserId,
        DateTime now,
        ValidationErrors errors,
        CancellationToken cancellationToken)
    {
        string? targetAr = null, targetEn = null;
        switch (kind)
        {
            case CustodyAccountKinds.Accountant:
                if (holderUserId is null)
                {
                    errors.Add("holderUserId", "An accountant's custody needs its holder.");
                }

                if (referenceId is not null)
                {
                    errors.Add("referenceId", "An accountant's custody has no reference.");
                }

                break;

            case CustodyAccountKinds.EngineerProject:
                if (holderUserId is null)
                {
                    errors.Add("holderUserId", "An engineer's project custody needs its holder.");
                }

                if (referenceId is null)
                {
                    errors.Add("referenceId", "An engineer's project custody needs its project (referenceId).");
                }

                break;

            case CustodyAccountKinds.Office:
                if (referenceId is null)
                {
                    errors.Add("referenceId", "An office custody needs its office custody record (referenceId).");
                }

                break;
            case CustodyAccountKinds.Vehicle:
                if (holderUserId is not null)
                {
                    errors.Add("holderUserId", "A vehicle custody has no holder user: its driver is recorded in Fleet.");
                }

                if (referenceId is null)
                {
                    errors.Add("referenceId", "A vehicle custody needs its vehicle (referenceId).");
                }

                break;
        }

        if (errors.HasErrors)
        {
            return null;
        }

        if (holderUserId is { } holder)
        {
            var found = (await users.FindAsync([holder], cancellationToken)).GetValueOrDefault(holder);
            if (found is not { IsActive: true })
            {
                errors.Add("holderUserId", "The holder must be an active user.");
            }
            else
            {
                (targetAr, targetEn) = (found.NameAr, found.NameEn);
            }
        }

        if (referenceId is { } reference && ledgers.SourceOf(kind) is { } source)
        {
            var target = await source.FindAsync(reference, holderUserId, cancellationToken);
            if (target is null)
            {
                errors.Add("referenceId", kind == CustodyAccountKinds.Vehicle
                    ? "The vehicle was not found."
                    : "The project was not found, or the holder is not an active member of it.");
            }
            else
            {
                (targetAr, targetEn) = (targetAr is null ? target.NameAr : $"{targetAr} – {target.NameAr}", targetEn is null ? target.NameEn : $"{targetEn} – {target.NameEn}");

                // A kind whose holder is recorded by the owning module (an office custody's custodian) takes it from there.
                holderUserId ??= target.HolderUserId;
            }
        }

        if (parentAccountId is { } parentId)
        {
            var parent = await db.CustodyAccounts.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == parentId, cancellationToken);
            if (parent is null || parent.Status != CustodyAccountStatus.Active)
            {
                errors.Add("parentAccountId", "The parent account was not found or is closed.");
            }
        }

        if (errors.HasErrors)
        {
            return null;
        }

        return new CustodyAccount
        {
            Kind = kind,
            HolderUserId = holderUserId,
            ReferenceId = referenceId,
            ParentAccountId = parentAccountId,
            NameAr = nameAr ?? targetAr ?? kind,
            NameEn = nameEn ?? targetEn ?? kind,
            Notes = notes,
            OpenedAt = now,
            OpenedBy = actorUserId,
        };
    }
}
