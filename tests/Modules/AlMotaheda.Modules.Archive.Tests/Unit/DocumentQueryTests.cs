using AlMotaheda.Modules.Archive.Data;
using AlMotaheda.Modules.Archive.Features.Documents;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Archive.Tests.Unit;

/// <summary>
/// Every archive query translates to SQL Server SQL, starts from the project, and leaves deleted documents out.
/// <c>ToQueryString()</c> opens no connection, so a query EF can't translate fails here without a database.
/// </summary>
public sealed class DocumentQueryTests
{
    private static ArchiveDbContext CreateContext() => new ArchiveDbContextDesignTimeFactory().CreateDbContext([]);

    [Fact]
    public void The_list_translates_with_every_filter_newest_first()
    {
        using var db = CreateContext();
        var filter = new DocumentFilter(
            DocumentCategory.PaymentCertificate,
            DocumentKind.Pdf,
            "certificate",
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            UploadedBy: 7,
            LinkedEntityType.Expense,
            EntityId: 12);

        var sql = DocumentQueries.Filtered(db, 1, filter).ToQueryString();

        Assert.Contains("FROM [docs].[Document]", sql);
        Assert.Contains("[ProjectId] = @", sql);
        Assert.Contains("[DeletedAt] IS NULL", sql);
        Assert.Contains("[Category] = @", sql);
        Assert.Contains("[Kind] = @", sql);
        Assert.Contains("LIKE", sql);
        Assert.Contains("[UploadedAt] >= @", sql);
        Assert.Contains("[UploadedAt] < @", sql);
        Assert.Contains("[UploadedBy] = @", sql);
        Assert.Contains("EXISTS", sql);
        Assert.Contains("[docs].[DocumentLink]", sql);
        Assert.Contains("ORDER BY [d].[UploadedAt] DESC, [d].[Id] DESC", sql);
    }

    [Fact]
    public void Search_text_is_a_parameter_never_part_of_the_sql()
    {
        using var db = CreateContext();
        var filter = new DocumentFilter(null, null, "x' OR 1=1--", null, null, null, null, null);

        var sql = DocumentQueries.Filtered(db, 1, filter).ToQueryString();

        Assert.Contains("[Title] LIKE @", sql);
        Assert.Contains("[Description] LIKE @", sql);
        Assert.Contains("[FileName] LIKE @", sql);
        Assert.DoesNotContain("LIKE N'%x' OR 1=1", sql);
    }

    [Fact]
    public void The_list_count_uses_the_same_filters_without_loading_links()
    {
        using var db = CreateContext();
        var filter = new DocumentFilter(DocumentCategory.SitePhoto, null, null, null, null, null, null, null);

        var sql = DocumentQueries.Matching(db, 1, filter).Select(document => document.Id).ToQueryString();

        Assert.Contains("[ProjectId] = @", sql);
        Assert.Contains("[DeletedAt] IS NULL", sql);
        Assert.Contains("[Category] = @", sql);
    }

    [Fact]
    public void One_document_is_read_only_inside_its_project_and_not_when_deleted()
    {
        using var db = CreateContext();

        var one = DocumentQueries.One(db, 1, 2).ToQueryString();
        var file = DocumentQueries.FileOf(db, 1, 2).ToQueryString();

        foreach (var sql in new[] { one, file })
        {
            Assert.Contains("[ProjectId] = @", sql);
            Assert.Contains("[Id] = @", sql);
            Assert.Contains("[DeletedAt] IS NULL", sql);
        }

        Assert.Contains("[docs].[DocumentLink]", one);
        Assert.Contains("[StorageKey]", file);
    }

    [Fact]
    public void Category_counts_are_grouped_in_sql_for_the_whole_project()
    {
        using var db = CreateContext();

        var sql = DocumentQueries.CategoryCounts(db, 1).ToQueryString();

        Assert.Contains("GROUP BY [d].[Category]", sql);
        Assert.Contains("COUNT(*)", sql);
        Assert.Contains("[DeletedAt] IS NULL", sql);
    }
}
