using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Data.Configurations;
using AlMotaheda.Modules.Workforce.Features.Payments;
using AlMotaheda.Modules.Workforce.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Features.Settlements;

/// <summary>One worker on the payable screen of a period.</summary>
/// <param name="WaitingLines">Days of the period on sheets not approved yet: not pay yet, shown apart.</param>
internal sealed record WorkerPayableView(
    int WorkerId, string? NameAr, string? NameEn, decimal Days, decimal Approved, decimal Paid, decimal Held, decimal Outstanding,
    int WaitingLines);

/// <summary>A settlement as the client sees it.</summary>
internal sealed record SettlementView(
    int Id,
    int ProjectId,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    DateOnly PaidOn,
    int PaidByUserId,
    string? PaidByNameAr,
    string? PaidByNameEn,
    string Method,
    decimal Amount,
    string? Reference,
    string? Notes,
    string Status,
    int RecordedBy,
    DateTime RecordedAt,
    int? ReviewedBy,
    DateTime? ReviewedAt,
    string? ReviewNote,
    int? ReversedBy,
    DateTime? ReversedAt,
    string? ReverseReason,
    int WorkerCount,
    IReadOnlyList<SettlementLineView>? Lines,
    string RowVersion);

internal sealed record SettlementLineView(
    int WorkerId, string? NameAr, string? NameEn, decimal Amount, int? PaymentId, IReadOnlyList<SettlementDayView> Days);

internal sealed record SettlementDayView(int LineId, DateOnly WorkDate, decimal Days, decimal Amount);

/// <summary>
/// Weekly labor settlements of a project ("تسوية الأسبوعية"): wages paid in cash out of a custody for a period,
/// confirmed or rejected by a custody reviewer (custody Approve: the accountant or an Executive) who is neither the
/// recorder nor the holder. Only a confirmed settlement is paid labor.
/// </summary>
/// <remarks>
/// Who records and sees them: whoever sees labor money (workers See money: the project manager, the accountant, an
/// Executive), for any holder; and a custody holder (custody Create: the site engineer), for their own custody only,
/// who then sees the payable figures of the projects they work on and their own settlements. Recording also needs
/// workers Create. Nobody confirms their own settlement.
/// </remarks>
internal static class SettlementEndpoints
{
    /// <summary>The custody permission module: reviewing a custody outflow needs its Approve.</summary>
    public const string CustodyModule = "custody";

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/projects/{projectId:int}/labor/payable", PayableAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.View)
            .RequireAnyPermission((WorkforcePermissions.Workers, PermissionAction.SeeMoney), (CustodyModule, PermissionAction.Create))
            .WithName("GetLaborPayable")
            .WithSummary("What each worker is owed for a period on the project: approved, paid, held by pending settlements or requests, outstanding (from, to)");

        api.MapGet("/projects/{projectId:int}/labor/settlements", ListAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.View)
            .RequireAnyPermission((WorkforcePermissions.Workers, PermissionAction.SeeMoney), (CustodyModule, PermissionAction.Create))
            .WithName("ListPayrollSettlements")
            .WithSummary("The weekly settlements of a project, newest first (status)");

        api.MapGet("/projects/{projectId:int}/labor/settlements/{settlementId:int}", GetAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.View)
            .RequireAnyPermission((WorkforcePermissions.Workers, PermissionAction.SeeMoney), (CustodyModule, PermissionAction.Create))
            .WithName("GetPayrollSettlement")
            .WithSummary("One settlement with its workers and the days each payment covers");

        api.MapPost("/projects/{projectId:int}/labor/settlements", RecordAsync)
            .RequirePermission(WorkforcePermissions.Workers, PermissionAction.Create)
            .RequireAnyPermission((WorkforcePermissions.Workers, PermissionAction.SeeMoney), (CustodyModule, PermissionAction.Create))
            .WithName("RecordPayrollSettlement")
            .WithSummary("Record wages paid in cash from a custody for a period; the days are held until a custody reviewer confirms (idempotent by idempotencyKey)");

