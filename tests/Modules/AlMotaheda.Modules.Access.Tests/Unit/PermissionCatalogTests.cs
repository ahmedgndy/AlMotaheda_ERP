using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Data;

namespace AlMotaheda.Modules.Access.Tests.Unit;

/// <summary>The seed data must match the prototype's role definitions exactly (design, "Role defaults"), plus the HR role and module.</summary>
public sealed class PermissionCatalogTests
{
    [Fact]
    public void Catalog_has_19_modules_and_95_permissions_with_unique_ids()
    {
        var permissions = PermissionCatalog.SeedPermissions().ToList();

        Assert.Equal(19, PermissionCatalog.Modules.Count);
        Assert.Equal(95, permissions.Count);
        Assert.Equal(95, permissions.Select(permission => permission.Id).Distinct().Count());
        Assert.Equal(95, permissions.Select(permission => (permission.Module, permission.Action)).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 95), permissions.Select(permission => permission.Id).Order());

        // Appended, so the prototype's 85 permissions keep their ids.
        Assert.Equal(86, PermissionCatalog.PermissionId("hr", PermissionAction.View));
        Assert.Equal(91, PermissionCatalog.PermissionId("procurement", PermissionAction.View));
    }

    [Fact]
    public void Nine_roles_are_seeded_with_256_role_permissions()
    {
        var roles = PermissionCatalog.SeedRoles().ToList();
        var links = PermissionCatalog.SeedRolePermissions().ToList();

        Assert.Equal(["executive", "manager", "engineer", "office_admin", "accountant", "staff", "hr", "purchasing", "financial_manager"], roles.Select(role => role.Code));
        Assert.Equal(256, links.Count);
        Assert.Equal(256, links.Select(link => (link.RoleId, link.PermissionId)).Distinct().Count());

        var countsByRole = links.GroupBy(link => link.RoleId).ToDictionary(group => group.Key, group => group.Count());
        Assert.Equal(92, countsByRole[PermissionCatalog.RoleId("executive")]);
        Assert.Equal(41, countsByRole[PermissionCatalog.RoleId("manager")]);
        Assert.Equal(18, countsByRole[PermissionCatalog.RoleId("engineer")]);
        Assert.Equal(8, countsByRole[PermissionCatalog.RoleId("office_admin")]);
        // Catalog Edit (docs/cost-flow §2.1): the Accountant asks for price changes.
        Assert.Equal(57, countsByRole[PermissionCatalog.RoleId("accountant")]);
        Assert.Equal(9, countsByRole[PermissionCatalog.RoleId("staff")]);
        Assert.Equal(4, countsByRole[PermissionCatalog.RoleId("hr")]);
        Assert.Equal(4, countsByRole[PermissionCatalog.RoleId("purchasing")]);
        Assert.Equal(23, countsByRole[PermissionCatalog.RoleId("financial_manager")]);
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
    [InlineData("hr", "hr", "VCEF")]
    [InlineData("accountant", "catalog", "VEF")]
    [InlineData("financial_manager", "catalog", "VAF")]
    [InlineData("financial_manager", "payments", "VF")]
    [InlineData("financial_manager", "accounts", "")]
    [InlineData("financial_manager", "hr", "")]
    [InlineData("hr", "payments", "")]
    [InlineData("accountant", "hr", "VAF")]
    [InlineData("executive", "hr", "VCEAF")]
    [InlineData("manager", "hr", "")]
    [InlineData("purchasing", "procurement", "VCEF")]
    [InlineData("purchasing", "site", "")]
    [InlineData("purchasing", "payments", "")]
    [InlineData("manager", "procurement", "VCAF")]
    [InlineData("engineer", "procurement", "VC")]
    [InlineData("accountant", "procurement", "VAF")]
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
