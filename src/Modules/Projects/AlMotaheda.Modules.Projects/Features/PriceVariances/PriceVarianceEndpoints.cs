using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Features.Catalog;
using AlMotaheda.Modules.Projects.Features.Projects;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.PriceVariances;

/// <summary>
/// A price variance requiring review. Unit prices are prices (catalog View, like the catalog); the total variance is
/// money and needs catalog See money.
/// </summary>
internal sealed record PriceVarianceView(
    int Id,
    int ProjectId,
    int ExpenseId,
    DateOnly ExpenseDate,
    int CatalogItemId,
    string CatalogItemNameAr,
    string CatalogItemNameEn,
    int? SupplierId,
    string? SupplierNameAr,
    string? SupplierNameEn,
    decimal ReferencePrice,
    decimal ActualPrice,
    decimal Quantity,
    decimal UnitVariance,
    decimal? TotalVariance,
    decimal VariancePercent,
    string Direction,
    decimal TolerancePercent,
    decimal RoundingAllowance,
    string Status,
    DateTime RaisedAt,
    PersonRef RaisedBy,
    DateTime? DecidedAt,
    PersonRef? DecidedBy,
    string? DecisionNote,
    string RowVersion);

/// <summary>One variance, flat, as the queries read it.</summary>
internal sealed record PriceVarianceRow(
    int Id,
    int ProjectId,
    int ExpenseId,
    DateOnly ExpenseDate,
    int CatalogItemId,
    string CatalogItemNameAr,
    string CatalogItemNameEn,
    int? SupplierId,
    string? SupplierNameAr,
    string? SupplierNameEn,
    decimal ReferencePrice,
    decimal ActualPrice,
    decimal Quantity,
    decimal UnitVariance,
    decimal TotalVariance,
    decimal VariancePercent,
    VarianceDirection Direction,
    decimal TolerancePercent,
    decimal RoundingAllowance,
    PriceVarianceStatus Status,
    DateTime RaisedAt,
    int RaisedBy,
    DateTime? DecidedAt,
    int? DecidedBy,
    string? DecisionNote,
    byte[] RowVersion)
{
    public PriceVarianceView ToView(IReadOnlyDictionary<int, UserSummary> people, bool seesMoney) => new(
        Id, ProjectId, ExpenseId, ExpenseDate, CatalogItemId, CatalogItemNameAr, CatalogItemNameEn, SupplierId, SupplierNameAr, SupplierNameEn,
        ReferencePrice, ActualPrice, Quantity, UnitVariance, seesMoney ? TotalVariance : null, VariancePercent, Direction.ToString(),
        TolerancePercent, RoundingAllowance, Status.ToString(), RaisedAt, PersonRef.From(RaisedBy, people), DecidedAt,
        DecidedBy is { } decider ? PersonRef.From(decider, people) : null, DecisionNote, RowVersions.Format(RowVersion));

    public static async Task<IReadOnlyList<PriceVarianceView>> ViewsAsync(
        IReadOnlyList<PriceVarianceRow> rows, IUserDirectory users, bool seesMoney, CancellationToken cancellationToken)
    {
        var people = await PlanningPeople.FindAsync(
            users, rows.SelectMany(row => row.DecidedBy is { } decider ? new[] { row.RaisedBy, decider } : [row.RaisedBy]), cancellationToken);
        return rows.Select(row => row.ToView(people, seesMoney)).ToList();
    }
}

/// <summary>The variance queries, as <see cref="IQueryable{T}"/> so tests can prove they translate to SQL.</summary>
internal static class PriceVarianceQueries
{
    /// <param name="projectIds">The projects to read, or <c>null</c> for every project.</param>
    public static IQueryable<PriceVarianceRow> Filtered(ProjectsDbContext db, IReadOnlyCollection<int>? projectIds, PriceVarianceStatus? status)
    {
        var query = db.PriceVariances.AsNoTracking();
        if (projectIds is not null)
        {
            query = query.Where(variance => projectIds.Contains(variance.ProjectId));
        }

        if (status is { } wanted)
        {
            query = query.Where(variance => variance.Status == wanted);
        }

        return Project(query.OrderByDescending(variance => variance.RaisedAt).ThenByDescending(variance => variance.Id), db);
    }

