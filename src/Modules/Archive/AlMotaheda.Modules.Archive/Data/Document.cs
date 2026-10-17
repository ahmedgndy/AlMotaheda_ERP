namespace AlMotaheda.Modules.Archive.Data;

/// <summary>
/// One file in a project's archive: its metadata and the key of its bytes in the file store. The bytes never live
/// in the database.
/// </summary>
/// <remarks>
/// <para><see cref="ProjectId"/>, <see cref="UploadedBy"/> and <see cref="DeletedBy"/> point to other modules'
/// tables (<c>projects.Project</c>, <c>access.User</c>). They are plain columns here; the real foreign keys are
/// written by hand in the migration.</para>
/// <para>Delete is soft: <see cref="DeletedAt"/> hides the row and the file stays in the store.</para>
/// </remarks>
internal sealed class Document
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public DocumentCategory Category { get; set; }

    public string Title { get; set; } = "";

    public string? Description { get; set; }

    /// <summary>The date written on the document (a certificate's date, a meeting's date), when it has one.</summary>
    public DateOnly? DocumentDate { get; set; }

    /// <summary>The original file name, cleaned. Only for display and the download header; never a path.</summary>
    public string FileName { get; set; } = "";

    /// <summary>Decided by the server from the file's first bytes, never taken from the client.</summary>
    public string ContentType { get; set; } = "";

    public DocumentKind Kind { get; set; }

    public long SizeBytes { get; set; }

    /// <summary>Lower-case hex SHA-256 of the bytes, computed while they were stored.</summary>
    public string Sha256 { get; set; } = "";

    /// <summary>Where the bytes are in the file store: <c>{projectId}/{yyyy}/{MM}/{guid}</c>.</summary>
    public string StorageKey { get; set; } = "";

    public DateTime UploadedAt { get; set; }

    public int UploadedBy { get; set; }

    /// <summary>When the photo or file was taken on the phone (UTC), as the app reports it. Empty when not sent.</summary>
    public DateTime? CapturedAt { get; set; }

    /// <summary>
    /// Optional key the app makes for one upload (<c>Idempotency-Key</c> header), so a file sent again from the offline
    /// queue after a lost answer is stored once. Unique per uploader when set.
    /// </summary>
    public Guid? IdempotencyKey { get; set; }

    public DateTime? DeletedAt { get; set; }

    public int? DeletedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];

    public List<DocumentLink> Links { get; set; } = [];
}

/// <summary>What a document is about. Stored by name.</summary>
internal enum DocumentCategory
{
    PaymentCertificate,
    MeetingMinutes,
    SitePhoto,
    SiteVideo,
    ProgressReport,
    Contract,
    Drawing,
    Invoice,
    Receipt,
    Official,
    Other,
}

/// <summary>What sort of file it is, decided from its bytes. Only PDF, images and video may be previewed.</summary>
internal enum DocumentKind
{
    Pdf,
    Image,
    Video,
    Office,
    Drawing,
}

/// <summary>
/// An optional pointer from a document to a record of another module (an expense, a daily report, ...).
/// There is no foreign key: the record lives in another module's schema. A document never copies the record.
/// </summary>
internal sealed class DocumentLink
{
    public int Id { get; set; }

    public int DocumentId { get; set; }

    public Document Document { get; set; } = null!;

    public LinkedEntityType EntityType { get; set; }

    public int EntityId { get; set; }
}

/// <summary>The kinds of records a document can point to (the ERD's generic attachment link). Stored by name.</summary>
internal enum LinkedEntityType
{
    Expense,
    SupplierPayment,
    DailyReport,
    ReportRequest,
    LaborSheet,
    SubContract,
    SubPayment,
    Equipment,
    EquipmentCost,
    Budget,
}
