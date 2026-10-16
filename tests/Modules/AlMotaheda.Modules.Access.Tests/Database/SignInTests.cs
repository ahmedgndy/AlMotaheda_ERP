using System.Net;
using AlMotaheda.Modules.Access.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Access.Tests.Database;

[Collection(SqlServerCollection.Name)]
public sealed class SignInTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task Startup_applies_the_migration_with_seed_data_and_creates_the_first_executive()
    {
        await using var api = await AccessApi.StartAsync(database);

        var counts = await api.QueryAsync(async db => new
        {
            Roles = await db.Roles.CountAsync(),
            Permissions = await db.Permissions.CountAsync(),
            RolePermissions = await db.RolePermissions.CountAsync(),
            Executive = await db.Users.Include(user => user.Role).SingleAsync(),
        });

        Assert.Equal(8, counts.Roles);
        Assert.Equal(95, counts.Permissions);
        Assert.Equal(232, counts.RolePermissions);
        Assert.Equal(AccessApi.ExecutiveEmail, counts.Executive.Email);
        Assert.Equal("executive", counts.Executive.Role.Code);
        Assert.True(counts.Executive.MustChangePassword);
        Assert.StartsWith("AQAAAA", counts.Executive.PasswordHash); // ASP.NET Core hash format, never the password
    }

    [DatabaseFact]
    public async Task First_sign_in_must_change_the_password_before_doing_anything_else()
    {
        await using var api = await AccessApi.StartAsync(database);

        var first = await api.LoginAsync(AccessApi.ExecutiveEmail, AccessApi.BootstrapPassword);
        var blocked = await api.SendAsync(HttpMethod.Get, "/api/users", first);
        var me = await api.SendAsync(HttpMethod.Get, "/api/me", first);
        var changed = await api.SendAsync(HttpMethod.Post, "/api/auth/change-password", first,
            new { currentPassword = AccessApi.BootstrapPassword, newPassword = AccessApi.ExecutivePassword });
        var after = await Session.FromAsync(changed);
        var allowed = await api.SendAsync(HttpMethod.Get, "/api/users", after);

        Assert.True(first.MustChangePassword);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("password_change_required", await AccessApi.CodeOf(blocked));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.False(after.MustChangePassword);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [DatabaseFact]
    public async Task Wrong_password_and_unknown_email_get_the_same_answer()
    {
        await using var api = await AccessApi.StartAsync(database);

        var wrongPassword = await api.PostLoginAsync(AccessApi.ExecutiveEmail, "not-the-password");
        var unknownEmail = await api.PostLoginAsync("nobody@almotaheda.test", "not-the-password");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);

        var first = await AccessApi.Json(wrongPassword);
        var second = await AccessApi.Json(unknownEmail);
        Assert.Equal("invalid_credentials", first.GetProperty("code").GetString());
        Assert.Equal(first.GetProperty("code").GetString(), second.GetProperty("code").GetString());
        Assert.Equal(first.GetProperty("title").GetString(), second.GetProperty("title").GetString());
    }

    [DatabaseFact]
    public async Task Email_is_matched_without_regard_to_case_or_spaces()
    {
        await using var api = await AccessApi.StartAsync(database);

        var response = await api.PostLoginAsync("  EXECUTIVE@AlMotaheda.TEST ", AccessApi.BootstrapPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [DatabaseFact]
    public async Task Five_wrong_passwords_lock_the_account_until_an_executive_resets_it()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var user = await api.CreateUserAsync(executive, "engineer");
        var email = user.GetProperty("email").GetString()!;
        var id = user.GetProperty("id").GetInt32();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await api.PostLoginAsync(email, "wrong-password-" + attempt)).StatusCode);
        }

        var correctWhileLocked = await api.PostLoginAsync(email, AccessApi.UserPassword);
        var stored = await api.QueryAsync(db => db.UserSignInStates.AsNoTracking().SingleAsync(candidate => candidate.UserId == id));
        var details = await AccessApi.Json(await api.SendAsync(HttpMethod.Get, $"/api/users/{id}", executive));

        Assert.Equal(HttpStatusCode.Unauthorized, correctWhileLocked.StatusCode);
        Assert.NotNull(stored.LockoutEndsAt);
        Assert.InRange(stored.LockoutEndsAt!.Value, DateTime.UtcNow.AddMinutes(14), DateTime.UtcNow.AddMinutes(16));
        Assert.Equal(5, stored.FailedLoginCount); // the fifth attempt locked it; attempts during the lock aren't counted
        Assert.True(details.GetProperty("isLockedOut").GetBoolean());

        var reset = await api.SendAsync(HttpMethod.Post, $"/api/users/{id}/reset-password", executive,
            new { newPassword = "a-brand-new-password", rowVersion = details.GetProperty("rowVersion").GetString() });
        var afterReset = await api.PostLoginAsync(email, "a-brand-new-password");

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(HttpStatusCode.OK, afterReset.StatusCode);
        Assert.True((await Session.FromAsync(afterReset)).MustChangePassword);
    }

    [DatabaseFact]
    public async Task Successful_sign_in_clears_failures_and_records_the_time()
    {
        await using var api = await AccessApi.StartAsync(database);
        await api.PostLoginAsync(AccessApi.ExecutiveEmail, "wrong-once");

        await api.LoginAsync(AccessApi.ExecutiveEmail, AccessApi.BootstrapPassword);
        var stored = await api.QueryAsync(db => db.UserSignInStates.AsNoTracking().SingleAsync());

        Assert.Equal(0, stored.FailedLoginCount);
        Assert.Null(stored.LockoutEndsAt);
        Assert.NotNull(stored.LastLoginAt);
        Assert.Equal(DateTimeKind.Utc, stored.LastLoginAt!.Value.Kind);
    }

    [DatabaseFact]
    public async Task Remember_me_makes_the_cookie_outlive_the_browser_session()
    {
        await using var api = await AccessApi.StartAsync(database);

        var remembered = await api.PostLoginAsync(AccessApi.ExecutiveEmail, AccessApi.BootstrapPassword, rememberMe: true);
        var notRemembered = await api.PostLoginAsync(AccessApi.ExecutiveEmail, AccessApi.BootstrapPassword, rememberMe: false);

        Assert.Contains("expires=", remembered.Headers.GetValues("Set-Cookie").Single(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", notRemembered.Headers.GetValues("Set-Cookie").Single(), StringComparison.OrdinalIgnoreCase);

        var tokens = await api.QueryAsync(db => db.RefreshTokens.AsNoTracking().OrderBy(token => token.Id).ToListAsync());
        Assert.InRange(tokens[0].ExpiresAt - tokens[0].CreatedAt, TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1), TimeSpan.FromDays(30));
        Assert.InRange(tokens[1].ExpiresAt - tokens[1].CreatedAt, TimeSpan.FromHours(12) - TimeSpan.FromSeconds(1), TimeSpan.FromHours(12));
        Assert.All(tokens, token => Assert.Equal(32, token.TokenHash.Length));
    }

    [DatabaseFact]
    public async Task Disabled_users_cannot_sign_in()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var user = await api.CreateUserAsync(executive, "staff");

        var disabled = await api.SendAsync(HttpMethod.Post, $"/api/users/{user.GetProperty("id").GetInt32()}/disable", executive,
            new { rowVersion = user.GetProperty("rowVersion").GetString() });
        var signIn = await api.PostLoginAsync(user.GetProperty("email").GetString()!, AccessApi.UserPassword);

        Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, signIn.StatusCode);
        Assert.Equal("invalid_credentials", await AccessApi.CodeOf(signIn));
    }

    [DatabaseFact]
    public async Task Wrong_current_password_is_refused_and_counted()
    {
        await using var api = await AccessApi.StartAsync(database);
        var session = await api.LoginAsync(AccessApi.ExecutiveEmail, AccessApi.BootstrapPassword);

        var response = await api.SendAsync(HttpMethod.Post, "/api/auth/change-password", session,
            new { currentPassword = "not-it-at-all", newPassword = "another-long-password" });
        var stored = await api.QueryAsync(db => db.UserSignInStates.AsNoTracking().SingleAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_current_password", await AccessApi.CodeOf(response));
        Assert.Equal(1, stored.FailedLoginCount);
    }

    [DatabaseFact]
    public async Task Weak_new_password_is_refused_with_weak_password()
    {
        await using var api = await AccessApi.StartAsync(database);
        var session = await api.LoginAsync(AccessApi.ExecutiveEmail, AccessApi.BootstrapPassword);

        var response = await api.SendAsync(HttpMethod.Post, "/api/auth/change-password", session,
            new { currentPassword = AccessApi.BootstrapPassword, newPassword = "short" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("weak_password", await AccessApi.CodeOf(response));
    }
}
