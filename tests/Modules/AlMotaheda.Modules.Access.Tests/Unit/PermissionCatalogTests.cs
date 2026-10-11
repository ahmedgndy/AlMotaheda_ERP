using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Data;

namespace AlMotaheda.Modules.Access.Tests.Unit;

/// <summary>The seed data must match the prototype's role definitions exactly (design, "Role defaults").</summary>
public sealed class PermissionCatalogTests
{
    [Fact]
    public void Catalog_has_17_modules_and_85_permissions_with_unique_ids()
    {
        var permissions = PermissionCatalog.SeedPermissions().ToList();

        Assert.Equal(17, PermissionCatalog.Modules.Count);
        Assert.Equal(85, permissions.Count);
        Assert.Equal(85, permissions.Select(permission => permission.Id).Distinct().Count());
        Assert.Equal(85, permissions.Select(permission => (permission.Module, permission.Action)).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 85), permissions.Select(permission => permission.Id).Order());
    }

    [Fact]
    public void Six_roles_are_seeded_with_202_role_permissions()
    {
        var roles = PermissionCatalog.SeedRoles().ToList();
        var links = PermissionCatalog.SeedRolePermissions().ToList();

        Assert.Equal(["executive", "manager", "engineer", "office_admin", "accountant", "staff"], roles.Select(role => role.Code));
        Assert.Equal(202, links.Count);
        Assert.Equal(202, links.Select(link => (link.RoleId, link.PermissionId)).Distinct().Count());

        var countsByRole = links.GroupBy(link => link.RoleId).ToDictionary(group => group.Key, group => group.Count());
        Assert.Equal(82, countsByRole[PermissionCatalog.RoleId("executive")]);
        Assert.Equal(37, countsByRole[PermissionCatalog.RoleId("manager")]);
        Assert.Equal(16, countsByRole[PermissionCatalog.RoleId("engineer")]);
        Assert.Equal(8, countsByRole[PermissionCatalog.RoleId("office_admin")]);
        Assert.Equal(50, countsByRole[PermissionCatalog.RoleId("accountant")]);
        Assert.Equal(9, countsByRole[PermissionCatalog.RoleId("staff")]);
    }

    [Theory]
    [InlineData("executive", "monitoring", "VE")]
    [InlineData("executive", "accounts", "VCEAF")]
    [InlineData("manager", "equipment", "VEA")]
    [InlineData("manager", "accounts", "")]
    [InlineData("accountant", "accounts", "V")]
    [InlineData("accountant", "finance", "VCEAF")]
    [InlineData("office_admin", "office", "VCE")]
    [InlineData("staff", "fuel", "VC")]
    [InlineData("engineer", "payments", "")]
    public void Role_defaults_match_the_prototype(string role, string module, string expectedLetters)
    {
        var actions = PermissionCatalog.SeedRolePermissions()
            .Where(link => link.RoleId == PermissionCatalog.RoleId(role))
            .Select(link => PermissionCatalog.SeedPermissions().Single(permission => permission.Id == link.PermissionId))
            .Where(permission => permission.Module == module)
            .Select(permission => permission.Action);

        Assert.Equal(expectedLetters, string.Concat(actions.Order().Select(PermissionActions.ToLetter)));
    }

    [Fact]
    public void Only_the_executive_can_create_or_edit_accounts_by_default()
    {
        var accountWriters = PermissionCatalog.RoleDefaults
            .Where(role => role.Value.TryGetValue("accounts", out var letters) && (letters.Contains('C') || letters.Contains('E')))
            .Select(role => role.Key);

        Assert.Equal(["executive"], accountWriters);
    }
}
