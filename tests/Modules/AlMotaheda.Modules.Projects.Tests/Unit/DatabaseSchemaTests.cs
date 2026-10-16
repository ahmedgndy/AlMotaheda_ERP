using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlMotaheda.Modules.Projects.Tests.Unit;

/// <summary>
/// Checks the SQL the model produces, with no database: the approved table design, and that the committed
/// migration matches the model (a forgotten migration fails here).
/// </summary>
public sealed class DatabaseSchemaTests
{
    private static ProjectsDbContext CreateContext() => new ProjectsDbContextDesignTimeFactory().CreateDbContext([]);

    [Fact]
    public void Committed_migrations_match_the_model()
    {
        using var db = CreateContext();

        Assert.False(db.Database.HasPendingModelChanges(), "The model changed without a migration. Run dotnet ef migrations add.");
        Assert.Equal(["InitialProjects", "ProjectWorkspace", "ExpenseAdjustments", "SupplierUniqueness"], db.Database.GetMigrations().Select(id => id[(id.IndexOf('_') + 1)..]));
    }

    [Fact]
    public void Tables_live_in_the_projects_schema()
    {
        using var db = CreateContext();
        var script = db.Database.GenerateCreateScript();

        Assert.Contains("CREATE TABLE [projects].[Project]", script);
        Assert.Contains("CREATE TABLE [projects].[ProjectMember]", script);
    }

    [Theory]
    [InlineData("CREATE UNIQUE INDEX [UX_Project_Code] ON [projects].[Project] ([Code])")]
    [InlineData("CREATE INDEX [IX_Project_Status] ON [projects].[Project] ([Status])")]
    [InlineData("CREATE UNIQUE INDEX [UX_ProjectMember_ActiveUser] ON [projects].[ProjectMember] ([ProjectId], [UserId]) WHERE [EndDate] IS NULL")]
    [InlineData("CREATE UNIQUE INDEX [UX_ProjectMember_ActiveManager] ON [projects].[ProjectMember] ([ProjectId]) WHERE [Responsibility] = 'ProjectManager' AND [EndDate] IS NULL")]
    [InlineData("CREATE INDEX [IX_ProjectMember_UserId_Active] ON [projects].[ProjectMember] ([UserId]) INCLUDE ([ProjectId], [Responsibility]) WHERE [EndDate] IS NULL")]
    [InlineData("CREATE INDEX [IX_ProjectMember_ProjectId] ON [projects].[ProjectMember] ([ProjectId])")]
    [InlineData("CONSTRAINT [CK_Project_Status] CHECK ([Status] IN ('Planning', 'Active', 'Delayed', 'OnHold', 'Completed'))")]
    [InlineData("CONSTRAINT [CK_Project_Dates] CHECK ([EndDate] > [StartDate])")]
    [InlineData("CONSTRAINT [CK_Project_SiteRadius] CHECK ([SiteRadiusMeters] BETWEEN 50 AND 5000)")]
    [InlineData("CONSTRAINT [CK_ProjectMember_Responsibility] CHECK ([Responsibility] IN ('ProjectManager', 'SiteEngineer', 'TeamMember'))")]
    [InlineData("CONSTRAINT [CK_ProjectMember_Dates] CHECK ([EndDate] IS NULL OR [EndDate] >= [StartDate])")]
    [InlineData("CONSTRAINT [FK_ProjectMember_Project_ProjectId] FOREIGN KEY ([ProjectId]) REFERENCES [projects].[Project] ([Id])")]
    [InlineData("[Code] varchar(30) NOT NULL")]
    [InlineData("[Status] varchar(30) NOT NULL")]
    [InlineData("[Responsibility] varchar(30) NOT NULL")]
    [InlineData("[NameAr] nvarchar(200) NOT NULL")]
    [InlineData("[SiteLatitude] decimal(9,6) NULL")]
    [InlineData("[StartDate] date NOT NULL")]
    [InlineData("[EndDate] date NULL")]
    [InlineData("[RowVersion] rowversion NOT NULL")]
    public void Script_contains_the_approved_constraints_and_indexes(string expected)
    {
        using var db = CreateContext();

        Assert.Contains(expected, db.Database.GenerateCreateScript());
    }

    [Fact]
    public void Site_check_refuses_one_coordinate_without_the_other()
    {
        using var db = CreateContext();

        // A CHECK passes when its result is unknown (NULL), so each branch must test IS NULL / IS NOT NULL itself.
        Assert.Contains(
            "CONSTRAINT [CK_Project_Site] CHECK (([SiteLatitude] IS NULL AND [SiteLongitude] IS NULL) OR ([SiteLatitude] IS NOT NULL AND [SiteLongitude] IS NOT NULL",
            db.Database.GenerateCreateScript());
    }

    [Fact]
    public void Migration_adds_the_foreign_key_to_access_user()
    {
        using var db = CreateContext();

        var script = db.GetService<IMigrator>().GenerateScript();

        Assert.Contains(
            "ALTER TABLE [projects].[ProjectMember] ADD CONSTRAINT [FK_ProjectMember_User_UserId] FOREIGN KEY ([UserId]) REFERENCES [access].[User] ([Id])",
            script);
        Assert.Contains(
            "ALTER TABLE [projects].[DailyReport] ADD CONSTRAINT [FK_DailyReport_User_AuthorUserId] FOREIGN KEY ([AuthorUserId]) REFERENCES [access].[User] ([Id])",
            script);
        Assert.Contains(
            "ALTER TABLE [projects].[ReportRequest] ADD CONSTRAINT [FK_ReportRequest_User_RequesterUserId] FOREIGN KEY ([RequesterUserId]) REFERENCES [access].[User] ([Id])",
            script);
        Assert.Contains(
            "ALTER TABLE [projects].[ReportRequest] ADD CONSTRAINT [FK_ReportRequest_User_RecipientUserId] FOREIGN KEY ([RecipientUserId]) REFERENCES [access].[User] ([Id])",
            script);
        // The workspace tables come after the first migration, so the order of the two is part of the contract.
        Assert.True(
            script.IndexOf("[projects].[ProjectMember]", StringComparison.Ordinal)
            < script.IndexOf("CREATE TABLE [projects].[ProjectItem]", StringComparison.Ordinal));
        Assert.DoesNotContain("ON DELETE CASCADE", script);
        Assert.Contains("[projects].[__EFMigrationsHistory]", script);
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
    }
}
