using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Features.Prices;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.PriceControl;

/// <summary>
/// Price exceptions (<c>/api/price-exceptions</c>): the Executive's inbox of transactions priced outside their range,
/// and the three decisions. catalog View to see them (your own, and those of the projects you may see); catalog
/// Approve and an active Executive to decide, never on your own submission.
/// </summary>
internal static class PriceExceptionEndpoints
{
    internal sealed record ListResponse(IReadOnlyList<PriceExceptionView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/price-exceptions", ListAsync)
            .RequirePermission(PricePermissions.Module, PermissionAction.View)
            .WithName("ListPriceExceptions")
            .WithSummary("Price exceptions by status (default Pending), project, price item, supplier, submitter, category and date; paging");

        api.MapGet("/price-exceptions/summary", SummaryAsync)
            .RequirePermission(PricePermissions.Module, PermissionAction.View)
            .WithName("PriceExceptionSummary")
            .WithSummary("Price exceptions counted and summed by status, project, category, supplier, submitter and item (from, to, projectId)");

        api.MapGet("/price-exceptions/{exceptionId:int}", GetAsync)
            .RequirePermission(PricePermissions.Module, PermissionAction.View)
            .WithName("GetPriceException")
            .WithSummary("One price exception");

        api.MapPost("/price-exceptions/{exceptionId:int}/approve", ApproveOnceAsync)
            .RequirePermission(PricePermissions.Module, PermissionAction.Approve)
            .WithName("ApprovePriceExceptionOnce")
            .WithSummary("Decision A: approve this transaction as a one-time exception; the catalog is unchanged (Executive, note required)");

        api.MapPost("/price-exceptions/{exceptionId:int}/approve-with-price", ApproveWithPriceAsync)
            .RequirePermission(PricePermissions.Module, PermissionAction.Approve)
            .WithName("ApprovePriceExceptionWithPriceChange")
            .WithSummary("Decision B: approve this transaction and set a new reference range for the company from its effective date (Executive, note required)");

        api.MapPost("/price-exceptions/{exceptionId:int}/reject", RejectAsync)
            .RequirePermission(PricePermissions.Module, PermissionAction.Approve)
            .WithName("RejectPriceException")
            .WithSummary("Decision C: reject the price; the transaction stays blocked until it is corrected (Executive, reason required)");
    }

    internal static async Task<Results<Ok<ListResponse>, ValidationProblem, ProblemHttpResult>> ListAsync(
        HttpContext httpContext,
        ProjectsDbContext db,
        IUserDirectory users,
        IVisibleProjects visibleProjects,
        CancellationToken cancellationToken,
        string? status = null,
        int? projectId = null,
        int? priceItemId = null,
        int? supplierId = null,
        int? submittedBy = null,
        string? categoryCode = null,
        string? sourceType = null,
        string? from = null,
        string? to = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var wanted = Input.Clean(status) is null ? PriceExceptionStatus.Pending : Input.OptionalName<PriceExceptionStatus>(status, errors, "status");
        var all = string.Equals(Input.Clean(status), "all", StringComparison.OrdinalIgnoreCase);
        if (all)
        {
            errors = new ValidationErrors();
            wanted = null;
        }

        var fromDate = Input.OptionalDate(from, errors, "from", "The first day");
        var toDate = Input.OptionalDate(to, errors, "to", "The last day");
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var query = await ScopedAsync(db, visibleProjects, httpContext, caller.UserId, cancellationToken);
        if (wanted is { } wantedStatus)
        {
            query = query.Where(exception => exception.Status == wantedStatus);
        }

        if (projectId is { } project)
        {
            query = query.Where(exception => exception.ProjectId == project);
        }

        if (priceItemId is { } item)
        {
            query = query.Where(exception => exception.PriceItemId == item);
        }

        if (supplierId is { } supplier)
        {
            query = query.Where(exception => exception.SupplierId == supplier);
        }

        if (submittedBy is { } submitter)
        {
            query = query.Where(exception => exception.SubmittedBy == submitter);
        }

        if (Input.Clean(categoryCode) is { } category)
        {
            query = query.Where(exception => exception.PriceItem.Category.Code == category);
        }

        if (Input.Clean(sourceType) is { } source)
        {
            query = query.Where(exception => exception.SourceType == source);
        }

        if (fromDate is { } first)
        {
            query = query.Where(exception => exception.TransactionDate >= first);
        }

        if (toDate is { } last)
        {
            query = query.Where(exception => exception.TransactionDate <= last);
        }

        var total = await query.CountAsync(cancellationToken);
        var items = await PriceExceptionQueries.ViewsAsync(
            db, users, query.OrderByDescending(exception => exception.SubmittedAt).ThenByDescending(exception => exception.Id).Skip((page - 1) * pageSize).Take(pageSize),
            cancellationToken);
        return TypedResults.Ok(new ListResponse(items, page, pageSize, total));
    }

