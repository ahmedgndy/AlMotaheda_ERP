using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Archive.Data.Configurations;

internal sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    /// <summary>Each stored file belongs to exactly one row.</summary>
    public const string StorageKeyIndex = "UX_Document_StorageKey";

    /// <summary>The foreign key to <c>projects.Project</c>. Added by hand in the migration: Projects owns that table.</summary>
    public const string ProjectForeignKey = "FK_Document_Project_ProjectId";

    /// <summary>The foreign key from the uploader to <c>access.User</c>. Added by hand in the migration.</summary>
    public const string UploaderForeignKey = "FK_Document_User_UploadedBy";

    /// <summary>The foreign key from the person who deleted it to <c>access.User</c>. Added by hand in the migration.</summary>
    public const string DeleterForeignKey = "FK_Document_User_DeletedBy";

    public const int TitleMaxLength = 200;
    public const int DescriptionMaxLength = 1000;
    public const int FileNameMaxLength = 255;
    public const int ContentTypeMaxLength = 100;
    public const int StorageKeyMaxLength = 200;

    public void Configure(EntityTypeBuilder<Document> builder)
    {
        var categories = SqlList.Of(Enum.GetNames<DocumentCategory>());
        var kinds = SqlList.Of(Enum.GetNames<DocumentKind>());

        builder.ToTable(nameof(Document), table =>
        {
            table.HasCheckConstraint("CK_Document_Category", $"[Category] IN ({categories})");
            table.HasCheckConstraint("CK_Document_Kind", $"[Kind] IN ({kinds})");
            table.HasCheckConstraint("CK_Document_Size", "[SizeBytes] > 0");

            // Binary collation so the range means lower-case hex only, whatever the database collation is.
            table.HasCheckConstraint(
                "CK_Document_Sha256",
                "LEN([Sha256]) = 64 AND [Sha256] COLLATE Latin1_General_BIN NOT LIKE '%[^0-9a-f]%'");

            // Both or neither: a CHECK passes when its result is unknown, so each side tests IS NULL itself.
            table.HasCheckConstraint(
                "CK_Document_Deleted",
                "([DeletedAt] IS NULL AND [DeletedBy] IS NULL) OR ([DeletedAt] IS NOT NULL AND [DeletedBy] IS NOT NULL)");
        });

        builder.Property(document => document.Category).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(document => document.Kind).HasConversion<string>().HasMaxLength(30).IsUnicode(false);
        builder.Property(document => document.Title).HasMaxLength(TitleMaxLength).IsUnicode();
        builder.Property(document => document.Description).HasMaxLength(DescriptionMaxLength).IsUnicode();
        builder.Property(document => document.FileName).HasMaxLength(FileNameMaxLength).IsUnicode();
        builder.Property(document => document.ContentType).HasMaxLength(ContentTypeMaxLength);
        builder.Property(document => document.Sha256).HasMaxLength(64).IsFixedLength();
        builder.Property(document => document.StorageKey).HasMaxLength(StorageKeyMaxLength);
        builder.Property(document => document.RowVersion).IsRowVersion();

        builder.HasIndex(document => document.StorageKey).IsUnique().HasDatabaseName(StorageKeyIndex);

        // The archive screen: one project, newest first, with or without a category. Deleted rows are never listed.
        builder.HasIndex(document => new { document.ProjectId, document.Category, document.UploadedAt }, "IX_Document_ProjectId_Category_UploadedAt")
            .HasFilter("[DeletedAt] IS NULL");
        builder.HasIndex(document => new { document.ProjectId, document.UploadedAt }, "IX_Document_ProjectId_UploadedAt")
            .HasFilter("[DeletedAt] IS NULL");

        builder.HasMany(document => document.Links)
            .WithOne(link => link.Document)
            .HasForeignKey(link => link.DocumentId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class DocumentLinkConfiguration : IEntityTypeConfiguration<DocumentLink>
{
    /// <summary>A document points to one record once.</summary>
    public const string UniqueLinkIndex = "UX_DocumentLink_Document_Entity";

    public void Configure(EntityTypeBuilder<DocumentLink> builder)
    {
        var entityTypes = SqlList.Of(Enum.GetNames<LinkedEntityType>());

        builder.ToTable(nameof(DocumentLink), table =>
        {
            table.HasCheckConstraint("CK_DocumentLink_EntityType", $"[EntityType] IN ({entityTypes})");
            table.HasCheckConstraint("CK_DocumentLink_EntityId", "[EntityId] > 0");
        });

        builder.Property(link => link.EntityType).HasConversion<string>().HasMaxLength(40).IsUnicode(false);

        builder.HasIndex(link => new { link.DocumentId, link.EntityType, link.EntityId }, UniqueLinkIndex).IsUnique();

        // "Which documents belong to this expense?" from the other modules' screens.
        builder.HasIndex(link => new { link.EntityType, link.EntityId }, "IX_DocumentLink_Entity");
    }
}