    public static IQueryable<PriceVarianceRow> One(ProjectsDbContext db, int projectId, int varianceId) =>
        Project(db.PriceVariances.AsNoTracking().Where(variance => variance.ProjectId == projectId && variance.Id == varianceId), db);

    private static IQueryable<PriceVarianceRow> Project(IQueryable<PriceVariance> query, ProjectsDbContext db) =>
        query.Select(variance => new PriceVarianceRow(
            variance.Id,
            variance.ProjectId,
            variance.ExpenseId,
            variance.Expense.ExpenseDate,
            variance.CatalogItemId,
            db.CatalogItems.Where(item => item.Id == variance.CatalogItemId).Select(item => item.NameAr).First(),
            db.CatalogItems.Where(item => item.Id == variance.CatalogItemId).Select(item => item.NameEn).First(),
            variance.Expense.SupplierId,
            variance.Expense.Supplier != null ? variance.Expense.Supplier.NameAr : null,
            variance.Expense.Supplier != null ? variance.Expense.Supplier.NameEn : null,
            variance.ReferencePrice,
            variance.ActualPrice,
            variance.Quantity,
            variance.UnitVariance,
            variance.TotalVariance,
            variance.VariancePercent,
            variance.Direction,
            variance.TolerancePercent,
            variance.RoundingAllowance,
            variance.Status,
            variance.RaisedAt,
            variance.RaisedBy,
            variance.DecidedAt,
            variance.DecidedBy,
            variance.DecisionNote,
            variance.RowVersion));
}

/// <summary><c>GET /api/projects/{pid}/price-variances</c>: one project's price variances, newest first.</summary>
internal static class ListProjectPriceVariances
{
    internal sealed record Response(IReadOnlyList<PriceVarianceView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/price-variances", HandleAsync)
            .RequirePermission(CatalogPermissions.Module, PermissionAction.View)
            .WithName("ListProjectPriceVariances")
            .WithSummary("List a project's price variances, newest first (status: Open, Accepted, Rejected, Superseded)");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CancellationToken cancellationToken,
        string? status = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        var statusFilter = Input.OptionalName<PriceVarianceStatus>(status, errors, "status");
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var query = PriceVarianceQueries.Filtered(db, [projectId], statusFilter);
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var seesMoney = httpContext.User.HasPermission(CatalogPermissions.Module, PermissionAction.SeeMoney);

        return TypedResults.Ok(new Response(await PriceVarianceRow.ViewsAsync(rows, users, seesMoney, cancellationToken), page, pageSize, totalCount));
    }
}

