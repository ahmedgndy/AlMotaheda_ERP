using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Security;

namespace AlMotaheda.Modules.Access.Tests.Unit;

/// <summary>Resolution order from the design: a user's override decides a whole module; otherwise the role decides.</summary>
public sealed class EffectivePermissionsTests
{
    private static readonly (string, PermissionAction)[] ManagerLikeRole =
    [
        ("projects", PermissionAction.View), ("projects", PermissionAction.Create), ("projects", PermissionAction.Edit),
        ("reports", PermissionAction.View),
    ];

    [Fact]
    public void Without_overrides_the_role_decides()
    {
        var permissions = EffectivePermissions.Combine(ManagerLikeRole, [], ignoreOverrides: false);

        Assert.True(permissions.Has("projects", PermissionAction.Edit));
        Assert.False(permissions.Has("projects", PermissionAction.Approve));
        Assert.False(permissions.Has("accounts", PermissionAction.View));
        Assert.Empty(permissions.OverriddenModules);
    }

    [Fact]
    public void An_override_replaces_all_five_actions_of_its_module_and_nothing_else()
    {
        (string, PermissionAction, bool)[] overrides =
        [
            ("projects", PermissionAction.View, true), ("projects", PermissionAction.Create, false),
            ("projects", PermissionAction.Edit, false), ("projects", PermissionAction.Approve, true),
            ("projects", PermissionAction.SeeMoney, false),
        ];

        var permissions = EffectivePermissions.Combine(ManagerLikeRole, overrides, ignoreOverrides: false);

        Assert.Equal([PermissionAction.View, PermissionAction.Approve], permissions.For("projects"));
        Assert.True(permissions.Has("reports", PermissionAction.View));
        Assert.Equal(["projects"], permissions.OverriddenModules);
    }

    [Fact]
    public void An_override_that_denies_everything_removes_the_module()
    {
        var overrides = PermissionActions.All.Select(action => ("projects", action, false));

        var permissions = EffectivePermissions.Combine(ManagerLikeRole, overrides, ignoreOverrides: false);

        Assert.Empty(permissions.For("projects"));
        Assert.False(permissions.ToResponse().ContainsKey("projects"));
        Assert.Contains("projects", permissions.OverriddenModules);
    }

    [Fact]
    public void Executives_ignore_overrides()
    {
        var overrides = PermissionActions.All.Select(action => ("projects", action, false));

        var permissions = EffectivePermissions.Combine(ManagerLikeRole, overrides, ignoreOverrides: true);

        Assert.True(permissions.Has("projects", PermissionAction.Edit));
        Assert.Empty(permissions.OverriddenModules);
    }

    [Fact]
    public void Claims_and_api_response_list_modules_in_catalog_order_with_ordered_actions()
    {
        var permissions = EffectivePermissions.Combine(
            [("reports", PermissionAction.Edit), ("projects", PermissionAction.SeeMoney), ("projects", PermissionAction.View)],
            [],
            ignoreOverrides: false);

        Assert.Equal(["projects:VF", "reports:E"], permissions.ToClaimValues());
        Assert.Equal(["projects", "reports"], permissions.ToResponse().Keys);
        Assert.Equal(["View", "SeeMoney"], permissions.ToResponse()["projects"]);
    }

    [Theory]
    [InlineData("View", true)]
    [InlineData(" seemoney ", true)]
    [InlineData("Create,Edit", false)]
    [InlineData("View, Edit", false)]
    [InlineData("1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_exact_action_names_are_accepted(string? input, bool accepted)
    {
        Assert.Equal(accepted, PermissionActions.TryParse(input, out _));
    }

    [Fact]
    public void Permission_claims_are_read_back_by_the_shared_check()
    {
        var identity = new ClaimsIdentity(
            [new Claim(AccessClaims.Permission, "projects:VF"), new Claim(AccessClaims.Permission, "accounts:V")],
            authenticationType: "test");
        var user = new ClaimsPrincipal(identity);

        Assert.True(user.HasPermission("projects", PermissionAction.SeeMoney));
        Assert.True(user.HasPermission("accounts", PermissionAction.View));
        Assert.False(user.HasPermission("accounts", PermissionAction.Create));
        Assert.False(user.HasPermission("project", PermissionAction.View));
        Assert.False(new ClaimsPrincipal(new ClaimsIdentity(identity.Claims)).HasPermission("projects", PermissionAction.View));
    }
}
