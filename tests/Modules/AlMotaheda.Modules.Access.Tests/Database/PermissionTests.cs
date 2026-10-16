using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Access.Tests.Database;

/// <summary>Role permissions, per-user overrides, and how changes reach signed-in users.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class PermissionTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task Roles_endpoint_returns_the_seeded_matrix()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();

        var body = await AccessApi.Json(await api.SendAsync(HttpMethod.Get, "/api/roles", executive));
        var roles = body.GetProperty("roles").EnumerateArray().ToDictionary(role => role.GetProperty("code").GetString()!);

        Assert.Equal(19, body.GetProperty("modules").GetArrayLength());
        Assert.Equal(["View", "Create", "Edit", "Approve", "SeeMoney"], body.GetProperty("actions").EnumerateArray().Select(action => action.GetString()));
        Assert.Equal(8, roles.Count);
        Assert.Equal(["View", "Create", "Edit", "SeeMoney"], Actions(roles["hr"], "hr"));
        Assert.Equal(["View", "Create", "Edit", "SeeMoney"], Actions(roles["purchasing"], "procurement"));
        Assert.False(roles["executive"].GetProperty("isEditable").GetBoolean());
        Assert.True(roles["manager"].GetProperty("isEditable").GetBoolean());
        Assert.Equal(["View", "Edit", "Approve"], Actions(roles["manager"], "equipment"));
        Assert.False(roles["manager"].GetProperty("permissions").TryGetProperty("accounts", out _));
    }

    [DatabaseFact]
    public async Task Role_change_reaches_signed_in_users_at_their_next_refresh()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (_, manager) = await api.CreateSignedInUserAsync(executive, "manager");
        var managerRole = await Role(api, executive, "manager");

        var permissions = managerRole.GetProperty("permissions").Deserialize<Dictionary<string, string[]>>()!;
        permissions["accounts"] = ["View"];
        var update = await api.SendAsync(HttpMethod.Put, "/api/roles/manager/permissions", executive,
            new { permissions, rowVersion = managerRole.GetProperty("rowVersion").GetString() });

        var beforeRefresh = await api.SendAsync(HttpMethod.Get, "/api/users", manager);
        var refreshed = await Session.FromAsync(await api.RefreshAsync(manager.RefreshCookie));
        var afterRefresh = await api.SendAsync(HttpMethod.Get, "/api/users", refreshed);

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(["View"], Actions(await AccessApi.Json(update), "accounts"));
        Assert.Equal(HttpStatusCode.Forbidden, beforeRefresh.StatusCode);
        Assert.Equal(HttpStatusCode.OK, afterRefresh.StatusCode);
    }

    [DatabaseFact]
    public async Task Role_permissions_need_the_current_row_version()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var staffRole = await Role(api, executive, "staff");
        var rowVersion = staffRole.GetProperty("rowVersion").GetString();
        var permissions = new Dictionary<string, string[]> { ["reports"] = ["View"] };

        var first = await api.SendAsync(HttpMethod.Put, "/api/roles/staff/permissions", executive, new { permissions, rowVersion });
        var second = await api.SendAsync(HttpMethod.Put, "/api/roles/staff/permissions", executive, new { permissions, rowVersion });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, await api.QueryAsync(db => db.RolePermissions.CountAsync(link => link.RoleId == PermissionCatalog.RoleId("staff"))));
    }

    [DatabaseFact]
    public async Task Executive_role_and_executives_cannot_be_customised()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var otherExecutive = await api.CreateUserAsync(executive, "executive");
        var executiveRole = await Role(api, executive, "executive");

        var roleEdit = await api.SendAsync(HttpMethod.Put, "/api/roles/executive/permissions", executive,
            new { permissions = new Dictionary<string, string[]>(), rowVersion = executiveRole.GetProperty("rowVersion").GetString() });
        var userEdit = await api.SendAsync(HttpMethod.Put, $"/api/users/{otherExecutive.GetProperty("id").GetInt32()}/permissions/projects", executive,
            new { actions = Array.Empty<string>(), rowVersion = otherExecutive.GetProperty("rowVersion").GetString() });

        Assert.Equal(HttpStatusCode.Conflict, roleEdit.StatusCode);
        Assert.Equal("executive_role_locked", await AccessApi.CodeOf(roleEdit));
        Assert.Equal(HttpStatusCode.Conflict, userEdit.StatusCode);
        Assert.Equal(92, await api.QueryAsync(db => db.RolePermissions.CountAsync(link => link.RoleId == PermissionCatalog.RoleId("executive"))));
    }

    [DatabaseFact]
    public async Task Customising_a_module_for_one_user_replaces_the_role_for_that_module()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (manager, managerSession) = await api.CreateSignedInUserAsync(executive, "manager");
        var id = manager.GetProperty("id").GetInt32();
        var current = await AccessApi.Json(await api.SendAsync(HttpMethod.Get, $"/api/users/{id}/permissions", executive));

        var customise = await api.SendAsync(HttpMethod.Put, $"/api/users/{id}/permissions/accounts", executive,
            new { actions = new[] { "View" }, rowVersion = current.GetProperty("rowVersion").GetString() });
        var removeProjects = await api.SendAsync(HttpMethod.Put, $"/api/users/{id}/permissions/projects", executive,
            new { actions = Array.Empty<string>(), rowVersion = (await AccessApi.Json(customise)).GetProperty("rowVersion").GetString() });
        var view = await AccessApi.Json(removeProjects);
        var rows = await api.QueryAsync(db => db.UserPermissionOverrides.CountAsync(link => link.UserId == id));

        var refreshed = await Session.FromAsync(await api.RefreshAsync(managerSession.RefreshCookie));
        var listAsManager = await api.SendAsync(HttpMethod.Get, "/api/users", refreshed);

        Assert.Equal(HttpStatusCode.OK, customise.StatusCode);
        Assert.Equal(["projects", "accounts"], view.GetProperty("overriddenModules").EnumerateArray().Select(module => module.GetString()));
        Assert.Equal(["View"], Actions(view, "accounts"));
        Assert.False(view.GetProperty("permissions").TryGetProperty("projects", out _));
        Assert.True(view.GetProperty("permissions").TryGetProperty("reports", out _));
        Assert.Equal(10, rows); // five rows per customised module
        Assert.Equal(HttpStatusCode.OK, listAsManager.StatusCode);

        var reset = await api.SendAsync(HttpMethod.Post, $"/api/users/{id}/permissions/reset", executive,
            new { rowVersion = view.GetProperty("rowVersion").GetString() });
        var afterReset = await Session.FromAsync(await api.RefreshAsync(refreshed.RefreshCookie));

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Empty((await AccessApi.Json(reset)).GetProperty("overriddenModules").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, "/api/users", afterReset)).StatusCode);
    }

    [DatabaseFact]
    public async Task Unknown_modules_and_actions_are_rejected()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var user = await api.CreateUserAsync(executive, "staff");
        var id = user.GetProperty("id").GetInt32();
        var rowVersion = user.GetProperty("rowVersion").GetString();

        var unknownModule = await api.SendAsync(HttpMethod.Put, $"/api/users/{id}/permissions/payroll", executive, new { actions = new[] { "View" }, rowVersion });
        var unknownAction = await api.SendAsync(HttpMethod.Put, $"/api/users/{id}/permissions/projects", executive, new { actions = new[] { "Delete" }, rowVersion });
        var numericAction = await api.SendAsync(HttpMethod.Put, $"/api/users/{id}/permissions/projects", executive, new { actions = new[] { "1" }, rowVersion });

        Assert.Equal(HttpStatusCode.NotFound, unknownModule.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, unknownAction.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, numericAction.StatusCode);
    }

    [DatabaseFact]
    public async Task Me_returns_fresh_profile_and_permissions()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (_, staff) = await api.CreateSignedInUserAsync(executive, "staff");

        var me = await AccessApi.Json(await api.SendAsync(HttpMethod.Get, "/api/me", staff));

        Assert.Equal("staff", me.GetProperty("role").GetString());
        Assert.Equal(["View", "Create"], Actions(me, "fuel"));
        Assert.False(me.GetProperty("mustChangePassword").GetBoolean());
        Assert.False(me.TryGetProperty("passwordHash", out _));
    }

    private static async Task<JsonElement> Role(AccessApi api, Session session, string code)
    {
        var body = await AccessApi.Json(await api.SendAsync(HttpMethod.Get, "/api/roles", session));
        return body.GetProperty("roles").EnumerateArray().Single(role => role.GetProperty("code").GetString() == code);
    }

    private static IEnumerable<string?> Actions(JsonElement holder, string module) =>
        holder.GetProperty("permissions").GetProperty(module).EnumerateArray().Select(action => action.GetString());
}