        api.MapPost("/projects/{projectId:int}/labor/settlements/{settlementId:int}/confirm", ConfirmAsync)
            .RequirePermission(CustodyModule, PermissionAction.Approve)
            .WithName("ConfirmPayrollSettlement")
            .WithSummary("Confirm a settlement (custody Approve; not the recorder or the holder): writes the worker payments");

        api.MapPost("/projects/{projectId:int}/labor/settlements/{settlementId:int}/reject", RejectAsync)
            .RequirePermission(CustodyModule, PermissionAction.Approve)
            .WithName("RejectPayrollSettlement")
            .WithSummary("Reject a pending settlement (custody Approve): the days are free again and the custody gets the cash back");

        api.MapPost("/projects/{projectId:int}/labor/settlements/{settlementId:int}/reverse", ReverseAsync)
            .RequirePermission(CustodyModule, PermissionAction.Approve)
            .WithName("ReversePayrollSettlement")
            .WithSummary("Reverse a confirmed settlement (custody Approve): its worker payments are voided and the custody outflow released");
    }

    internal sealed record PayableResponse(
        DateOnly From, DateOnly To, IReadOnlyList<WorkerPayableView> Items, decimal Approved, decimal Paid, decimal Held, decimal Outstanding,
        decimal? CustodyBalance);

    internal static async Task<Results<Ok<PayableResponse>, ValidationProblem, ProblemHttpResult>> PayableAsync(
        int projectId,
        HttpContext httpContext,
        WorkforceDbContext db,
        IProjectAccess access,
        IPaymentRequests requests,
        ICustodyLedger custody,
        CompanyClock clock,
        CancellationToken cancellationToken,
        string? from = null,
        string? to = null)
    {
        if (WorkforceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var (periodFrom, periodTo) = Period(from, to, clock.Today, errors);
        if (errors.HasErrors)
        {
            return WorkforceProblems.Validation(errors);
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return WorkforceProblems.ProjectNotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var payables = await PayablesAsync(db, requests, projectId, periodFrom, periodTo, null, cancellationToken);
        var waiting = await SettlementQueries.WaitingLines(db, projectId, periodFrom, periodTo).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var workerIds = payables.Select(payable => payable.WorkerId).Concat(waiting.Select(row => row.WorkerId)).Distinct().ToList();
        var workers = await db.Workers.AsNoTracking().Where(worker => workerIds.Contains(worker.Id))
            .Select(worker => new { worker.Id, worker.NameAr, worker.NameEn })
            .ToDictionaryAsync(worker => worker.Id, cancellationToken);

        var items = workerIds
            .Select(workerId =>
            {
                var payable = payables.FirstOrDefault(row => row.WorkerId == workerId);
                var worker = workers.GetValueOrDefault(workerId);
                return new WorkerPayableView(
                    workerId, worker?.NameAr, worker?.NameEn, payable?.Days ?? 0, payable?.Approved ?? 0, payable?.Paid ?? 0, payable?.Held ?? 0,
                    payable?.Outstanding ?? 0, waiting.FirstOrDefault(row => row.WorkerId == workerId)?.Count ?? 0);
            })
            .OrderByDescending(item => item.Outstanding)
            .ThenBy(item => item.NameEn)
            .ToList();

        decimal? balance = await custody.CanHoldAsync(projectId, caller.UserId, cancellationToken)
            ? await custody.BalanceAsync(projectId, caller.UserId, cancellationToken)
            : null;

        return TypedResults.Ok(new PayableResponse(
            periodFrom, periodTo, items, items.Sum(item => item.Approved), items.Sum(item => item.Paid), items.Sum(item => item.Held),
            items.Sum(item => item.Outstanding), balance));
    }

    internal sealed record ListResponse(IReadOnlyList<SettlementView> Items);

    internal static async Task<Results<Ok<ListResponse>, ProblemHttpResult>> ListAsync(
        int projectId,
        HttpContext httpContext,
        WorkforceDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CancellationToken cancellationToken,
        string? status = null)
    {
        if (WorkforceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return WorkforceProblems.ProjectNotFound();
        }

        var wanted = Input.TryParseName<SettlementStatus>(status, out var parsed) ? parsed : (SettlementStatus?)null;
        int? onlyOf = SeesAllSettlements(httpContext.User) ? null : caller.UserId;
        var settlements = await db.PayrollSettlements.AsNoTracking()
            .Where(settlement => settlement.ProjectId == projectId && (wanted == null || settlement.Status == wanted))
            .Where(settlement => onlyOf == null || settlement.RecordedBy == onlyOf || settlement.PaidByUserId == onlyOf)
            .Include(settlement => settlement.Lines)
            .OrderByDescending(settlement => settlement.PaidOn).ThenByDescending(settlement => settlement.Id)
            .Take(200)
            .ToListAsync(cancellationToken);
        var names = await users.FindAsync(settlements.Select(settlement => settlement.PaidByUserId).Distinct().ToList(), cancellationToken);

        return TypedResults.Ok(new ListResponse(settlements.Select(settlement => View(settlement, names, null)).ToList()));
    }

    internal static async Task<Results<Ok<SettlementView>, ProblemHttpResult>> GetAsync(
        int projectId,
        int settlementId,
        HttpContext httpContext,
        WorkforceDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CancellationToken cancellationToken)
    {
        if (WorkforceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return WorkforceProblems.ProjectNotFound();
        }

        // A holder without labor money sees only the settlements they recorded or paid; others are not found.
        var view = await LoadAsync(db, users, projectId, settlementId, cancellationToken);
        return view is null || (!SeesAllSettlements(httpContext.User) && view.RecordedBy != caller.UserId && view.PaidByUserId != caller.UserId)
            ? WorkforceProblems.SettlementNotFound()
            : TypedResults.Ok(view);
    }

    internal sealed record WorkerAmount(int? WorkerId, decimal? Amount);

    internal sealed record RecordRequest(
        string? PeriodFrom,
        string? PeriodTo,
        string? PaidOn,
        int? PaidByUserId,
        string? Reference,
        string? Notes,
        string? IdempotencyKey,
        IReadOnlyList<WorkerAmount>? Lines);

    /// <summary>
    /// Records a settlement. Without <c>lines</c>, it pays every worker everything still outstanding for the period;
    /// with them, each worker's amount, allocated oldest day first. One transaction, under the project's and the
    /// workers' locks, with the custody outflow (Projects) and the alerts.
    /// </summary>
    internal static async Task<Results<Created<SettlementView>, Ok<SettlementView>, ValidationProblem, ProblemHttpResult>> RecordAsync(
        int projectId,
        RecordRequest request,
        HttpContext httpContext,
        WorkforceDbContext db,
        IProjectAccess access,
        IProjectDirectory directory,
        IPaymentRequests requests,
        ICustodyLedger custody,
        IUserDirectory users,
        CompanyClock clock,
        WorkforceLog log,
        CancellationToken cancellationToken)
    {
        if (WorkforceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var (periodFrom, periodTo) = Period(request.PeriodFrom, request.PeriodTo, clock.Today, errors, required: true);
        var paidOn = Input.RequiredDate(request.PaidOn, errors, "paidOn", "The payment date");
        if (paidOn > clock.Today)
        {
            errors.Add("paidOn", "The payment date can't be in the future.");
        }

        var reference = Input.OptionalText(request.Reference, PayrollSettlementConfiguration.ReferenceMaxLength, errors, "reference", "The reference");
        var notes = Input.OptionalText(request.Notes, PayrollSettlementConfiguration.NotesMaxLength, errors, "notes", "Notes");
        if (!Guid.TryParse(Input.Clean(request.IdempotencyKey), out var idempotencyKey) || idempotencyKey == Guid.Empty)
        {
            errors.Add("idempotencyKey", "idempotencyKey must be a GUID made by the client for this settlement.");
        }

        Dictionary<int, decimal>? asked = null;
        if (request.Lines is { } lines)
        {
            asked = [];
            foreach (var line in lines)
            {
                if (line.WorkerId is not { } workerId || workerId < 1)
                {
                    errors.Add("lines", "Every line needs a workerId.");
                    break;
                }

                if (NumberInput.RequiredMoney(line.Amount, errors, "lines", "Every amount") is not { } amount)
                {
                    break;
                }

                if (!asked.TryAdd(workerId, amount))
                {
                    errors.Add("lines", $"Worker {workerId} is listed twice.");
                    break;
                }
            }

            if (asked.Count == 0 && !errors.HasErrors)
            {
                errors.Add("lines", "Give at least one worker, or leave lines out to settle everything outstanding.");
            }
        }

        if (request.PaidByUserId is < 1)
        {
            errors.Add("paidByUserId", "paidByUserId must be a user id.");
        }

        if (errors.HasErrors)
        {
            return WorkforceProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return WorkforceProblems.ProjectNotFound();
        }

        if (project.IsCompleted && !project.CallerIsExecutive)
        {
            return WorkforceProblems.CompletedProject();
        }

        if (await RepeatAsync(db, users, projectId, idempotencyKey, cancellationToken) is { } repeat)
        {
            return repeat;
        }

        var holderUserId = request.PaidByUserId ?? caller.UserId;

        // A custody holder without labor money records only what they paid from their own custody.
        if (holderUserId != caller.UserId && !SeesAllSettlements(httpContext.User))
        {
            return ApiProblem.ForbiddenResult("You can record wages paid from your own custody only.");
        }

        if (!await custody.CanHoldAsync(projectId, holderUserId, cancellationToken))
        {
            errors.Add("paidByUserId", "The cash must come from the custody of an active member of this project.");
            return WorkforceProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Project(projectId), cancellationToken))
        {
            return WorkforceProblems.TryAgainLater();
        }

        // Checked again under the lock: a retry with the same key may have committed while this one waited.
        if (await RepeatAsync(db, users, projectId, idempotencyKey, cancellationToken) is { } committed)
        {
            return committed;
        }

        var payables = await PayablesAsync(db, requests, projectId, periodFrom, periodTo, null, cancellationToken);
        var plan = SettlementPlan.Plan(payables, asked);
        if (plan.TooMuchFor is { } overpaid)
        {
            return WorkforceProblems.SettlesTooMuch(overpaid, plan.OutstandingOfThatWorker ?? 0);
        }

        if (plan.Lines.Count == 0)
        {
            return WorkforceProblems.NothingToSettle();
        }

        // Each worker's lock too: a Finance payment request for the same worker takes the worker lock, not the project's.
        foreach (var workerId in plan.Lines.Select(line => line.WorkerId).Order())
        {
            if (!await AppLock.TryAcquireAsync(db, AppLock.Worker(workerId), cancellationToken))
            {
                return WorkforceProblems.TryAgainLater();
            }
        }

        var now = clock.UtcNow;
        var settlement = new PayrollSettlement
        {
            ProjectId = projectId,
            PeriodFrom = periodFrom,
            PeriodTo = periodTo,
            PaidOn = paidOn!.Value,
            PaidByUserId = holderUserId,
            Amount = plan.Amount,
            Reference = reference,
            Notes = notes,
            RecordedBy = caller.UserId,
            RecordedAt = now,
            IdempotencyKey = idempotencyKey,
        };
        foreach (var line in plan.Lines)
        {
            var entity = new PayrollSettlementLine { WorkerId = line.WorkerId, Amount = line.Amount };
            entity.Allocations.AddRange(line.Allocations.Select(allocation => new PayrollSettlementAllocation { LineId = allocation.LineId, Amount = allocation.Amount }));
            settlement.Lines.Add(entity);
        }

        db.PayrollSettlements.Add(settlement);

        // The project's managers, the Executives and the accountants hear of it; never the actor.
        var recipients = await SettlementRecipientsAsync(directory, users, projectId, caller.UserId, cancellationToken);
        db.SettlementAlerts.AddRange(recipients.Select(recipient => new SettlementAlert
        {
            Settlement = settlement, ProjectId = projectId, RecipientUserId = recipient, CreatedAt = now,
        }));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await custody.HoldAsync(
                new CustodyOutflow(projectId, holderUserId, settlement.Amount, settlement.PaidOn, CustodySourceTypes.PayrollSettlement, settlement.Id,
                    settlement.Method.ToString(), reference, caller.UserId),
                SharedTransaction.Of(db),
                cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, PayrollSettlementConfiguration.IdempotencyKeyIndex))
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return await RepeatAsync(db, users, projectId, idempotencyKey, cancellationToken) ?? WorkforceProblems.KeyUsedElsewhere();
        }
        catch (CustodyRefusedException refused)
        {
            await transaction.RollbackAsync(cancellationToken);
            return refused.Code == CustodyRefusedException.Busy
                ? WorkforceProblems.TryAgainLater()
                : WorkforceProblems.Conflict(refused.Code, refused.Message);
        }

        await transaction.CommitAsync(cancellationToken);
        log.SettlementRecorded(caller.UserId, projectId, settlement.Id, settlement.Lines.Count, recipients.Count);

        var view = await LoadAsync(db, users, projectId, settlement.Id, cancellationToken);
        return TypedResults.Created($"/api/projects/{projectId}/labor/settlements/{settlement.Id}", view!);
    }

    internal sealed record ReviewRequest(string? Note, string? RowVersion);

    internal static Task<Results<Ok<SettlementView>, ValidationProblem, ProblemHttpResult>> ConfirmAsync(
        int projectId, int settlementId, ReviewRequest request, HttpContext httpContext, WorkforceDbContext db, IProjectAccess access,
        IPaymentRequests requests, ICustodyLedger custody, IUserDirectory users, CompanyClock clock, WorkforceLog log, CancellationToken cancellationToken) =>
        DecideAsync(SettlementStatus.Confirmed, projectId, settlementId, request, httpContext, db, access, requests, custody, users, clock, log, cancellationToken);

    internal static Task<Results<Ok<SettlementView>, ValidationProblem, ProblemHttpResult>> RejectAsync(
        int projectId, int settlementId, ReviewRequest request, HttpContext httpContext, WorkforceDbContext db, IProjectAccess access,
        IPaymentRequests requests, ICustodyLedger custody, IUserDirectory users, CompanyClock clock, WorkforceLog log, CancellationToken cancellationToken) =>
        DecideAsync(SettlementStatus.Rejected, projectId, settlementId, request, httpContext, db, access, requests, custody, users, clock, log, cancellationToken);

    internal static Task<Results<Ok<SettlementView>, ValidationProblem, ProblemHttpResult>> ReverseAsync(
        int projectId, int settlementId, ReviewRequest request, HttpContext httpContext, WorkforceDbContext db, IProjectAccess access,
        IPaymentRequests requests, ICustodyLedger custody, IUserDirectory users, CompanyClock clock, WorkforceLog log, CancellationToken cancellationToken) =>
        DecideAsync(SettlementStatus.Reversed, projectId, settlementId, request, httpContext, db, access, requests, custody, users, clock, log, cancellationToken);

    /// <summary>
    /// Confirm (Pending → Confirmed: one worker payment per line, the custody outflow posted), reject (Pending →
    /// Rejected: the days and the cash are free again) or reverse (Confirmed → Reversed: the payments voided, the
    /// outflow released). Never by the recorder or the holder. Under the project's and the workers' locks.
    /// </summary>
    private static async Task<Results<Ok<SettlementView>, ValidationProblem, ProblemHttpResult>> DecideAsync(
        SettlementStatus outcome,
        int projectId,
        int settlementId,
        ReviewRequest request,
        HttpContext httpContext,
        WorkforceDbContext db,
        IProjectAccess access,
        IPaymentRequests requests,
        ICustodyLedger custody,
        IUserDirectory users,
        CompanyClock clock,
        WorkforceLog log,
        CancellationToken cancellationToken)
    {
        if (WorkforceCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = outcome == SettlementStatus.Confirmed
            ? Input.OptionalText(request.Note, PayrollSettlementConfiguration.ReasonMaxLength, errors, "note", "The note")
            : Input.RequiredText(request.Note, PayrollSettlementConfiguration.ReasonMaxLength, errors, "note", "The reason");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return WorkforceProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return WorkforceProblems.ProjectNotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Project(projectId), cancellationToken))
        {
            return WorkforceProblems.TryAgainLater();
        }

        var settlement = await db.PayrollSettlements
            .Include(candidate => candidate.Lines).ThenInclude(line => line.Allocations)
            .SingleOrDefaultAsync(candidate => candidate.Id == settlementId && candidate.ProjectId == projectId, cancellationToken);
        if (settlement is null)
        {
            return WorkforceProblems.SettlementNotFound();
        }

        // Separation of duties: whoever recorded the settlement, or paid it from their custody, doesn't review it.
        if (settlement.RecordedBy == caller.UserId || settlement.PaidByUserId == caller.UserId)
        {
            log.ActionRefused(caller.UserId, $"{outcome}PayrollSettlement", "the recorder or the holder");
            return WorkforceProblems.ReviewerIsRecorder();
        }

        var allowed = outcome == SettlementStatus.Reversed ? settlement.Status == SettlementStatus.Confirmed : settlement.Status == SettlementStatus.Pending;
        if (!allowed)
        {
            return WorkforceProblems.WrongStatus(outcome == SettlementStatus.Reversed
                ? "Only a confirmed settlement can be reversed."
                : "Only a pending settlement can be confirmed or rejected.");
        }

        foreach (var workerId in settlement.Lines.Select(line => line.WorkerId).Distinct().Order())
        {
            if (!await AppLock.TryAcquireAsync(db, AppLock.Worker(workerId), cancellationToken))
            {
                return WorkforceProblems.TryAgainLater();
            }
        }

        var now = clock.UtcNow;
        var shared = SharedTransaction.Of(db);
        db.Entry(settlement).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            switch (outcome)
            {
                case SettlementStatus.Confirmed:
                    if (await NoLongerPayableAsync(db, requests, settlement, cancellationToken) is { } lineId)
                    {
                        return WorkforceProblems.SettlementNotPayable(lineId);
                    }

                    var payments = settlement.Lines.Select(line =>
                    {
                        var payment = new WorkerPayment
                        {
                            WorkerId = line.WorkerId,
                            PaymentDate = settlement.PaidOn,
                            Amount = line.Amount,
                            Method = settlement.Method,
                            Reference = settlement.Reference ?? $"PS-{settlement.Id}",
                            Notes = settlement.Notes,
                            IdempotencyKey = Guid.NewGuid(),
                            Settlement = settlement,
                            CreatedAt = now,
                            CreatedBy = settlement.RecordedBy,
                        };
                        payment.Allocations.AddRange(line.Allocations.Select(allocation => new WorkerPaymentAllocation { LineId = allocation.LineId, Amount = allocation.Amount }));
                        line.Payment = payment;
                        return payment;
                    }).ToList();
                    db.WorkerPayments.AddRange(payments);
                    settlement.Review(SettlementStatus.Confirmed, caller.UserId, now, note);
                    await db.SaveChangesAsync(cancellationToken);
                    await custody.PostAsync(CustodySourceTypes.PayrollSettlement, settlement.Id, shared, cancellationToken);
                    break;

                case SettlementStatus.Rejected:
                    settlement.Review(SettlementStatus.Rejected, caller.UserId, now, note);
                    await db.SaveChangesAsync(cancellationToken);
                    await custody.ReleaseAsync(CustodySourceTypes.PayrollSettlement, settlement.Id, shared, cancellationToken);
                    break;

                default:
                    var paymentIds = settlement.Lines.Select(line => line.PaymentId).OfType<int>().ToList();
                    var toVoid = await db.WorkerPayments.Where(payment => paymentIds.Contains(payment.Id) && payment.VoidedAt == null).ToListAsync(cancellationToken);
                    foreach (var payment in toVoid)
                    {
                        payment.VoidedAt = now;
                        payment.VoidedBy = caller.UserId;
                        payment.VoidReason = note;
                    }

                    settlement.Reverse(caller.UserId, now, note!);
                    await db.SaveChangesAsync(cancellationToken);
                    await custody.ReleaseAsync(CustodySourceTypes.PayrollSettlement, settlement.Id, shared, cancellationToken);
                    break;
            }
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (CustodyRefusedException)
        {
            await transaction.RollbackAsync(cancellationToken);
            return WorkforceProblems.TryAgainLater();
        }

        await transaction.CommitAsync(cancellationToken);
        log.SettlementDecided(caller.UserId, projectId, settlement.Id, outcome.ToString());

        var view = await LoadAsync(db, users, projectId, settlement.Id, cancellationToken);
        return TypedResults.Ok(view!);
    }

    /// <summary>Whoever sees labor money sees every settlement of a project; a custody holder only their own.</summary>
    private static bool SeesAllSettlements(ClaimsPrincipal user) => user.HasPermission(WorkforcePermissions.Workers, PermissionAction.SeeMoney);

    /// <summary>
    /// The first planned day that is no longer owed as planned (a payment request was paid on it since), or
    /// <c>null</c> when every day still fits. The settlement's own hold is left out.
    /// </summary>
    private static async Task<int?> NoLongerPayableAsync(WorkforceDbContext db, IPaymentRequests requests, PayrollSettlement settlement, CancellationToken cancellationToken)
    {
        var lineIds = settlement.Lines.SelectMany(line => line.Allocations).Select(allocation => allocation.LineId).Distinct().ToList();
        var nets = await db.AttendanceLines.AsNoTracking()
            .Where(line => lineIds.Contains(line.Id) && !line.IsVoid && line.Sheet.Status == SheetStatus.Approved && line.NetAmount != null)
            .ToDictionaryAsync(line => line.Id, line => line.NetAmount!.Value, cancellationToken);
        var paid = await SettlementQueries.PaidParts(db, lineIds).ToListAsync(cancellationToken);
        var held = await SettlementQueries.HeldParts(db, lineIds, settlement.Id).ToListAsync(cancellationToken);
        var reserved = await requests.ReservedAsync(PaidRecordTypes.AttendanceLine, lineIds, SharedTransaction.Of(db), cancellationToken);

        foreach (var allocation in settlement.Lines.SelectMany(line => line.Allocations))
        {
            if (!nets.TryGetValue(allocation.LineId, out var net))
            {
                return allocation.LineId;
            }

            var taken = paid.Where(part => part.LineId == allocation.LineId && !part.PaymentVoided).Sum(part => part.Amount)
                        + held.Where(part => part.LineId == allocation.LineId).Sum(part => part.Amount)
                        + reserved.GetValueOrDefault(allocation.LineId);
            if (allocation.Amount > net - taken)
            {
                return allocation.LineId;
            }
        }

        return null;
    }

    /// <summary>Every worker's payable figures for the period, read in the caller's transaction (Finance's holds need it).</summary>
    private static async Task<IReadOnlyList<WorkerPayable>> PayablesAsync(
        WorkforceDbContext db, IPaymentRequests requests, int projectId, DateOnly from, DateOnly to, int? exceptSettlementId, CancellationToken cancellationToken)
    {
        var lines = await SettlementQueries.PayableLines(db, projectId, from, to).ToListAsync(cancellationToken);
        if (lines.Count == 0)
        {
            return [];
        }

        var lineIds = lines.Select(line => line.LineId).ToList();
        var paid = await SettlementQueries.PaidParts(db, lineIds).ToListAsync(cancellationToken);
        var held = await SettlementQueries.HeldParts(db, lineIds, exceptSettlementId).ToListAsync(cancellationToken);
        var reserved = await requests.ReservedAsync(PaidRecordTypes.AttendanceLine, lineIds, SharedTransaction.Of(db), cancellationToken);
        var holds = held.Concat(reserved.Select(entry => new PaidPart(entry.Key, entry.Value, false)));

        return SettlementPlan.Payables(lines.Select(line => (line.WorkerId, line.Line)).ToList(), paid, holds);
    }

    /// <summary>The project's active managers, the active Executives and accountants, never the actor.</summary>
    private static async Task<IReadOnlyList<int>> SettlementRecipientsAsync(
        IProjectDirectory directory, IUserDirectory users, int projectId, int actorUserId, CancellationToken cancellationToken)
    {
        var managers = await directory.ManagerIdsAsync(projectId, cancellationToken);
        var company = await users.ListActiveAsync([AccessRoles.Executive, AccessRoles.Accountant], cancellationToken);
        return managers.Concat(company.Select(user => user.Id)).Where(id => id != actorUserId).Distinct().Order().ToList();
    }

    private static async Task<Results<Created<SettlementView>, Ok<SettlementView>, ValidationProblem, ProblemHttpResult>?> RepeatAsync(
        WorkforceDbContext db, IUserDirectory users, int projectId, Guid key, CancellationToken cancellationToken)
    {
        var first = await db.PayrollSettlements.AsNoTracking()
            .Where(settlement => settlement.IdempotencyKey == key)
            .Select(settlement => new { settlement.Id, settlement.ProjectId })
            .SingleOrDefaultAsync(cancellationToken);
        if (first is null)
        {
            return null;
        }

        if (first.ProjectId != projectId)
        {
            return WorkforceProblems.KeyUsedElsewhere();
        }

        return TypedResults.Ok((await LoadAsync(db, users, projectId, first.Id, cancellationToken))!);
    }

    internal static async Task<SettlementView?> LoadAsync(WorkforceDbContext db, IUserDirectory users, int projectId, int settlementId, CancellationToken cancellationToken)
    {
        var settlement = await db.PayrollSettlements.AsNoTracking()
            .Include(candidate => candidate.Lines).ThenInclude(line => line.Allocations).ThenInclude(allocation => allocation.Line)
            .Include(candidate => candidate.Lines).ThenInclude(line => line.Worker)
            .AsSplitQuery()
            .SingleOrDefaultAsync(candidate => candidate.Id == settlementId && candidate.ProjectId == projectId, cancellationToken);
        if (settlement is null)
        {
            return null;
        }

        var names = await users.FindAsync([settlement.PaidByUserId], cancellationToken);
        var lines = settlement.Lines
            .OrderBy(line => line.Worker.NameEn)
            .Select(line => new SettlementLineView(
                line.WorkerId, line.Worker.NameAr, line.Worker.NameEn, line.Amount, line.PaymentId,
                line.Allocations.OrderBy(allocation => allocation.Line.WorkDate)
                    .Select(allocation => new SettlementDayView(allocation.LineId, allocation.Line.WorkDate, allocation.Line.Days, allocation.Amount))
                    .ToList()))
            .ToList();
        return View(settlement, names, lines);
    }

    private static SettlementView View(PayrollSettlement settlement, IReadOnlyDictionary<int, UserSummary> names, IReadOnlyList<SettlementLineView>? lines)
    {
        var holder = names.GetValueOrDefault(settlement.PaidByUserId);
        return new SettlementView(
            settlement.Id, settlement.ProjectId, settlement.PeriodFrom, settlement.PeriodTo, settlement.PaidOn, settlement.PaidByUserId, holder?.NameAr,
            holder?.NameEn, settlement.Method.ToString(), settlement.Amount, settlement.Reference, settlement.Notes, settlement.Status.ToString(),
            settlement.RecordedBy, settlement.RecordedAt, settlement.ReviewedBy, settlement.ReviewedAt, settlement.ReviewNote, settlement.ReversedBy,
            settlement.ReversedAt, settlement.ReverseReason, settlement.Lines.Count, lines, RowVersions.Format(settlement.RowVersion));
    }

    /// <summary>
    /// The period: both dates, or the current week (Saturday to Friday, the site week) when neither is given and the
    /// period is optional. At most 62 days, never in the future.
    /// </summary>
    private static (DateOnly From, DateOnly To) Period(string? from, string? to, DateOnly today, ValidationErrors errors, bool required = false)
    {
        if (!required && Input.Clean(from) is null && Input.Clean(to) is null)
        {
            var start = today.AddDays(-(((int)today.DayOfWeek - (int)DayOfWeek.Saturday + 7) % 7));
            return (start, today);
        }

        var periodFrom = Input.RequiredDate(from, errors, required ? "periodFrom" : "from", "The first day");
        var periodTo = Input.RequiredDate(to, errors, required ? "periodTo" : "to", "The last day");
        if (periodFrom is { } first && periodTo is { } last)
        {
            if (last < first)
            {
                errors.Add(required ? "periodTo" : "to", "The last day can't be before the first.");
            }
            else if (last.DayNumber - first.DayNumber > 62)
            {
                errors.Add(required ? "periodTo" : "to", "A period is at most 62 days.");
            }

            if (last > today)
            {
                errors.Add(required ? "periodTo" : "to", "The period can't end in the future.");
            }
        }

        return (periodFrom ?? today, periodTo ?? today);
    }
}