    internal static async Task<Results<Ok<PriceExceptionView>, ProblemHttpResult>> GetAsync(
        int exceptionId, HttpContext httpContext, ProjectsDbContext db, IUserDirectory users, IVisibleProjects visibleProjects, CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var query = (await ScopedAsync(db, visibleProjects, httpContext, caller.UserId, cancellationToken)).Where(exception => exception.Id == exceptionId);
        var view = (await PriceExceptionQueries.ViewsAsync(db, users, query, cancellationToken)).SingleOrDefault();
        return view is null ? PriceControlProblems.ExceptionNotFound() : TypedResults.Ok(view);
    }

    /// <summary>A group of the summary: a status, a project, a category, a supplier, a submitter or a price item.</summary>
    internal sealed record SummaryGroup(string Key, string? LabelAr, string? LabelEn, int Count, decimal VarianceTotal, int Pending, int ApprovedOnce, int ApprovedWithPriceChange, int Rejected);

    internal sealed record SummaryResponse(
        int Total,
        int Pending,
        int ApprovedOnce,
        int ApprovedWithPriceChange,
        int Rejected,
        int Withdrawn,
        decimal VarianceTotal,
        IReadOnlyList<SummaryGroup> ByProject,
        IReadOnlyList<SummaryGroup> ByCategory,
        IReadOnlyList<SummaryGroup> BySupplier,
        IReadOnlyList<SummaryGroup> BySubmitter,
        IReadOnlyList<SummaryGroup> ByPriceItem);

    internal static async Task<Results<Ok<SummaryResponse>, ValidationProblem, ProblemHttpResult>> SummaryAsync(
        HttpContext httpContext,
        ProjectsDbContext db,
        IUserDirectory users,
        IVisibleProjects visibleProjects,
        CancellationToken cancellationToken,
        string? from = null,
        string? to = null,
        int? projectId = null)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fromDate = Input.OptionalDate(from, errors, "from", "The first day");
        var toDate = Input.OptionalDate(to, errors, "to", "The last day");
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var query = await ScopedAsync(db, visibleProjects, httpContext, caller.UserId, cancellationToken);
        query = query.Where(exception => exception.Status != PriceExceptionStatus.Withdrawn || true);
        if (projectId is { } project)
        {
            query = query.Where(exception => exception.ProjectId == project);
        }

        if (fromDate is { } first)
        {
            query = query.Where(exception => exception.TransactionDate >= first);
        }

        if (toDate is { } last)
        {
            query = query.Where(exception => exception.TransactionDate <= last);
        }

        var rows = await query.Select(exception => new
        {
            exception.Status,
            exception.VarianceTotal,
            exception.ProjectId,
            ProjectCode = exception.Project!.Code,
            ProjectNameAr = exception.Project!.NameAr,
            ProjectNameEn = exception.Project!.NameEn,
            CategoryCode = exception.PriceItem.Category.Code,
            CategoryNameAr = exception.PriceItem.Category.NameAr,
            CategoryNameEn = exception.PriceItem.Category.NameEn,
            exception.SupplierId,
            SupplierNameAr = db.Suppliers.Where(supplier => supplier.Id == exception.SupplierId).Select(supplier => supplier.NameAr).FirstOrDefault(),
            SupplierNameEn = db.Suppliers.Where(supplier => supplier.Id == exception.SupplierId).Select(supplier => supplier.NameEn).FirstOrDefault(),
            exception.SubmittedBy,
            exception.PriceItemId,
            ItemNameAr = exception.PriceItem.NameAr,
            ItemNameEn = exception.PriceItem.NameEn,
        }).ToListAsync(cancellationToken);

        var people = await users.FindAsync(rows.Select(row => row.SubmittedBy).Distinct().ToList(), cancellationToken);

        SummaryGroup Group<TKey>(string key, string? ar, string? en, IEnumerable<dynamic> items)
        {
            var list = items.ToList();
            return new SummaryGroup(key, ar, en, list.Count, list.Sum(item => (decimal)item.VarianceTotal),
                list.Count(item => item.Status == PriceExceptionStatus.Pending), list.Count(item => item.Status == PriceExceptionStatus.ApprovedOnce),
                list.Count(item => item.Status == PriceExceptionStatus.ApprovedWithPriceChange), list.Count(item => item.Status == PriceExceptionStatus.Rejected));
        }

