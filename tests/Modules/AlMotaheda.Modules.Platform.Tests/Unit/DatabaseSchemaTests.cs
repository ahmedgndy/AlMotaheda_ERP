using AlMotaheda.Modules.Platform.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlMotaheda.Modules.Platform.Tests.Unit;

/// <summary>
/// Checks the SQL the model produces, with no database: the table design the audit writer relies on, and that the
/// committed migration matches the model.
/// </summary>
public sealed class DatabaseSchemaTests
{
    private static PlatformDbContext CreateContext() => new PlatformDbContextDesignTimeFactory().CreateDbContext([]);

    [Fact]
    public void Committed_migrations_match_the_model()
    {
        using var db = CreateContext();

        Assert.False(db.Database.HasPendingModelChanges(), "The model changed without a migration. Run dotnet ef migrations add.");
        Assert.Equal(["InitialPlatform"], db.Database.GetMigrations().Select(id => id[(id.IndexOf('_') + 1)..]));
    }

    [Theory]
    [InlineData("CREATE TABLE [platform].[AuditEntry]")]
    [InlineData("CREATE TABLE [platform].[OutboxMessage]")]
    [InlineData("CREATE TABLE [platform].[Notification]")]
    [InlineData("CONSTRAINT [CK_AuditEntry_Action] CHECK ([Action] IN ('Added', 'Modified', 'Deleted'))")]
    [InlineData("CREATE UNIQUE INDEX [UX_OutboxMessage_MessageId] ON [platform].[OutboxMessage] ([MessageId])")]
    [InlineData("CREATE INDEX [IX_OutboxMessage_Pending] ON [platform].[OutboxMessage] ([Id]) INCLUDE ([Attempts]) WHERE [ProcessedAt] IS NULL")]
    [InlineData("CREATE UNIQUE INDEX [UX_Notification_Source] ON [platform].[Notification] ([SourceMessageId], [RecipientUserId], [EntityType], [EntityKey], [Type])")]
    public void The_create_script_contains(string expected)
    {
        using var db = CreateContext();

        Assert.Contains(expected, db.Database.GenerateCreateScript());
    }

    [Fact]
    public void The_audit_writer_columns_match_the_table()
    {
        // AuditTrailInterceptor inserts with OPENJSON column types; they must not be wider than the table's.
        using var db = CreateContext();
        var script = db.Database.GenerateCreateScript();

        Assert.Contains("[Module] varchar(30) NOT NULL", script);
        Assert.Contains("[EntityType] varchar(100) NOT NULL", script);
        Assert.Contains("[EntityKey] varchar(100) NOT NULL", script);
        Assert.Contains("[Action] varchar(10) NOT NULL", script);
        Assert.Contains("[Changes] nvarchar(max) NOT NULL", script);
        Assert.Contains("[TraceId] varchar(32) NULL", script);
    }
}
