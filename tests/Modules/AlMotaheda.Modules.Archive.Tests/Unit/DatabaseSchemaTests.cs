using AlMotaheda.Modules.Archive.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlMotaheda.Modules.Archive.Tests.Unit;

/// <summary>
/// Checks the SQL the model produces, with no database: the approved table design, and that the committed
/// migration matches the model (a forgotten migration fails here).
/// </summary>
public sealed class DatabaseSchemaTests
{
    private static ArchiveDbContext CreateContext() => new ArchiveDbContextDesignTimeFactory().CreateDbContext([]);

    [Fact]
    public void Committed_migrations_match_the_model()
    {
        using var db = CreateContext();

        Assert.False(db.Database.HasPendingModelChanges(), "The model changed without a migration. Run dotnet ef migrations add.");
        Assert.Equal(["InitialArchive", "DocumentIdempotency", "CustodyAndSettlementLinks"], db.Database.GetMigrations().Select(id => id[(id.IndexOf('_') + 1)..]));
    }

    [Fact]
    public void Tables_live_in_the_docs_schema()
    {
        using var db = CreateContext();
        var script = db.Database.GenerateCreateScript();

        Assert.Contains("CREATE TABLE [docs].[Document]", script);
        Assert.Contains("CREATE TABLE [docs].[DocumentLink]", script);
    }

    [Theory]
    [InlineData("CREATE UNIQUE INDEX [UX_Document_StorageKey] ON [docs].[Document] ([StorageKey])")]
    [InlineData("CREATE INDEX [IX_Document_ProjectId_Category_UploadedAt] ON [docs].[Document] ([ProjectId], [Category], [UploadedAt]) WHERE [DeletedAt] IS NULL")]
    [InlineData("CREATE INDEX [IX_Document_ProjectId_UploadedAt] ON [docs].[Document] ([ProjectId], [UploadedAt]) WHERE [DeletedAt] IS NULL")]
    [InlineData("CREATE UNIQUE INDEX [UX_DocumentLink_Document_Entity] ON [docs].[DocumentLink] ([DocumentId], [EntityType], [EntityId])")]
    [InlineData("CREATE INDEX [IX_DocumentLink_Entity] ON [docs].[DocumentLink] ([EntityType], [EntityId])")]
    [InlineData("CONSTRAINT [CK_Document_Category] CHECK ([Category] IN ('PaymentCertificate', 'MeetingMinutes', 'SitePhoto', 'SiteVideo', 'ProgressReport', 'Contract', 'Drawing', 'Invoice', 'Receipt', 'Official', 'Other'))")]
    [InlineData("CONSTRAINT [CK_Document_Kind] CHECK ([Kind] IN ('Pdf', 'Image', 'Video', 'Office', 'Drawing'))")]
    [InlineData("CONSTRAINT [CK_Document_Size] CHECK ([SizeBytes] > 0)")]
    [InlineData("CONSTRAINT [CK_Document_Sha256] CHECK (LEN([Sha256]) = 64 AND [Sha256] COLLATE Latin1_General_BIN NOT LIKE '%[^0-9a-f]%')")]
    [InlineData("CONSTRAINT [CK_DocumentLink_EntityType] CHECK ([EntityType] IN ('Expense', 'SupplierPayment', 'DailyReport', 'ReportRequest', 'LaborSheet', 'SubContract', 'SubPayment', 'Equipment', 'EquipmentCost', 'Budget', 'CustodyEntry', 'SupplyAgreement', 'PayrollSettlement', 'ContractVariation'))")]
    [InlineData("CONSTRAINT [CK_DocumentLink_EntityId] CHECK ([EntityId] > 0)")]
    [InlineData("CONSTRAINT [FK_DocumentLink_Document_DocumentId] FOREIGN KEY ([DocumentId]) REFERENCES [docs].[Document] ([Id])")]
    [InlineData("[Title] nvarchar(200) NOT NULL")]
    [InlineData("[Description] nvarchar(1000) NULL")]
    [InlineData("[DocumentDate] date NULL")]
    [InlineData("[FileName] nvarchar(255) NOT NULL")]
    [InlineData("[ContentType] varchar(100) NOT NULL")]
    [InlineData("[Category] varchar(30) NOT NULL")]
    [InlineData("[Kind] varchar(30) NOT NULL")]
    [InlineData("[SizeBytes] bigint NOT NULL")]
    [InlineData("[Sha256] char(64) NOT NULL")]
    [InlineData("[StorageKey] varchar(200) NOT NULL")]
    [InlineData("[UploadedAt] datetime2(3) NOT NULL")]
    [InlineData("[DeletedAt] datetime2(3) NULL")]
    [InlineData("[EntityType] varchar(40) NOT NULL")]
    [InlineData("[RowVersion] rowversion NOT NULL")]
    public void Script_contains_the_approved_constraints_and_indexes(string expected)
    {
        using var db = CreateContext();

        Assert.Contains(expected, db.Database.GenerateCreateScript());
    }

    [Fact]
    public void Deleted_at_and_deleted_by_are_set_together()
    {
        using var db = CreateContext();

        // A CHECK passes when its result is unknown (NULL), so each branch tests IS NULL / IS NOT NULL itself.
        Assert.Contains(
            "CONSTRAINT [CK_Document_Deleted] CHECK (([DeletedAt] IS NULL AND [DeletedBy] IS NULL) OR ([DeletedAt] IS NOT NULL AND [DeletedBy] IS NOT NULL))",
            db.Database.GenerateCreateScript());
    }

    [Theory]
    [InlineData("ALTER TABLE [docs].[Document] ADD CONSTRAINT [FK_Document_Project_ProjectId] FOREIGN KEY ([ProjectId]) REFERENCES [projects].[Project] ([Id])")]
    [InlineData("ALTER TABLE [docs].[Document] ADD CONSTRAINT [FK_Document_User_UploadedBy] FOREIGN KEY ([UploadedBy]) REFERENCES [access].[User] ([Id])")]
    [InlineData("ALTER TABLE [docs].[Document] ADD CONSTRAINT [FK_Document_User_DeletedBy] FOREIGN KEY ([DeletedBy]) REFERENCES [access].[User] ([Id])")]
    [InlineData("[docs].[__EFMigrationsHistory]")]
    public void Migration_adds_the_foreign_keys_to_other_schemas(string expected)
    {
        using var db = CreateContext();

        Assert.Contains(expected, db.GetService<IMigrator>().GenerateScript());
    }

    [Fact]
    public void Foreign_keys_never_cascade()
    {
        using var db = CreateContext();

        var cascading = db.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetForeignKeys())
            .Where(foreignKey => foreignKey.DeleteBehavior != DeleteBehavior.NoAction)
            .Select(foreignKey => foreignKey.ToString());

        Assert.Empty(cascading);

        var script = db.GetService<IMigrator>().GenerateScript();
        Assert.DoesNotContain("ON DELETE CASCADE", script);
    }
}
