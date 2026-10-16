using AlMotaheda.BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Reports.Common;

/// <summary>Settings section <c>Reports</c>.</summary>
internal sealed class ReportsOptions
{
    public const string SectionName = "Reports";

    /// <summary>Off only for test hosts without a database: the background export worker never starts.</summary>
    public bool BackgroundExports { get; set; } = true;

    /// <summary>Above this many rows an Excel export is built in the background.</summary>
    public int ImmediateExportRowLimit { get; set; } = 5_000;

    /// <summary>How long a background export's file is kept.</summary>
    public int KeepExportsDays { get; set; } = 7;

    /// <summary>How often the background worker looks for queued exports.</summary>
    public int PollSeconds { get; set; } = 5;
}

internal static class ReportsProblems
{
    public const string ExportNotReady = "export_not_ready";

    public static ValidationProblem Validation(ValidationErrors errors) => ApiProblem.Validation(errors.ToDictionary());

    /// <summary>An unknown report, or one for a project the caller may not see: the same answer.</summary>
    public static ProblemHttpResult ReportNotFound() => ApiProblem.NotFoundResult("The report was not found.");

    public static ProblemHttpResult ExportNotFound() => ApiProblem.NotFoundResult("The export was not found.");

    public static ProblemHttpResult NotReady(string status) =>
        ApiProblem.Create(StatusCodes.Status409Conflict, ExportNotReady, $"The export is {status}, or its file is no longer kept.");
}

/// <summary>Structured events of the Reports module (event ids 11001 to 11999). Ids and report codes only.</summary>
internal sealed partial class ReportsLog(ILogger<ReportsLog> logger)
{
    private readonly ILogger<ReportsLog> _logger = logger;

    [LoggerMessage(EventId = 11001, Level = LogLevel.Information, Message = "User {ActorId} exported report {ReportCode} ({Rows} rows), run {RunId}")]
    public partial void Exported(int actorId, long runId, string reportCode, int rows);

    [LoggerMessage(EventId = 11002, Level = LogLevel.Information, Message = "User {ActorId} printed report {ReportCode} ({Rows} rows), run {RunId}")]
    public partial void Printed(int actorId, long runId, string reportCode, int rows);

    [LoggerMessage(EventId = 11003, Level = LogLevel.Information, Message = "User {ActorId} queued a background export of report {ReportCode}, run {RunId}")]
    public partial void ExportQueued(int actorId, long runId, string reportCode);

    [LoggerMessage(EventId = 11004, Level = LogLevel.Information, Message = "Background export {RunId} of report {ReportCode} is ready ({Rows} rows)")]
    public partial void ExportReady(long runId, string reportCode, int rows);

    [LoggerMessage(EventId = 11005, Level = LogLevel.Error, Message = "Background export {RunId} of report {ReportCode} failed")]
    public partial void ExportFailed(long runId, string reportCode, Exception exception);

    [LoggerMessage(EventId = 11006, Level = LogLevel.Information, Message = "User {ActorId} downloaded export {RunId}")]
    public partial void Downloaded(int actorId, long runId);
}
