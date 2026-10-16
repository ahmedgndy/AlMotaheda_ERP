using System.Text.Json;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Reports.Common;
using AlMotaheda.Modules.Reports.Contracts;
using AlMotaheda.Modules.Reports.Data;
using AlMotaheda.Modules.Reports.Engine;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Reports.Features;

/// <summary>
/// The report endpoints (14-reports-design.md §5). Every endpoint needs a signed-in user; each report then needs one
/// of its own permissions (<see cref="ReportDefinition.AnyOf"/>) and the owning module applies its data scope.
/// </summary>
internal static class ReportEndpoints
{
    internal sealed record CatalogResponse(IReadOnlyList<ReportDefinition> Reports);

    internal sealed record RunResponse(
        ReportDefinition Report, IReadOnlyList<ReportRow> Rows, IReadOnlyDictionary<string, object?> Totals, string? Note, int Page, int PageSize, int TotalCount);

    internal sealed record QueuedResponse(long ExportId, string Status);

    internal sealed record RunView(
        long Id, string ReportCode, string Kind, string Status, string Filters, string Language, int? RowCount, string? FileName, string? Error,
        DateTime RequestedAt, DateTime? CompletedAt, DateTime? ExpiresAt, bool Downloadable);

    internal sealed record RunsResponse(IReadOnlyList<RunView> Items, int Page, int PageSize, int TotalCount);

    public const string ExcelContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/reports", Catalog)
            .WithName("ListReports")
            .WithSummary("The reports the caller may run, with their filters and columns");

        api.MapGet("/reports/{code}", RunAsync)
            .WithName("RunReport")
            .WithSummary("Run a report: one page of rows, and the totals of every row matching the filters");

        api.MapGet("/reports/{code}/excel", ExcelAsync)
            .WithName("ExportReport")
            .WithSummary("The whole report as an .xlsx file; a large one is built in the background (202) and announced when ready");

        api.MapGet("/reports/{code}/print", PrintAsync)
            .WithName("PrintReport")
            .WithSummary("The whole report as a print-ready page (HTML, A4, right-to-left for Arabic)");

        api.MapGet("/report-exports", ListRunsAsync)
            .WithName("ListReportExports")
            .WithSummary("My report exports and prints, newest first");

