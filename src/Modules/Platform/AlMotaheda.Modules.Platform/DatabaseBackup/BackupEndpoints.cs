using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Platform.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Platform.DatabaseBackup;

internal sealed record BackupSettingsView(
    bool Enabled, string DailyAt, string Folder, int RetentionCount, bool IncludeDocuments, bool RunRequested, bool SupportedOnThisServer,
    DateTime? UpdatedAt, int? UpdatedBy, string RowVersion)
{
    public static BackupSettingsView From(BackupSettings settings, bool supported) => new(
        settings.Enabled, settings.DailyAt.ToString("HH:mm", CultureInfo.InvariantCulture), settings.Folder, settings.RetentionCount,
        settings.IncludeDocuments, settings.RunRequestedAt is not null, supported, settings.UpdatedAt, settings.UpdatedBy,
        RowVersions.Format(settings.RowVersion));
}

internal sealed record BackupRunView(
    long Id, string Trigger, int? RequestedBy, DateTime StartedAt, DateTime? CompletedAt, string Status, string Database, string? FileName,
    long? SizeBytes, bool Verified, bool Compressed, string? DocumentsFileName, long? DocumentsSizeBytes, string? Error, DateTime? FilesDeletedAt)
{
    public static BackupRunView From(BackupRun run) => new(
        run.Id, run.Trigger.ToString(), run.RequestedBy, run.StartedAt, run.CompletedAt, run.Status.ToString(), run.Database,
        run.FilePath is null ? null : Path.GetFileName(run.FilePath), run.SizeBytes, run.Verified, run.Compressed,
        run.DocumentsFilePath is null ? null : Path.GetFileName(run.DocumentsFilePath), run.DocumentsSizeBytes, run.Error, run.FilesDeletedAt);
}

/// <summary>
/// Backup settings and history, Executive only (the role is re-checked in the database for every change):
/// <c>GET</c>/<c>PUT /api/settings/backup</c>, <c>POST /api/settings/backup/run</c> ("Run backup now"),
/// <c>GET /api/backups</c> (history, newest first). Every settings change is written to the audit trail.
/// </summary>
internal static class BackupEndpoints
{
    internal sealed record Request(bool? Enabled, string? DailyAt, string? Folder, int? RetentionCount, bool? IncludeDocuments, string? RowVersion);

    internal sealed record HistoryResponse(IReadOnlyList<BackupRunView> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/settings/backup", GetAsync).RequireRole(AccessRoles.Executive)
            .WithName("GetBackupSettings").WithSummary("The database backup settings (Executive)");
        api.MapPut("/settings/backup", UpdateAsync).RequireRole(AccessRoles.Executive)
            .WithName("UpdateBackupSettings").WithSummary("Change the backup settings: on/off, daily time, folder, how many to keep (Executive, audited)");
        api.MapPost("/settings/backup/run", RunNowAsync).RequireRole(AccessRoles.Executive)
            .WithName("RunBackupNow").WithSummary("Run a backup now (Executive); it starts within seconds and appears in the history");
        api.MapGet("/backups", HistoryAsync).RequireRole(AccessRoles.Executive)
            .WithName("ListBackups").WithSummary("Backup history, newest first: date, size, status, verified, error (Executive)");
    }

