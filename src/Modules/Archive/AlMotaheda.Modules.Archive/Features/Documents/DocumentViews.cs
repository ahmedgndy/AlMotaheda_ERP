using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Archive.Common;
using AlMotaheda.Modules.Archive.Data;
using AlMotaheda.Modules.Archive.Security;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Archive.Features.Documents;

/// <summary>A person shown next to a record, in both languages, with whether their account is still active.</summary>
internal sealed record UploaderRef(int UserId, string NameAr, string NameEn, bool IsActive)
{
    public static UploaderRef From(int userId, IReadOnlyDictionary<int, UserSummary> users) =>
        users.TryGetValue(userId, out var user)
            ? new UploaderRef(user.Id, user.NameAr, user.NameEn, user.IsActive)
            // Not expected: the uploader points to access.User with a foreign key and users are never deleted.
            : new UploaderRef(userId, $"#{userId}", $"#{userId}", false);
}

/// <summary>A link from a document to another module's record.</summary>
internal sealed record DocumentLinkView(string EntityType, int EntityId);

/// <summary>What the caller may do with this document, so the frontend shows only the buttons that will work.</summary>
/// <param name="Preview">PDF, images and video open in the browser; other kinds only download.</param>
internal sealed record DocumentAbilities(bool Edit, bool Delete, bool Preview);

/// <summary>How many documents of the project are in one category (the filter chips).</summary>
internal sealed record DocumentCategoryCountView(string Category, int Count);

/// <summary>
/// One document, as the list, the detail, upload and edit return it. The bytes are fetched separately from
/// <c>GET …/documents/{id}/content</c>.
/// </summary>
internal sealed record DocumentView(
    int Id,
    int ProjectId,
    string Category,
    string Title,
    string? Description,
    DateOnly? DocumentDate,
    string FileName,
    string ContentType,
    string Kind,
    long SizeBytes,
    string Sha256,
    DateTime UploadedAt,
    DateTime? CapturedAt,
    UploaderRef Uploader,
    IReadOnlyList<DocumentLinkView> Links,
    DocumentAbilities Abilities,
    string RowVersion)
{
    public static DocumentView From(DocumentRow row, IReadOnlyDictionary<int, UserSummary> people, ArchiveCaller caller, bool writesBlocked)
    {
        var canChange = !writesBlocked && caller.CanChange(row.UploadedBy);

        return new DocumentView(
            row.Id,
            row.ProjectId,
            row.Category.ToString(),
            row.Title,
            row.Description,
            row.DocumentDate,
            row.FileName,
            row.ContentType,
            row.Kind.ToString(),
            row.SizeBytes,
            row.Sha256,
            row.UploadedAt,
            row.CapturedAt,
            UploaderRef.From(row.UploadedBy, people),
            row.Links.Select(link => new DocumentLinkView(link.EntityType.ToString(), link.EntityId)).ToList(),
            new DocumentAbilities(canChange, canChange, DocumentPreview.IsAllowed(row.Kind)),
            RowVersions.Format(row.RowVersion));
    }

    /// <summary>Builds the views of a page with one name lookup for every uploader on it.</summary>
    public static async Task<IReadOnlyList<DocumentView>> FromAsync(
        IReadOnlyList<DocumentRow> rows, IUserDirectory users, ArchiveCaller caller, bool writesBlocked, CancellationToken cancellationToken)
    {
        var userIds = rows.Select(row => row.UploadedBy).Distinct().ToList();
        var people = userIds.Count == 0
            ? new Dictionary<int, UserSummary>()
            : await users.FindAsync(userIds, cancellationToken);

        return rows.Select(row => From(row, people, caller, writesBlocked)).ToList();
    }

    /// <summary>Reads one document of a project back after a write.</summary>
    public static async Task<DocumentView?> LoadAsync(
        ArchiveDbContext db, IUserDirectory users, ArchiveCaller caller, bool writesBlocked, int projectId, int documentId, CancellationToken cancellationToken)
    {
        var row = await DocumentQueries.One(db, projectId, documentId).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : (await FromAsync([row], users, caller, writesBlocked, cancellationToken))[0];
    }
}

/// <summary>Which kinds the browser may show in place. Everything else always downloads.</summary>
internal static class DocumentPreview
{
    public static bool IsAllowed(DocumentKind kind) => kind is DocumentKind.Pdf or DocumentKind.Image or DocumentKind.Video;
}