/// <summary>
/// <c>GET /api/catalog/price-variances</c>: the price variances of every project the caller may see, newest first;
/// with <c>status=Open</c>, the Financial Manager's review queue.
/// </summary>
internal static class ListPriceVariances
{
    internal sealed record Response(IReadOnlyList<PriceVarianceView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/catalog/price-variances", HandleAsync)
            .RequirePermission(CatalogPermissions.Module, PermissionAction.View)
            .WithName("ListPriceVariances")
            .WithSummary("List the price variances of the projects you can see (status; projectId)");

    internal static async Task<Results<Ok<Response>, ValidationProblem>> HandleAsync(
        HttpContext httpContext,
        ProjectsDbContext db,
        IVisibleProjects visible,
        IUserDirectory users,
        CancellationToken cancellationToken,
        string? status = null,
        int? projectId = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        var statusFilter = Input.OptionalName<PriceVarianceStatus>(status, errors, "status");
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        IReadOnlyCollection<int>? scope = await visible.IdsAsync(httpContext.User, cancellationToken);
        if (projectId is { } wanted)
        {
            scope = scope is null || scope.Contains(wanted) ? [wanted] : [];
        }

        var query = PriceVarianceQueries.Filtered(db, scope, statusFilter);
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var seesMoney = httpContext.User.HasPermission(CatalogPermissions.Module, PermissionAction.SeeMoney);

        return TypedResults.Ok(new Response(await PriceVarianceRow.ViewsAsync(rows, users, seesMoney, cancellationToken), page, pageSize, totalCount));
    }
}

/// <summary>The body of accept and reject: a reason is required for both (it is the audit of the decision).</summary>
internal sealed record PriceVarianceDecisionRequest(string? Note, string? RowVersion);

/// <summary>
/// <c>POST /api/projects/{pid}/price-variances/{id}/accept</c> and <c>/reject</c>: catalog Approve re-checked in the
/// database (the Financial Manager or an Executive), never by the person who recorded the supply. Accepting allows
/// the supply to be approved at its price; it never changes any catalog price. Runs under the project lock, like the
/// supply's review, so a decision and an approval never cross.
/// </summary>
internal static class DecidePriceVariance
{
    public static void Map(IEndpointRouteBuilder projects)
    {
        projects.MapPost("/{projectId:int}/price-variances/{varianceId:int}/accept",
                (int projectId, int varianceId, PriceVarianceDecisionRequest request, HttpContext httpContext, ProjectsDbContext db, IProjectAccess access,
                    IUserDirectory users, PermissionGate gate, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
                    HandleAsync(PriceVarianceStatus.Accepted, projectId, varianceId, request, httpContext, db, access, users, gate, clock, log, cancellationToken))
            .RequirePermission(CatalogPermissions.Module, PermissionAction.Approve)
            .WithName("AcceptPriceVariance")
            .WithSummary("Accept a supply's price variance with a reason (this supply only). 409 own_record, price_variance_decided");

        projects.MapPost("/{projectId:int}/price-variances/{varianceId:int}/reject",
                (int projectId, int varianceId, PriceVarianceDecisionRequest request, HttpContext httpContext, ProjectsDbContext db, IProjectAccess access,
                    IUserDirectory users, PermissionGate gate, CompanyClock clock, ProjectsLog log, CancellationToken cancellationToken) =>
                    HandleAsync(PriceVarianceStatus.Rejected, projectId, varianceId, request, httpContext, db, access, users, gate, clock, log, cancellationToken))
            .RequirePermission(CatalogPermissions.Module, PermissionAction.Approve)
            .WithName("RejectPriceVariance")
            .WithSummary("Reject a supply's price variance with a reason: the supply can't be approved until it is fixed. 409 own_record, price_variance_decided");
    }

    internal static async Task<Results<Ok<PriceVarianceView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        PriceVarianceStatus target,
        int projectId,
        int varianceId,
        PriceVarianceDecisionRequest request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        PermissionGate gate,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var note = Input.RequiredText(request.Note, PriceVarianceConfiguration.NoteMaxLength, errors, "note", "The reason");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
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

        if (await gate.CheckAsync(httpContext.User, CatalogPermissions.Module, PermissionAction.Approve, $"PriceVariance{target}", cancellationToken) is { } refused)
        {
            return refused;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await ProjectLock.TryAcquireAsync(db, projectId, cancellationToken))
        {
            return ProjectProblems.TryAgainLater();
        }

        var variance = await db.PriceVariances.Include(candidate => candidate.Expense)
            .SingleOrDefaultAsync(candidate => candidate.Id == varianceId && candidate.ProjectId == projectId, cancellationToken);
        if (variance is null)
        {
            return CostFlowProblems.PriceVarianceNotFound();
        }

        if (variance.RaisedBy == caller.UserId || variance.Expense.CreatedBy == caller.UserId)
        {
            log.ActionRefused(caller.UserId, $"PriceVariance{target}", "deciding own supply's variance");
            return WorkspaceProblems.ReviewerIsRecorder();
        }

        if (variance.Status != PriceVarianceStatus.Open)
        {
            return CostFlowProblems.VarianceDecided(variance.Status.ToString());
        }

        variance.Decide(target, caller.UserId, clock.UtcNow, note!);
        db.Entry(variance).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.PriceVarianceDecided(caller.UserId, projectId, variance.Id, target.ToString());

        var row = await PriceVarianceQueries.One(db, projectId, variance.Id).SingleAsync(cancellationToken);
        var seesMoney = httpContext.User.HasPermission(CatalogPermissions.Module, PermissionAction.SeeMoney);
        return TypedResults.Ok((await PriceVarianceRow.ViewsAsync([row], users, seesMoney, cancellationToken))[0]);
    }
}

/// <summary>The price-variance thresholds, as the policy screens show them.</summary>
internal sealed record PricingPolicyView(decimal VarianceTolerancePercent, decimal RoundingAllowance, DateTime? UpdatedAt, int? UpdatedBy, string RowVersion)
{
    public static PricingPolicyView From(PricingPolicy policy) =>
        new(policy.VarianceTolerancePercent, policy.RoundingAllowance, policy.UpdatedAt, policy.UpdatedBy, RowVersions.Format(policy.RowVersion));
}

/// <summary><c>GET /api/catalog/pricing-policy</c>: the variance thresholds (catalog View).</summary>
internal static class GetPricingPolicy
{
    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/catalog/pricing-policy", HandleAsync)
            .RequirePermission(CatalogPermissions.Module, PermissionAction.View)
            .WithName("GetPricingPolicy")
            .WithSummary("Get the price-variance thresholds");

