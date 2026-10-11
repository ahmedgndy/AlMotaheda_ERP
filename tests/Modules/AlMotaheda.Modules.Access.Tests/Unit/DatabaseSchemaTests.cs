using AlMotaheda.Modules.Access.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AlMotaheda.Modules.Access.Tests.Unit;

/// <summary>
/// Checks the SQL the model produces, with no database: the approved table design, and that the committed
/// migration matches the model (a forgotten migration fails here).
/// </summary>
public sealed class DatabaseSchemaTests
{
    private static AccessDbContext CreateContext() =>
        new AccessDbContextDesignTimeFactory().CreateDbContext([]);

    [Fact]
    public void Committed_migrations_match_the_model()
    {
        using var db = CreateContext();

        Assert.False(db.Database.HasPendingModelChanges(), "The model changed without a migration. Run dotnet ef migrations add.");
        Assert.Equal(["InitialAccess"], db.Database.GetMigrations().Select(id => id[(id.IndexOf('_') + 1)..]));
    }

    [Fact]
    public void Tables_live_in_the_access_schema()
    {
        using var db = CreateContext();
        var script = db.Database.GenerateCreateScript();

        foreach (var table in new[] { "Role", "Permission", "RolePermission", "User", "UserSignInState", "UserPermissionOverride", "RefreshToken" })
        {
            Assert.Contains($"CREATE TABLE [access].[{table}]", script);
        }
    }

    [Theory]
    [InlineData("CREATE UNIQUE INDEX [UX_User_NormalizedEmail] ON [access].[User] ([NormalizedEmail])")]
    [InlineData("CREATE UNIQUE INDEX [UX_User_Phone] ON [access].[User] ([Phone]) WHERE [Phone] IS NOT NULL")]
    [InlineData("CREATE UNIQUE INDEX [UX_Role_Code] ON [access].[Role] ([Code])")]
    [InlineData("CREATE UNIQUE INDEX [UX_Permission_Module_Action] ON [access].[Permission] ([Module], [Action])")]
    [InlineData("CREATE UNIQUE INDEX [UX_RefreshToken_TokenHash] ON [access].[RefreshToken] ([TokenHash])")]
    [InlineData("CREATE INDEX [IX_RefreshToken_UserId_Active] ON [access].[RefreshToken] ([UserId]) WHERE [RevokedAt] IS NULL")]
    [InlineData("CREATE INDEX [IX_RefreshToken_FamilyId] ON [access].[RefreshToken] ([FamilyId])")]
    [InlineData("CREATE INDEX [IX_RefreshToken_ExpiresAt] ON [access].[RefreshToken] ([ExpiresAt])")]
    [InlineData("CONSTRAINT [CK_User_Status] CHECK ([Status] IN ('Active', 'Disabled'))")]
    [InlineData("CONSTRAINT [CK_UserSignInState_FailedLoginCount] CHECK ([FailedLoginCount] >= 0)")]
    [InlineData("CONSTRAINT [FK_UserSignInState_User_UserId] FOREIGN KEY ([UserId]) REFERENCES [access].[User] ([Id])")]
    [InlineData("CONSTRAINT [CK_Permission_Action] CHECK ([Action] IN ('View', 'Create', 'Edit', 'Approve', 'SeeMoney'))")]
    [InlineData("[TokenHash] binary(32) NOT NULL")]
    [InlineData("[RowVersion] rowversion NOT NULL")]
    [InlineData("[NameAr] nvarchar(150) NOT NULL")]
    [InlineData("[LockoutEndsAt] datetime2(3) NULL")]
    [InlineData("CONSTRAINT [PK_RolePermission] PRIMARY KEY ([RoleId], [PermissionId])")]
    [InlineData("CONSTRAINT [PK_UserPermissionOverride] PRIMARY KEY ([UserId], [PermissionId])")]
    public void Script_contains_the_approved_constraints_and_indexes(string expected)
    {
        using var db = CreateContext();

        Assert.Contains(expected, db.Database.GenerateCreateScript());
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

    [Fact]
    public void Seed_data_is_part_of_the_model()
    {
        using var db = CreateContext();
        var model = db.GetService<IDesignTimeModel>().Model;

        Assert.Equal(6, model.FindEntityType(typeof(Role))!.GetSeedData().Count());
        Assert.Equal(85, model.FindEntityType(typeof(Permission))!.GetSeedData().Count());
        Assert.Equal(202, model.FindEntityType(typeof(RolePermission))!.GetSeedData().Count());
    }
}
