using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Archive.Common;

/// <summary>
/// Structured audit events of the archive (event ids 6001 to 6999). Until the platform AuditLog table exists,
/// these entries are the record of who changed what.
/// </summary>
/// <remarks>
/// Ids, kinds and categories only. Titles, descriptions and file names are never logged: they can hold names of
/// people and companies.
/// </remarks>
internal sealed partial class ArchiveLog(ILogger<ArchiveLog> logger)
{
    private readonly ILogger<ArchiveLog> _logger = logger;

    [LoggerMessage(EventId = 6001, Level = LogLevel.Information, Message = "User {ActorId} uploaded document {DocumentId} to project {ProjectId} ({Kind}, {Category}, {SizeBytes} bytes)")]
    public partial void DocumentUploaded(int actorId, int projectId, int documentId, string kind, string category, long sizeBytes);

    [LoggerMessage(EventId = 6002, Level = LogLevel.Information, Message = "User {ActorId} updated document {DocumentId} on project {ProjectId}")]
    public partial void DocumentUpdated(int actorId, int projectId, int documentId);

    [LoggerMessage(EventId = 6003, Level = LogLevel.Information, Message = "User {ActorId} replaced the links of document {DocumentId} on project {ProjectId}: {LinkCount} link(s)")]
    public partial void DocumentLinksReplaced(int actorId, int projectId, int documentId, int linkCount);

    [LoggerMessage(EventId = 6004, Level = LogLevel.Information, Message = "User {ActorId} deleted document {DocumentId} on project {ProjectId}")]
    public partial void DocumentDeleted(int actorId, int projectId, int documentId);

    [LoggerMessage(EventId = 6101, Level = LogLevel.Information, Message = "User {ActorId} upload to project {ProjectId} refused: {Code}")]
    public partial void UploadRefused(int actorId, int projectId, string code);

    [LoggerMessage(EventId = 6102, Level = LogLevel.Information, Message = "Stored file {StorageKey} of project {ProjectId} was removed because the upload did not complete")]
    public partial void StoredFileRemoved(int projectId, string storageKey);

    [LoggerMessage(EventId = 6103, Level = LogLevel.Error, Message = "The file of document {DocumentId} on project {ProjectId} is missing from the store ({StorageKey})")]
    public partial void StoredFileMissing(int projectId, int documentId, string storageKey);

    [LoggerMessage(EventId = 6104, Level = LogLevel.Error, Message = "The archive file store failed for project {ProjectId}")]
    public partial void StorageFailed(Exception exception, int projectId);

    [LoggerMessage(EventId = 6105, Level = LogLevel.Error, Message = "Stored file {StorageKey} of project {ProjectId} could not be removed after a failed upload")]
    public partial void StoredFileNotRemoved(Exception exception, int projectId, string storageKey);

    [LoggerMessage(EventId = 6301, Level = LogLevel.Warning, Message = "User {ActorId} was refused {Action}: {Reason}")]
    public partial void ActionRefused(int? actorId, string action, string reason);
}