    private static bool Supported(IOptions<BackupOptions> options) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) || options.Value.AllowNonWindows;

    internal static async Task<Ok<BackupSettingsView>> GetAsync(PlatformDbContext db, IOptions<BackupOptions> options, CancellationToken cancellationToken) =>
        TypedResults.Ok(BackupSettingsView.From(await db.BackupSettings.AsNoTracking().SingleAsync(cancellationToken), Supported(options)));

    internal static async Task<Results<Ok<BackupSettingsView>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        Request request, HttpContext httpContext, PlatformDbContext db, IExecutiveCheck executives, CompanyClock clock, IOptions<BackupOptions> options,
        CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        if (request.Enabled is null)
        {
            errors.Add("enabled", "enabled is required (true or false).");
        }

        if (!TimeOnly.TryParseExact(request.DailyAt ?? "", "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dailyAt))
        {
            errors.Add("dailyAt", "dailyAt must be a time written as HH:mm, for example 02:00.");
        }

        var folder = Input.RequiredText(request.Folder, 260, errors, "folder", "The backup folder");
        if (folder is not null && !Path.IsPathFullyQualified(folder))
        {
            errors.Add("folder", "The backup folder must be a full local path, for example D:\\Backups\\AlMotaheda.");
        }

        if (request.RetentionCount is not (>= 1 and <= 365))
        {
            errors.Add("retentionCount", "retentionCount must be between 1 and 365.");
        }

        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return ApiProblem.Validation(errors.ToDictionary());
        }

        if (await ExecutiveAsync(httpContext, executives, cancellationToken) is not { } userId)
        {
            return ApiProblem.ForbiddenResult("Only an active Executive can change the backup settings.");
        }

        var settings = await db.BackupSettings.SingleAsync(cancellationToken);
        var before = Snapshot(settings);
        settings.Enabled = request.Enabled!.Value;
        settings.DailyAt = dailyAt;
        settings.Folder = folder!;
        settings.RetentionCount = request.RetentionCount!.Value;
        settings.IncludeDocuments = request.IncludeDocuments ?? settings.IncludeDocuments;
        settings.UpdatedAt = clock.UtcNow;
        settings.UpdatedBy = userId;
        db.Entry(settings).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        // This module's context isn't audited automatically (it owns the audit table), so the change is written here.
        db.AuditEntries.Add(new AuditEntry
        {
            OccurredAt = clock.UtcNow,
            UserId = userId,
            Module = "platform",
            EntityType = nameof(BackupSettings),
            EntityKey = "1",
            Action = "Modified",
            Changes = JsonSerializer.Serialize(new { before, after = Snapshot(settings) }),
            TraceId = httpContext.TraceIdentifier,
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        return TypedResults.Ok(BackupSettingsView.From(settings, Supported(options)));
    }

    internal static async Task<Results<Accepted<BackupSettingsView>, ProblemHttpResult>> RunNowAsync(
        HttpContext httpContext, PlatformDbContext db, IExecutiveCheck executives, BackupSignal signal, CompanyClock clock,
        IOptions<BackupOptions> options, CancellationToken cancellationToken)
    {
        if (await ExecutiveAsync(httpContext, executives, cancellationToken) is not { } userId)
        {
            return ApiProblem.ForbiddenResult("Only an active Executive can run a backup.");
        }

        if (!Supported(options))
        {
            return ApiProblem.Create(StatusCodes.Status409Conflict, "backup_not_supported", "Backups run on the Windows database server only.");
        }

        var settings = await db.BackupSettings.SingleAsync(cancellationToken);
        settings.RunRequestedAt = clock.UtcNow;
        settings.RunRequestedBy = userId;
        db.AuditEntries.Add(new AuditEntry
        {
            OccurredAt = clock.UtcNow, UserId = userId, Module = "platform", EntityType = nameof(BackupSettings), EntityKey = "1",
            Action = "Modified", Changes = """{"runRequested":true}""", TraceId = httpContext.TraceIdentifier,
        });
        await db.SaveChangesAsync(cancellationToken);
        signal.Wake();

        return TypedResults.Accepted("/api/backups", BackupSettingsView.From(settings, true));
    }

    internal static async Task<Results<Ok<HistoryResponse>, ValidationProblem>> HistoryAsync(
        PlatformDbContext db, CancellationToken cancellationToken, int page = 1, int pageSize = Paging.DefaultPageSize)
    {
        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return ApiProblem.Validation(errors.ToDictionary());
        }

        var total = await db.BackupRuns.CountAsync(cancellationToken);
        var runs = await db.BackupRuns.AsNoTracking().OrderByDescending(run => run.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return TypedResults.Ok(new HistoryResponse(runs.Select(BackupRunView.From).ToList(), page, pageSize, total));
    }

    private static async Task<int?> ExecutiveAsync(HttpContext httpContext, IExecutiveCheck executives, CancellationToken cancellationToken) =>
        httpContext.User.GetUserId() is { } userId && httpContext.User.GetSessionId() is { } session
        && await executives.IsActiveExecutiveAsync(userId, session, cancellationToken)
            ? userId
            : null;

    private static object Snapshot(BackupSettings settings) =>
        new { settings.Enabled, DailyAt = settings.DailyAt.ToString("HH:mm", CultureInfo.InvariantCulture), settings.Folder, settings.RetentionCount, settings.IncludeDocuments };
}
