using AlMotaheda.BuildingBlocks.Domain;

namespace AlMotaheda.Modules.Reports.Data;

/// <summary>
/// One Excel export or print of a report: who, which report, which filters, how many rows, when. Every row is in the
/// audit trail. A background export also keeps its file here until it expires.
/// </summary>
internal sealed class ReportRun
{
    private static readonly StatusFlow<ReportRunStatus> Flow = new(
        (ReportRunStatus.Queued, ReportRunStatus.Running),
        (ReportRunStatus.Running, ReportRunStatus.Ready),
        (ReportRunStatus.Running, ReportRunStatus.Failed),
        (ReportRunStatus.Queued, ReportRunStatus.Failed));

    private ReportRunStatus _status = ReportRunStatus.Queued;

    public long Id { get; set; }

    public string ReportCode { get; set; } = "";

    public ReportRunKind Kind { get; set; }

    /// <summary>The filters as sent (JSON), so the run can be repeated and audited.</summary>
    public string Filters { get; set; } = "{}";

    /// <summary><c>ar</c> or <c>en</c>.</summary>
    public string Language { get; set; } = "ar";

    public ReportRunStatus Status { get => _status; init => _status = value; }

    public int? RowCount { get; set; }

    public string? FileName { get; set; }

    /// <summary>The background export's file, until it expires. Never audited (byte arrays are skipped).</summary>
    public byte[]? Content { get; set; }

    public string? Error { get; set; }

    /// <summary>The requester's claims (JSON) at request time: a background export runs with the same rights.</summary>
    [NotAudited]
    public string? Claims { get; set; }

    public DateTime RequestedAt { get; set; }
    public int RequestedBy { get; set; }

    public DateTime? CompletedAt { get; private set; }

    public DateTime? ExpiresAt { get; set; }

    public DateTime? LastDownloadedAt { get; set; }

    /// <summary>So two workers (or a worker and a download) never both change the same run.</summary>
    public byte[] RowVersion { get; set; } = [];

    public void Start() => _status = Flow.Move(Status, ReportRunStatus.Running);

    public void Complete(int rows, string fileName, byte[]? content, DateTime now, DateTime? expiresAt)
    {
        _status = Flow.Move(Status, ReportRunStatus.Ready);
        RowCount = rows;
        FileName = fileName;
        Content = content;
        CompletedAt = now;
        ExpiresAt = expiresAt;
        Claims = null;
    }

    public void Fail(string error, DateTime now)
    {
        _status = Flow.Move(Status, ReportRunStatus.Failed);
        Error = error;
        CompletedAt = now;
        Claims = null;
    }
}

/// <summary>Stored by name.</summary>
internal enum ReportRunKind
{
    /// <summary>An Excel file returned at once.</summary>
    Excel,

    /// <summary>An Excel file built in the background (large reports).</summary>
    BackgroundExcel,

    /// <summary>A print page.</summary>
    Print,
}

/// <summary>Stored by name.</summary>
internal enum ReportRunStatus
{
    Queued,
    Running,
    Ready,
    Failed,
}