    internal static async Task<Ok<PricingPolicyView>> HandleAsync(ProjectsDbContext db, CancellationToken cancellationToken) =>
        TypedResults.Ok(PricingPolicyView.From(await db.PricingPolicies.AsNoTracking().SingleAsync(cancellationToken)));
}

/// <summary>
/// <c>PUT /api/catalog/pricing-policy</c>: change the variance thresholds. Executive only, re-checked in the database
/// (a looser threshold weakens the control). The change is in the audit trail.
/// </summary>
internal static class UpdatePricingPolicy
{
    internal sealed record Request(decimal? VarianceTolerancePercent, decimal? RoundingAllowance, string? RowVersion);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPut("/catalog/pricing-policy", HandleAsync)
            .RequireExecutive()
            .WithName("UpdatePricingPolicy")
            .WithSummary("Change the price-variance thresholds (Executive only)");

    internal static async Task<Results<Ok<PricingPolicyView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        ExecutiveGate gate,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        if (request.VarianceTolerancePercent is not { } tolerance || tolerance < 0 || tolerance > 100 || decimal.Round(tolerance, 2) != tolerance)
        {
            errors.Add("varianceTolerancePercent", "The tolerance must be between 0 and 100, with at most two decimals.");
        }

        if (request.RoundingAllowance is not { } rounding || rounding < 0 || rounding > 1_000 || decimal.Round(rounding, 2) != rounding)
        {
            errors.Add("roundingAllowance", "The rounding allowance must be between 0 and 1000 EGP, with at most two decimals.");
        }

        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await gate.CheckAsync(httpContext.User, nameof(UpdatePricingPolicy), cancellationToken) is { } refused)
        {
            return refused;
        }

        var policy = await db.PricingPolicies.SingleAsync(cancellationToken);
        policy.VarianceTolerancePercent = request.VarianceTolerancePercent!.Value;
        policy.RoundingAllowance = request.RoundingAllowance!.Value;
        policy.UpdatedAt = clock.UtcNow;
        policy.UpdatedBy = httpContext.User.GetUserId();
        db.Entry(policy).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.PricingPolicyChanged(policy.UpdatedBy ?? 0);
        return TypedResults.Ok(PricingPolicyView.From(policy));
    }
}