        api.MapGet("/report-exports/{exportId:long}/file", DownloadAsync)
            .WithName("DownloadReportExport")
            .WithSummary("Download a background export of mine while it is kept");
    }

    internal static Ok<CatalogResponse> Catalog(HttpContext httpContext, ReportCatalog catalog) =>
        TypedResults.Ok(new CatalogResponse(catalog.All.Where(definition => ReportCatalog.CanRun(httpContext.User, definition)).ToList()));

    internal static async Task<Results<Ok<RunResponse>, ValidationProblem, ProblemHttpResult>> RunAsync(
        string code, HttpContext httpContext, ReportRunner runner, CancellationToken cancellationToken, int page = 1, int pageSize = Paging.DefaultPageSize)
    {
        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return ReportsProblems.Validation(errors);
        }

        var outcome = await runner.RunAsync(code, Values(httpContext), httpContext.User, cancellationToken);
        if (Refusal(outcome) is { } refusal)
        {
            return refusal;
        }

        if (outcome.Errors is { } invalid)
        {
            return ReportsProblems.Validation(invalid);
        }

        var rows = outcome.Data!.Rows;
        return TypedResults.Ok(new RunResponse(
            outcome.Definition!, rows.Skip((page - 1) * pageSize).Take(pageSize).ToList(), ReportTotals.Of(outcome.Definition!, rows),
            outcome.Data.Note, page, pageSize, rows.Count));
    }

    internal static async Task<Results<FileContentHttpResult, Accepted<QueuedResponse>, ValidationProblem, ProblemHttpResult>> ExcelAsync(
        string code, HttpContext httpContext, ReportRunner runner, ReportsDbContext db, CompanyClock clock, IOptions<ReportsOptions> options,
        ReportsLog log, CancellationToken cancellationToken, string? lang = null)
    {
        if (httpContext.User.GetUserId() is not { } userId)
        {
            return ApiProblem.ForbiddenResult();
        }

        var language = ReportRunner.Language(lang);
        var outcome = await runner.RunAsync(code, Values(httpContext), httpContext.User, cancellationToken);
        if (Refusal(outcome) is { } refusal)
        {
            return refusal;
        }

        if (outcome.Errors is { } invalid)
        {
            return ReportsProblems.Validation(invalid);
        }

        var run = new ReportRun
        {
            ReportCode = outcome.Definition!.Code,
            Filters = JsonSerializer.Serialize(outcome.Filters),
            Language = language,
            RequestedAt = clock.UtcNow,
            RequestedBy = userId,
        };

        // Large reports are built in the background; the user is told when the file is ready.
        if (outcome.Data!.Rows.Count > options.Value.ImmediateExportRowLimit)
        {
            run.Kind = ReportRunKind.BackgroundExcel;
            run.Claims = JsonSerializer.Serialize(httpContext.User.Claims.Select(claim => new[] { claim.Type, claim.Value }));
            db.Runs.Add(run);
            await db.SaveChangesAsync(cancellationToken);
            log.ExportQueued(userId, run.Id, run.ReportCode);
            return TypedResults.Accepted($"/api/report-exports/{run.Id}", new QueuedResponse(run.Id, run.Status.ToString()));
        }

        var document = await runner.DocumentAsync(outcome, language, userId, cancellationToken);
        var file = ExcelReport.Write(document);
        run.Kind = ReportRunKind.Excel;
        run.Start();
        run.Complete(outcome.Data.Rows.Count, document.FileName, null, clock.UtcNow, null);
        db.Runs.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        log.Exported(userId, run.Id, run.ReportCode, run.RowCount ?? 0);

        return TypedResults.File(file, ExcelContentType, document.FileName);
    }

    internal static async Task<Results<ContentHttpResult, ValidationProblem, ProblemHttpResult>> PrintAsync(
        string code, HttpContext httpContext, ReportRunner runner, ReportsDbContext db, CompanyClock clock, ReportsLog log,
        CancellationToken cancellationToken, string? lang = null)
    {
        if (httpContext.User.GetUserId() is not { } userId)
        {
            return ApiProblem.ForbiddenResult();
        }

        var language = ReportRunner.Language(lang);
        var outcome = await runner.RunAsync(code, Values(httpContext), httpContext.User, cancellationToken);
        if (Refusal(outcome) is { } refusal)
        {
            return refusal;
        }

        if (outcome.Errors is { } invalid)
        {
            return ReportsProblems.Validation(invalid);
        }

        var document = await runner.DocumentAsync(outcome, language, userId, cancellationToken);
        var run = new ReportRun
        {
            ReportCode = outcome.Definition!.Code,
            Kind = ReportRunKind.Print,
            Filters = JsonSerializer.Serialize(outcome.Filters),
            Language = language,
            RequestedAt = clock.UtcNow,
            RequestedBy = userId,
        };
        run.Start();
        run.Complete(outcome.Data!.Rows.Count, $"{outcome.Definition.Code}.html", null, clock.UtcNow, null);
        db.Runs.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        log.Printed(userId, run.Id, run.ReportCode, run.RowCount ?? 0);

        return TypedResults.Content(PrintReport.Write(document), "text/html; charset=utf-8");
    }

    internal static async Task<Results<Ok<RunsResponse>, ValidationProblem, ProblemHttpResult>> ListRunsAsync(
        HttpContext httpContext, ReportsDbContext db, CompanyClock clock, CancellationToken cancellationToken, int page = 1, int pageSize = Paging.DefaultPageSize)
    {
        if (httpContext.User.GetUserId() is not { } userId)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return ReportsProblems.Validation(errors);
        }

        var now = clock.UtcNow;
        var query = db.Runs.AsNoTracking().Where(run => run.RequestedBy == userId);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.OrderByDescending(run => run.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(run => new RunView(run.Id, run.ReportCode, run.Kind.ToString(), run.Status.ToString(), run.Filters, run.Language, run.RowCount,
                run.FileName, run.Error, run.RequestedAt, run.CompletedAt, run.ExpiresAt,
                run.Kind == ReportRunKind.BackgroundExcel && run.Status == ReportRunStatus.Ready && run.Content != null && run.ExpiresAt > now))
            .ToListAsync(cancellationToken);
        return TypedResults.Ok(new RunsResponse(items, page, pageSize, total));
    }

    internal static async Task<Results<FileContentHttpResult, ProblemHttpResult>> DownloadAsync(
        long exportId, HttpContext httpContext, ReportsDbContext db, CompanyClock clock, ReportsLog log, CancellationToken cancellationToken)
    {
        if (httpContext.User.GetUserId() is not { } userId)
        {
            return ApiProblem.ForbiddenResult();
        }

        // Only the requester's own exports: another user's id answers like a missing one.
        var run = await db.Runs.SingleOrDefaultAsync(candidate => candidate.Id == exportId && candidate.RequestedBy == userId, cancellationToken);
        if (run is null)
        {
            return ReportsProblems.ExportNotFound();
        }

        var now = clock.UtcNow;
        if (run.Status != ReportRunStatus.Ready || run.Content is null || run.ExpiresAt <= now)
        {
            return ReportsProblems.NotReady(run.Status.ToString());
        }

        run.LastDownloadedAt = now;
        await db.SaveChangesAsync(cancellationToken);
        log.Downloaded(userId, run.Id);
        return TypedResults.File(run.Content, ExcelContentType, run.FileName);
    }

    /// <summary>The report's filters from the query string (everything except paging and language).</summary>
    private static Dictionary<string, string?> Values(HttpContext httpContext) =>
        httpContext.Request.Query
            .Where(entry => !ReportRunner.ReservedNames.Contains(entry.Key))
            .ToDictionary(entry => entry.Key, entry => (string?)entry.Value.ToString(), StringComparer.Ordinal);

    /// <summary>An unknown report, or a project outside the caller's scope: 404. A report the caller may not run: 403.</summary>
    private static ProblemHttpResult? Refusal(ReportOutcome outcome) =>
        outcome.NotFound ? ReportsProblems.ReportNotFound()
        : outcome.Forbidden ? ApiProblem.ForbiddenResult()
        : null;
}