        var counted = rows.Where(row => row.Status != PriceExceptionStatus.Withdrawn).ToList();
        return TypedResults.Ok(new SummaryResponse(
            counted.Count,
            counted.Count(row => row.Status == PriceExceptionStatus.Pending),
            counted.Count(row => row.Status == PriceExceptionStatus.ApprovedOnce),
            counted.Count(row => row.Status == PriceExceptionStatus.ApprovedWithPriceChange),
            counted.Count(row => row.Status == PriceExceptionStatus.Rejected),
            rows.Count(row => row.Status == PriceExceptionStatus.Withdrawn),
            counted.Sum(row => row.VarianceTotal),
            // Office expenses have no project: they are one group of their own.
            counted.GroupBy(row => row.ProjectId).Select(group => group.Key is null
                ? Group<int>("office", "مصروفات المكتب", "Company office", group)
                : Group<int>(group.Key.ToString()!, group.First().ProjectNameAr, $"{group.First().ProjectCode} · {group.First().ProjectNameEn}", group)).OrderByDescending(group => group.VarianceTotal).ToList(),
            counted.GroupBy(row => row.CategoryCode).Select(group => Group<string>(group.Key, group.First().CategoryNameAr, group.First().CategoryNameEn, group)).OrderByDescending(group => group.VarianceTotal).ToList(),
            counted.Where(row => row.SupplierId != null).GroupBy(row => row.SupplierId!.Value).Select(group => Group<int>(group.Key.ToString(), group.First().SupplierNameAr, group.First().SupplierNameEn, group)).OrderByDescending(group => group.VarianceTotal).ToList(),
            counted.GroupBy(row => row.SubmittedBy).Select(group => Group<int>(group.Key.ToString(), people.GetValueOrDefault(group.Key)?.NameAr, people.GetValueOrDefault(group.Key)?.NameEn, group)).OrderByDescending(group => group.Count).ToList(),
            counted.GroupBy(row => row.PriceItemId).Select(group => Group<int>(group.Key.ToString(), group.First().ItemNameAr, group.First().ItemNameEn, group)).OrderByDescending(group => group.Count).ThenByDescending(group => group.VarianceTotal).Take(20).ToList()));
    }

    internal sealed record DecisionRequest(string? Note, string? RowVersion);

    internal sealed record PriceChangeRequest(
        string? Note,
        string? RowVersion,
        decimal? MinPrice,
        decimal? MaxPrice,
        decimal? ReferencePrice,
        decimal? TolerancePercent,
        string? Kind,
        string? EffectiveFrom,
        string? EffectiveTo,
        string? Conditions,
        string? SourceNote,
        bool ForSupplier = false);

    internal static Task<Results<Ok<PriceExceptionView>, ValidationProblem, ProblemHttpResult>> ApproveOnceAsync(
        int exceptionId, DecisionRequest request, HttpContext httpContext, ProjectsDbContext db, IUserDirectory users, ExecutiveGate executives,
        IEnumerable<IPriceExceptionSource> sources, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
        DecideAsync(PriceExceptionStatus.ApprovedOnce, exceptionId, request.Note, request.RowVersion, null, httpContext, db, users, executives, sources, clock, log, cancellationToken);

    internal static Task<Results<Ok<PriceExceptionView>, ValidationProblem, ProblemHttpResult>> RejectAsync(
        int exceptionId, DecisionRequest request, HttpContext httpContext, ProjectsDbContext db, IUserDirectory users, ExecutiveGate executives,
        IEnumerable<IPriceExceptionSource> sources, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
        DecideAsync(PriceExceptionStatus.Rejected, exceptionId, request.Note, request.RowVersion, null, httpContext, db, users, executives, sources, clock, log, cancellationToken);

    internal static Task<Results<Ok<PriceExceptionView>, ValidationProblem, ProblemHttpResult>> ApproveWithPriceAsync(
        int exceptionId, PriceChangeRequest request, HttpContext httpContext, ProjectsDbContext db, IUserDirectory users, ExecutiveGate executives,
        IEnumerable<IPriceExceptionSource> sources, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
        DecideAsync(PriceExceptionStatus.ApprovedWithPriceChange, exceptionId, request.Note, request.RowVersion, request, httpContext, db, users, executives, sources, clock, log, cancellationToken);

    /// <summary>
    /// The one decision flow: a reason is always required for the audit; an active Executive (checked in the database);
    /// never the submitter; only while pending; under the case's lock, and the item's lock for a catalog change. The
    /// module that owns the transaction is told in the same transaction.
    /// </summary>
    private static async Task<Results<Ok<PriceExceptionView>, ValidationProblem, ProblemHttpResult>> DecideAsync(
        PriceExceptionStatus target,
        int exceptionId,
        string? noteText,
        string? rowVersionText,
        PriceChangeRequest? change,
        HttpContext httpContext,
        ProjectsDbContext db,
        IUserDirectory users,
        ExecutiveGate executives,
        IEnumerable<IPriceExceptionSource> sources,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (httpContext.User.GetUserId() is not { } userId)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = Input.RequiredText(noteText, PriceExceptionConfiguration.TextMaxLength, errors, "note", "The reason");
        var rowVersion = RowVersions.Parse(rowVersionText, errors);
        RangeFields? range = null;
        if (change is not null)
        {
            range = RangeFields.Validate(
                new RangeInput(null, change.MinPrice, change.MaxPrice, change.EffectiveFrom, change.EffectiveTo, change.Conditions, null, change.Kind,
                    change.ReferencePrice, change.TolerancePercent, change.SourceNote),
                clock.Today, errors);
        }

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await executives.CheckAsync(httpContext.User, $"PriceException{target}", cancellationToken) is { } refusal)
        {
            return refusal;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await SqlAppLock.TryAcquireAsync(db, $"AlMotaheda.Projects.PriceException.{exceptionId}", cancellationToken))
        {
            return ProjectProblems.TryAgainLater();
        }

        var exception = await db.PriceExceptions.SingleOrDefaultAsync(candidate => candidate.Id == exceptionId, cancellationToken);
        if (exception is null)
        {
            return PriceControlProblems.ExceptionNotFound();
        }

        if (exception.SubmittedBy == userId)
        {
            log.ActionRefused(userId, $"PriceException{target}", "deciding own submission");
            return WorkspaceProblems.ReviewerIsRecorder();
        }

        if (exception.Status != PriceExceptionStatus.Pending)
        {
            return WorkspaceProblems.WrongStatus($"Only a pending price exception can be decided. This one is {exception.Status}.");
        }

        int? newRangeId = null;
        if (range is not null)
        {
            if (!await PriceItemLock.TryAcquireAsync(db, exception.PriceItemId, cancellationToken))
            {
                return ProjectProblems.TryAgainLater();
            }

            // The new range is the company's (general) unless the Executive asks for the supplier's own.
            var supplierId = change!.ForSupplier ? exception.SupplierId : null;
            var row = (range with { SupplierId = supplierId }).ToRange(PriceRangeStatus.Approved, userId, clock.UtcNow, note);
            row.PriceItemId = exception.PriceItemId;
            if (await db.PriceRanges.AnyAsync(
                    other => other.PriceItemId == row.PriceItemId && other.SupplierId == row.SupplierId && other.Status == PriceRangeStatus.Approved
                             && other.EffectiveFrom == row.EffectiveFrom, cancellationToken))
            {
                return PriceProblems.DateTaken();
            }

            db.PriceRanges.Add(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException dbException) when (SqlViolations.IsUnique(dbException, PriceRangeConfiguration.ApprovedIndex))
            {
                return PriceProblems.DateTaken();
            }

            newRangeId = row.Id;
            log.PriceRangeDecided(userId, exception.PriceItemId, row.Id, PriceRangeStatus.Approved.ToString());
        }

        exception.Decide(target, userId, clock.UtcNow, note!, newRangeId);
        db.Entry(exception).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        // The module that owns the transaction acts on the decision (Procurement posts a held invoice), in this transaction.
        if (exception.SourceType != PriceExceptionSources.ProjectExpense)
        {
            var source = sources.FirstOrDefault(candidate => candidate.SourceType == exception.SourceType);
            if (source is not null)
            {
                await source.OnDecidedAsync(
                    new PriceExceptionDecision(exception.Id, exception.SourceType, exception.SourceId, exception.ProjectId, target.ToString(), exception.IsApproved, userId, note!),
                    SharedTransaction.Of(db),
                    cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
        log.PriceExceptionDecided(userId, exception.Id, target.ToString(), newRangeId);
        return TypedResults.Ok((await PriceExceptionQueries.OneAsync(db, users, exception.Id, cancellationToken))!);
    }

    /// <summary>The cases the caller may see: on the projects they may see, plus their own submissions.</summary>
    private static async Task<IQueryable<PriceException>> ScopedAsync(
        ProjectsDbContext db, IVisibleProjects visibleProjects, HttpContext httpContext, int userId, CancellationToken cancellationToken)
    {
        var query = db.PriceExceptions.AsNoTracking();
        var visible = await visibleProjects.IdsAsync(httpContext.User, cancellationToken);
        if (visible is not null)
        {
            var ids = visible.ToList();
            query = query.Where(exception => (exception.ProjectId != null && ids.Contains(exception.ProjectId.Value)) || exception.SubmittedBy == userId);
        }

        return query;
    }
}
