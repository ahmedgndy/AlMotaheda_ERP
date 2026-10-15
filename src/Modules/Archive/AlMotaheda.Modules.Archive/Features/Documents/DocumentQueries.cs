using AlMotaheda.Modules.Archive.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Archive.Features.Documents;

/// <summary>A link of a document, as the queries read it.</summary>
internal sealed record DocumentLinkRef(LinkedEntityType EntityType, int EntityId);

/// <summary>One document as the queries project it: every column a view needs, and its links.</summary>
internal sealed class DocumentRow
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public DocumentCategory Category { get; init; }
    public string Title { get; init; } = "";
    public string? Description { get; init; }
    public DateOnly? DocumentDate { get; init; }
    public string FileName { get; init; } = "";
    public string ContentType { get; init; } = "";
    public DocumentKind Kind { get; init; }
    public long SizeBytes { get; init; }
    public string Sha256 { get; init; } = "";
    public DateTime UploadedAt { get; init; }
    public int UploadedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public List<DocumentLinkRef> Links { get; init; } = [];
}

/// <summary>What the content endpoint needs to send the bytes.</summary>
internal sealed record DocumentFileRow(
    int Id, string StorageKey, string FileName, string ContentType, DocumentKind Kind, long SizeBytes, string Sha256, DateTime UploadedAt);

/// <summary>How many documents of a project are in one category.</summary>
internal sealed record DocumentCategoryCount(DocumentCategory Category, int Count);

/// <summary>The filters of <c>GET /api/projects/{pid}/documents</c>, after validation.</summary>
/// <param name="UploadedFromUtc">Start of the company's day of <c>from</c>, as UTC.</param>
/// <param name="UploadedBeforeUtc">Start of the company's day after <c>to</c>, as UTC (exclusive).</param>
internal sealed record DocumentFilter(
    DocumentCategory? Category,
    DocumentKind? Kind,
    string? Search,
    DateTime? UploadedFromUtc,
    DateTime? UploadedBeforeUtc,
    int? UploadedBy,
    LinkedEntityType? EntityType,
    int? EntityId);

/// <summary>
/// The archive queries, as <see cref="IQueryable{T}"/> so tests can prove they translate to SQL. Every one of them
/// starts from the project and leaves deleted documents out.
/// </summary>
internal static class DocumentQueries
{
    /// <summary>The documents of a project that are not deleted. The start of every read.</summary>
    public static IQueryable<Document> Live(ArchiveDbContext db, int projectId) =>
        db.Documents.AsNoTracking().Where(document => document.ProjectId == projectId && document.DeletedAt == null);

    /// <summary>The list screen, newest first. Every filter, the order and the paging run in SQL.</summary>
    public static IQueryable<DocumentRow> Filtered(ArchiveDbContext db, int projectId, DocumentFilter filter) =>
        Project(Matching(db, projectId, filter).OrderByDescending(document => document.UploadedAt).ThenByDescending(document => document.Id));

    /// <summary>The documents that pass the filters, unordered: what the list counts.</summary>
    public static IQueryable<Document> Matching(ArchiveDbContext db, int projectId, DocumentFilter filter)
    {
        var query = Live(db, projectId);

        if (filter.Category is { } category)
        {
            query = query.Where(document => document.Category == category);
        }

        if (filter.Kind is { } kind)
        {
            query = query.Where(document => document.Kind == kind);
        }

        if (filter.Search is { } text)
        {
            query = query.Where(document =>
                document.Title.Contains(text)
                || (document.Description != null && document.Description.Contains(text))
                || document.FileName.Contains(text));
        }

        if (filter.UploadedFromUtc is { } from)
        {
            query = query.Where(document => document.UploadedAt >= from);
        }

        if (filter.UploadedBeforeUtc is { } before)
        {
            query = query.Where(document => document.UploadedAt < before);
        }

        if (filter.UploadedBy is { } uploader)
        {
            query = query.Where(document => document.UploadedBy == uploader);
        }

        if (filter.EntityType is { } entityType && filter.EntityId is { } entityId)
        {
            query = query.Where(document => document.Links.Any(link => link.EntityType == entityType && link.EntityId == entityId));
        }

        return query;
    }

    /// <summary>One document of the project, or none when it belongs to another project or was deleted.</summary>
    public static IQueryable<DocumentRow> One(ArchiveDbContext db, int projectId, int documentId) =>
        Project(Live(db, projectId).Where(document => document.Id == documentId));

    /// <summary>The stored file of one document of the project.</summary>
    public static IQueryable<DocumentFileRow> FileOf(ArchiveDbContext db, int projectId, int documentId) =>
        Live(db, projectId)
            .Where(document => document.Id == documentId)
            .Select(document => new DocumentFileRow(
                document.Id,
                document.StorageKey,
                document.FileName,
                document.ContentType,
                document.Kind,
                document.SizeBytes,
                document.Sha256,
                document.UploadedAt));

    /// <summary>The count of each category that has documents, for the whole project whatever the filters.</summary>
    public static IQueryable<DocumentCategoryCount> CategoryCounts(ArchiveDbContext db, int projectId) =>
        Live(db, projectId)
            .GroupBy(document => document.Category)
            .Select(group => new DocumentCategoryCount(group.Key, group.Count()));

    private static IQueryable<DocumentRow> Project(IQueryable<Document> query) =>
        query.Select(document => new DocumentRow
        {
            Id = document.Id,
            ProjectId = document.ProjectId,
            Category = document.Category,
            Title = document.Title,
            Description = document.Description,
            DocumentDate = document.DocumentDate,
            FileName = document.FileName,
            ContentType = document.ContentType,
            Kind = document.Kind,
            SizeBytes = document.SizeBytes,
            Sha256 = document.Sha256,
            UploadedAt = document.UploadedAt,
            UploadedBy = document.UploadedBy,
            RowVersion = document.RowVersion,
            Links = document.Links
                .OrderBy(link => link.Id)
                .Select(link => new DocumentLinkRef(link.EntityType, link.EntityId))
                .ToList(),
        });
}
