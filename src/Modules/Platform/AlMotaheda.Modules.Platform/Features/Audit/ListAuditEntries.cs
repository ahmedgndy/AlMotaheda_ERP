using System.Text.Json;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Platform.Common;
using AlMotaheda.Modules.Platform.Data;
using AlMotaheda.Modules.Platform.Data.Configurations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Platform.Features.Audit;

/// <summary>
/// <c>GET /api/audit-entries</c>: the audit trail, newest first, filtered by record, project, user and company dates.
/// Executive only.
/// </summary>
/// <remarks>
/// Executive only because the trail shows every module's old and new values, salaries and personal data included.
/// Whether the Accountant should also read it (for financial records only) is an open question.
/// </remarks>
internal static class ListAuditEntries
{
    internal sealed record Filter(
        string? Module, string? EntityType, string? EntityKey, int? ProjectId, int? UserId, DateTime? FromUtc, DateTime? ToUtc);

    internal sealed record Row(
        long Id, DateTime OccurredAt, int? UserId, string Module, string EntityType, string EntityKey, string Action,
        int? ProjectId, string Changes, string? TraceId);

    internal sealed record EntryView(
        long Id,
        DateTime OccurredAt,
        int? UserId,
        string? UserNameAr,
        string? UserNameEn,
        string Module,
        string EntityType,
        string EntityKey,
        string Action,
        int? ProjectId,
        JsonElement Changes,
        string? TraceId);

    internal sealed record Response(IReadOnlyList<EntryView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/audit-entries", HandleAsync)
            .RequireRole(AccessRoles.Executive)
            .WithName(nameof(ListAuditEntries))
            .WithSummary("List the audit trail, newest first (module, entityType, entityKey, projectId, userId, from, to; page, pageSize). Executive only");

    internal static async Task<Results<Ok<Response>, ValidationProblem>> HandleAsync(
        PlatformDbContext db,
        IUserDirectory users,
        CompanyClock clock,
        CancellationToken cancellationToken,
        string? module = null,
        string? entityType = null,
        string? entityKey = null,
        int? projectId = null,
        int? userId = null,
        string? from = null,
        string? to = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        var errors = new ValidationErrors();
        var fromDate = Input.OptionalDate(from, errors, "from", "from");
        var toDate = Input.OptionalDate(to, errors, "to", "to");
        if (fromDate is { } start && toDate is { } end && end < start)
        {
            errors.Add("to", "to must be on or after from.");
        }

        Input.OptionalText(module, AuditEntryConfiguration.ModuleMaxLength, errors, "module", "module");
        Input.OptionalText(entityType, AuditEntryConfiguration.EntityTypeMaxLength, errors, "entityType", "entityType");
        Input.OptionalText(entityKey, AuditEntryConfiguration.EntityKeyMaxLength, errors, "entityKey", "entityKey");
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return PlatformProblems.Validation(errors);
        }

        var filter = new Filter(
            Input.Clean(module),
            Input.Clean(entityType),
            Input.Clean(entityKey),
            projectId,
            userId,
            fromDate is { } first ? clock.StartOfDayUtc(first) : null,
            toDate is { } last ? clock.StartOfDayUtc(last.AddDays(1)) : null);

        var query = Query(db, filter);
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);

        // One query for every name on the page, never one per row.
        var names = await users.FindAsync(rows.Where(row => row.UserId is not null).Select(row => row.UserId!.Value).Distinct().ToList(), cancellationToken);

        var items = rows.Select(row =>
        {
            var name = row.UserId is { } id && names.TryGetValue(id, out var user) ? user : null;
            using var changes = JsonDocument.Parse(row.Changes);
            return new EntryView(
                row.Id, row.OccurredAt, row.UserId, name?.NameAr, name?.NameEn, row.Module, row.EntityType, row.EntityKey,
                row.Action, row.ProjectId, changes.RootElement.Clone(), row.TraceId);
        }).ToList();

        return TypedResults.Ok(new Response(items, page, pageSize, totalCount));
    }

    /// <summary>The filtered trail, newest first. A separate method so a test can check its SQL translation.</summary>
    internal static IQueryable<Row> Query(PlatformDbContext db, Filter filter)
    {
        var query = db.AuditEntries.AsNoTracking();

        if (filter.Module is { } moduleName)
        {
            query = query.Where(entry => entry.Module == moduleName);
        }

        if (filter.EntityType is { } type)
        {
            query = query.Where(entry => entry.EntityType == type);
        }

        if (filter.EntityKey is { } key)
        {
            query = query.Where(entry => entry.EntityKey == key);
        }

        if (filter.ProjectId is { } project)
        {
            query = query.Where(entry => entry.ProjectId == project);
        }

        if (filter.UserId is { } user)
        {
            query = query.Where(entry => entry.UserId == user);
        }

        if (filter.FromUtc is { } fromUtc)
        {
            query = query.Where(entry => entry.OccurredAt >= fromUtc);
        }

        if (filter.ToUtc is { } toUtc)
        {
            query = query.Where(entry => entry.OccurredAt < toUtc);
        }

        return query
            .OrderByDescending(entry => entry.Id)
            .Select(entry => new Row(
                entry.Id, entry.OccurredAt, entry.UserId, entry.Module, entry.EntityType, entry.EntityKey, entry.Action,
                entry.ProjectId, entry.Changes, entry.TraceId));
    }
}
