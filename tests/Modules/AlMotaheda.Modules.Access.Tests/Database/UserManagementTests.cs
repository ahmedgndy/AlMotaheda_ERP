using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Access.Tests.Database;

/// <summary>Users screens, and the mandatory rule: only an active Executive manages users.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class UserManagementTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task Executive_creates_a_user_who_must_change_their_first_password()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();

        var response = await api.SendAsync(HttpMethod.Post, "/api/users", executive, new
        {
            email = "  Sara.Engineer@AlMotaheda.test ",
            phone = "+974 5555 0101",
            nameAr = "سارة",
            nameEn = "Sara",
            jobTitleAr = "مهندسة موقع",
            jobTitleEn = "Site engineer",
            role = "engineer",
            initialPassword = AccessApi.UserPassword,
        });
        var body = await AccessApi.Json(response);
        var stored = await api.QueryAsync(db => db.Users.AsNoTracking().SingleAsync(user => user.Id == body.GetProperty("id").GetInt32()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal($"/api/users/{stored.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal("Sara.Engineer@AlMotaheda.test", body.GetProperty("email").GetString());
        Assert.Equal("+97455550101", body.GetProperty("phone").GetString());
        Assert.Equal("engineer", body.GetProperty("role").GetString());
        Assert.Equal("Active", body.GetProperty("status").GetString());
        Assert.True(body.GetProperty("mustChangePassword").GetBoolean());
        Assert.False(body.TryGetProperty("passwordHash", out _));
        Assert.DoesNotContain(AccessApi.UserPassword, body.GetRawText());
        Assert.Equal("SARA.ENGINEER@ALMOTAHEDA.TEST", stored.NormalizedEmail);
        Assert.Equal(executive.UserId, stored.CreatedBy);
    }

    [DatabaseFact]
    public async Task Email_and_phone_must_be_unique()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        await api.CreateUserAsync(executive, "staff", email: "driver@almotaheda.test", phone: "+97450000001");

        var sameEmail = await api.SendAsync(HttpMethod.Post, "/api/users", executive, NewUser("DRIVER@almotaheda.test", null));
        var samePhone = await api.SendAsync(HttpMethod.Post, "/api/users", executive, NewUser("other@almotaheda.test", "+974 5000 0001"));

        Assert.Equal(HttpStatusCode.Conflict, sameEmail.StatusCode);
        Assert.Equal("email_taken", await AccessApi.CodeOf(sameEmail));
        Assert.Equal(HttpStatusCode.Conflict, samePhone.StatusCode);
        Assert.Equal("phone_taken", await AccessApi.CodeOf(samePhone));
    }

    [DatabaseFact]
    public async Task Invalid_input_lists_each_failing_field()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();

        var response = await api.SendAsync(HttpMethod.Post, "/api/users", executive, new
        {
            email = "not-an-email", phone = "12", nameAr = "", nameEn = new string('x', 151), role = "boss", initialPassword = "short",
        });
        var errors = (await AccessApi.Json(response)).GetProperty("errors");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        foreach (var field in new[] { "email", "phone", "nameAr", "nameEn", "role", "initialPassword" })
        {
            Assert.True(errors.TryGetProperty(field, out _), $"Expected an error for {field}");
        }
    }

    [DatabaseFact]
    public async Task Accountant_can_view_users_but_cannot_change_anything()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (_, accountant) = await api.CreateSignedInUserAsync(executive, "accountant");
        var target = await api.CreateUserAsync(executive, "staff");
        var id = target.GetProperty("id").GetInt32();
        var rowVersion = target.GetProperty("rowVersion").GetString();

        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, "/api/users", accountant)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, $"/api/users/{id}", accountant)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, "/api/roles", accountant)).StatusCode);

        // Every way to grant access or take over an account, called directly against the API.
        var attempts = new (HttpMethod Method, string Path, object Body)[]
        {
            (HttpMethod.Post, "/api/users", NewUser("sneaky@almotaheda.test", null, role: "executive")),
            (HttpMethod.Put, $"/api/users/{id}", new { email = "x@almotaheda.test", nameAr = "س", nameEn = "X", role = "executive", rowVersion }),
            (HttpMethod.Post, $"/api/users/{id}/disable", new { rowVersion }),
            (HttpMethod.Post, $"/api/users/{id}/reset-password", new { newPassword = "taken-over-account", rowVersion }),
            (HttpMethod.Put, $"/api/users/{accountant.UserId}/permissions/accounts", new { actions = new[] { "View", "Create", "Edit" }, rowVersion }),
            (HttpMethod.Post, $"/api/users/{id}/permissions/reset", new { rowVersion }),
            (HttpMethod.Put, "/api/roles/accountant/permissions", new { permissions = new Dictionary<string, string[]> { ["accounts"] = ["View", "Create", "Edit"] }, rowVersion }),
        };

        foreach (var (method, path, body) in attempts)
        {
            var response = await api.SendAsync(method, path, accountant, body);
            Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{method} {path} returned {(int)response.StatusCode}");
        }

        var unchanged = await api.QueryAsync(db => db.Users.AsNoTracking().Include(user => user.Role).SingleAsync(user => user.Id == id));
        Assert.Equal("staff", unchanged.Role.Code);
        Assert.Equal(UserStatus.Active, unchanged.Status);
        Assert.Equal(0, await api.QueryAsync(db => db.UserPermissionOverrides.CountAsync()));
    }

    [DatabaseFact]
    public async Task Users_without_accounts_access_can_read_only_themselves()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (manager, managerSession) = await api.CreateSignedInUserAsync(executive, "manager");

        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, $"/api/users/{manager.GetProperty("id").GetInt32()}", managerSession)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, $"/api/users/{manager.GetProperty("id").GetInt32()}/permissions", managerSession)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, $"/api/users/{executive.UserId}", managerSession)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, "/api/users", managerSession)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, "/api/roles", managerSession)).StatusCode);
    }

    [DatabaseFact]
    public async Task Executive_cannot_disable_themselves_or_change_their_own_role()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var self = await AccessApi.Json(await api.SendAsync(HttpMethod.Get, $"/api/users/{executive.UserId}", executive));

        var disable = await api.SendAsync(HttpMethod.Post, $"/api/users/{executive.UserId}/disable", executive,
            new { rowVersion = self.GetProperty("rowVersion").GetString() });
        var demote = await api.SendAsync(HttpMethod.Put, $"/api/users/{executive.UserId}", executive, new
        {
            email = AccessApi.ExecutiveEmail, nameAr = "مدير", nameEn = "Boss", role = "manager",
            rowVersion = self.GetProperty("rowVersion").GetString(),
        });
        var renameSelf = await api.SendAsync(HttpMethod.Put, $"/api/users/{executive.UserId}", executive, new
        {
            email = AccessApi.ExecutiveEmail, nameAr = "مدير", nameEn = "Boss", role = "executive",
            rowVersion = self.GetProperty("rowVersion").GetString(),
        });

        Assert.Equal(HttpStatusCode.Forbidden, disable.StatusCode);
        Assert.Equal("self_change_not_allowed", await AccessApi.CodeOf(disable));
        Assert.Equal(HttpStatusCode.Forbidden, demote.StatusCode);
        Assert.Equal(HttpStatusCode.OK, renameSelf.StatusCode);
    }

    [DatabaseFact]
    public async Task Saving_with_an_old_row_version_is_rejected()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var user = await api.CreateUserAsync(executive, "staff");
        var id = user.GetProperty("id").GetInt32();
        var original = user.GetProperty("rowVersion").GetString();

        var first = await api.SendAsync(HttpMethod.Put, $"/api/users/{id}", executive, UpdateBody(user, "First edit", original));
        var second = await api.SendAsync(HttpMethod.Put, $"/api/users/{id}", executive, UpdateBody(user, "Second edit", original));
        var stored = await api.QueryAsync(db => db.Users.AsNoTracking().SingleAsync(candidate => candidate.Id == id));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("concurrency_conflict", await AccessApi.CodeOf(second));
        Assert.Equal("First edit", stored.NameEn);
    }

    [DatabaseFact]
    public async Task List_searches_filters_and_pages()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        await api.CreateUserAsync(executive, "engineer", email: "omar.site@almotaheda.test");
        await api.CreateUserAsync(executive, "engineer", email: "huda.site@almotaheda.test");
        var staff = await api.CreateUserAsync(executive, "staff", email: "driver.one@almotaheda.test");
        await api.SendAsync(HttpMethod.Post, $"/api/users/{staff.GetProperty("id").GetInt32()}/disable", executive,
            new { rowVersion = staff.GetProperty("rowVersion").GetString() });

        var search = await AccessApi.Json(await api.SendAsync(HttpMethod.Get, "/api/users?search=SITE@", executive));
        var engineers = await AccessApi.Json(await api.SendAsync(HttpMethod.Get, "/api/users?role=engineer&pageSize=1&page=2", executive));
        var disabled = await AccessApi.Json(await api.SendAsync(HttpMethod.Get, "/api/users?status=disabled", executive));
        var badPage = await api.SendAsync(HttpMethod.Get, "/api/users?pageSize=500", executive);

        Assert.Equal(2, search.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, engineers.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, engineers.GetProperty("items").GetArrayLength());
        Assert.Equal(2, engineers.GetProperty("page").GetInt32());
        Assert.Equal("driver.one@almotaheda.test", disabled.GetProperty("items")[0].GetProperty("email").GetString());
        Assert.Equal(1, disabled.GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.BadRequest, badPage.StatusCode);
    }

    [DatabaseFact]
    public async Task A_demoted_executive_loses_user_management_at_once_even_with_a_valid_token()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executiveA = await api.SignInExecutiveAsync();
        var (executiveB, sessionB) = await api.CreateSignedInUserAsync(executiveA, "executive");
        var currentB = await AccessApi.Json(await api.SendAsync(HttpMethod.Get, $"/api/users/{executiveB.GetProperty("id").GetInt32()}", executiveA));

        var demote = await api.SendAsync(HttpMethod.Put, $"/api/users/{executiveB.GetProperty("id").GetInt32()}", executiveA,
            UpdateBody(currentB, "Former executive", currentB.GetProperty("rowVersion").GetString(), role: "manager"));

        // B's access token still says "executive" for up to 15 minutes, but every user-management change
        // re-checks the caller in the database.
        var attempt = await api.SendAsync(HttpMethod.Post, "/api/users", sessionB, NewUser("late@almotaheda.test", null));

        Assert.Equal(HttpStatusCode.OK, demote.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
        Assert.False(await api.QueryAsync(db => db.Users.AnyAsync(user => user.Email == "late@almotaheda.test")));
    }

    [DatabaseFact]
    public async Task Two_executives_disabling_each_other_at_once_leave_one_active()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executiveA = await api.SignInExecutiveAsync();
        var (executiveB, sessionB) = await api.CreateSignedInUserAsync(executiveA, "executive");
        var idA = executiveA.UserId;
        var idB = executiveB.GetProperty("id").GetInt32();
        var rowA = (await AccessApi.Json(await api.SendAsync(HttpMethod.Get, $"/api/users/{idA}", executiveA))).GetProperty("rowVersion").GetString();
        var rowB = (await AccessApi.Json(await api.SendAsync(HttpMethod.Get, $"/api/users/{idB}", executiveA))).GetProperty("rowVersion").GetString();

        var results = await Task.WhenAll(
            api.SendAsync(HttpMethod.Post, $"/api/users/{idB}/disable", executiveA, new { rowVersion = rowB }),
            api.SendAsync(HttpMethod.Post, $"/api/users/{idA}/disable", sessionB, new { rowVersion = rowA }));

        var activeExecutives = await api.QueryAsync(db => db.Users.CountAsync(user => user.Status == UserStatus.Active && user.Role.Code == "executive"));

        Assert.Equal(1, results.Count(response => response.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, activeExecutives);
    }

    [DatabaseFact]
    public async Task Promoting_a_user_to_executive_clears_their_own_permissions()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var user = await api.CreateUserAsync(executive, "manager");
        var id = user.GetProperty("id").GetInt32();

        var customised = await AccessApi.Json(await api.SendAsync(HttpMethod.Put, $"/api/users/{id}/permissions/accounts", executive,
            new { actions = new[] { "View" }, rowVersion = user.GetProperty("rowVersion").GetString() }));
        var current = await AccessApi.Json(await api.SendAsync(HttpMethod.Get, $"/api/users/{id}", executive));
        var promote = await api.SendAsync(HttpMethod.Put, $"/api/users/{id}", executive,
            UpdateBody(current, "Promoted", current.GetProperty("rowVersion").GetString(), role: "executive"));

        Assert.Equal(["accounts"], customised.GetProperty("overriddenModules").EnumerateArray().Select(module => module.GetString()));
        Assert.Equal(HttpStatusCode.OK, promote.StatusCode);
        Assert.Equal(0, await api.QueryAsync(db => db.UserPermissionOverrides.CountAsync(link => link.UserId == id)));
    }

    private static object NewUser(string email, string? phone, string role = "staff") => new
    {
        email, phone, nameAr = "مستخدم", nameEn = "User", role, initialPassword = AccessApi.UserPassword,
    };

    private static object UpdateBody(JsonElement user, string nameEn, string? rowVersion, string? role = null) => new
    {
        email = user.GetProperty("email").GetString(),
        phone = user.GetProperty("phone").GetString(),
        nameAr = user.GetProperty("nameAr").GetString(),
        nameEn,
        jobTitleAr = user.GetProperty("jobTitleAr").GetString(),
        jobTitleEn = user.GetProperty("jobTitleEn").GetString(),
        role = role ?? user.GetProperty("role").GetString(),
        rowVersion,
    };
}
