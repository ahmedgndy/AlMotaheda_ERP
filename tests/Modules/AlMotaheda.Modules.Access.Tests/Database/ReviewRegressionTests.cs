using System.Net;
using AlMotaheda.Modules.Access.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Access.Tests.Database;

/// <summary>Regression tests for the issues found in the independent review.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class ReviewRegressionTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task Copied_executive_token_stops_working_for_user_management_once_their_password_is_reset()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executiveA = await api.SignInExecutiveAsync();
        var (executiveB, sessionB) = await api.CreateSignedInUserAsync(executiveA, "executive");
        var currentB = await AccessApi.Json(await api.SendAsync(HttpMethod.Get, $"/api/users/{executiveB.GetProperty("id").GetInt32()}", executiveA));

        var reset = await api.SendAsync(HttpMethod.Post, $"/api/users/{executiveB.GetProperty("id").GetInt32()}/reset-password", executiveA,
            new { newPassword = "reset-by-executive-a", rowVersion = currentB.GetProperty("rowVersion").GetString() });

        // B's access token is still within its 15 minutes and still says "executive".
        var backdoor = await api.SendAsync(HttpMethod.Post, "/api/users", sessionB, new
        {
            email = "backdoor@almotaheda.test", nameAr = "باب", nameEn = "Backdoor", role = "executive", initialPassword = "attacker-chosen-password",
        });

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, backdoor.StatusCode);
        Assert.False(await api.QueryAsync(db => db.Users.AnyAsync(user => user.Email == "backdoor@almotaheda.test")));
    }

    [DatabaseFact]
    public async Task Executive_token_stops_working_for_user_management_after_sign_out()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();

        await api.SendAsync(HttpMethod.Post, "/api/auth/logout", executive);
        var attempt = await api.SendAsync(HttpMethod.Post, "/api/users", executive, new
        {
            email = "after-logout@almotaheda.test", nameAr = "س", nameEn = "X", role = "staff", initialPassword = AccessApi.UserPassword,
        });

        Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
    }

    [DatabaseFact]
    public async Task Disabled_executive_token_cannot_manage_users()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executiveA = await api.SignInExecutiveAsync();
        var (executiveB, sessionB) = await api.CreateSignedInUserAsync(executiveA, "executive");

        await api.SendAsync(HttpMethod.Post, $"/api/users/{executiveB.GetProperty("id").GetInt32()}/disable", executiveA, new { });
        var attempt = await api.SendAsync(HttpMethod.Post, $"/api/users/{executiveA.UserId}/disable", sessionB, new { });

        Assert.Equal(HttpStatusCode.Forbidden, attempt.StatusCode);
    }

    [DatabaseFact]
    public async Task Parallel_wrong_passwords_cannot_get_past_the_lockout()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var user = await api.CreateUserAsync(executive, "engineer");
        var email = user.GetProperty("email").GetString()!;
        var id = user.GetProperty("id").GetInt32();

        var guesses = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => api.PostLoginAsync(email, "guess-number-" + index)));
        var correct = await api.PostLoginAsync(email, AccessApi.UserPassword);
        var state = await api.QueryAsync(db => db.UserSignInStates.AsNoTracking().SingleAsync(candidate => candidate.UserId == id));

        Assert.All(guesses, response => Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode));
        Assert.Equal(HttpStatusCode.Unauthorized, correct.StatusCode);
        Assert.NotNull(state.LockoutEndsAt);
        Assert.InRange(state.FailedLoginCount, (short)1, (short)5);
    }

    [DatabaseFact]
    public async Task Signing_in_does_not_invalidate_an_open_edit_form()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var user = await api.CreateUserAsync(executive, "staff");
        var email = user.GetProperty("email").GetString()!;

        // The Executive opens the user's page, then the user signs in (and mistypes once).
        await api.PostLoginAsync(email, "a-typo-password");
        await api.LoginAsync(email, AccessApi.UserPassword);

        var save = await api.SendAsync(HttpMethod.Put, $"/api/users/{user.GetProperty("id").GetInt32()}", executive, new
        {
            email, nameAr = "مستخدم", nameEn = "Renamed while the user signed in", role = "staff",
            rowVersion = user.GetProperty("rowVersion").GetString(),
        });

        Assert.Equal(HttpStatusCode.OK, save.StatusCode);
    }

    [DatabaseFact]
    public async Task Disable_works_without_a_row_version_and_enable_restores_sign_in()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var user = await api.CreateUserAsync(executive, "staff");
        var id = user.GetProperty("id").GetInt32();
        var email = user.GetProperty("email").GetString()!;

        var disable = await api.SendAsync(HttpMethod.Post, $"/api/users/{id}/disable", executive, new { });
        var whileDisabled = await api.PostLoginAsync(email, AccessApi.UserPassword);
        var enable = await api.SendAsync(HttpMethod.Post, $"/api/users/{id}/enable", executive,
            new { rowVersion = (await AccessApi.Json(disable)).GetProperty("rowVersion").GetString() });
        var afterEnable = await api.PostLoginAsync(email, AccessApi.UserPassword);

        Assert.Equal(HttpStatusCode.OK, disable.StatusCode);
        Assert.Equal("Disabled", (await AccessApi.Json(disable)).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, whileDisabled.StatusCode);
        Assert.Equal(HttpStatusCode.OK, enable.StatusCode);
        Assert.Equal(HttpStatusCode.OK, afterEnable.StatusCode);
    }

    [DatabaseFact]
    public async Task Self_change_is_refused_with_its_own_code_on_every_route()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var self = await AccessApi.Json(await api.SendAsync(HttpMethod.Get, $"/api/users/{executive.UserId}", executive));
        var rowVersion = self.GetProperty("rowVersion").GetString();

        var reset = await api.SendAsync(HttpMethod.Post, $"/api/users/{executive.UserId}/reset-password", executive,
            new { newPassword = "my-own-new-password", rowVersion });
        var permissions = await api.SendAsync(HttpMethod.Post, $"/api/users/{executive.UserId}/permissions/reset", executive, new { rowVersion });

        Assert.Equal("self_change_not_allowed", await AccessApi.CodeOf(reset));
        Assert.Equal("self_change_not_allowed", await AccessApi.CodeOf(permissions));
    }

    [DatabaseFact]
    public async Task Sign_in_attempts_during_password_resets_never_fail_with_a_server_error()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var user = await api.CreateUserAsync(executive, "staff");
        var id = user.GetProperty("id").GetInt32();
        var email = user.GetProperty("email").GetString()!;

        for (var round = 0; round < 10; round++)
        {
            var current = await AccessApi.Json(await api.SendAsync(HttpMethod.Get, $"/api/users/{id}", executive));
            var responses = await Task.WhenAll(
                api.SendAsync(HttpMethod.Post, $"/api/users/{id}/reset-password", executive,
                    new { newPassword = $"reset-password-round-{round}", rowVersion = current.GetProperty("rowVersion").GetString() }),
                api.PostLoginAsync(email, "wrong-password-" + round),
                api.PostLoginAsync(email, AccessApi.UserPassword));

            Assert.All(responses, response => Assert.True((int)response.StatusCode < 500, $"Round {round}: {(int)response.StatusCode}"));
        }
    }

    [DatabaseFact]
    public async Task Refreshes_during_permission_edits_never_fail_with_a_server_error()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (user, session) = await api.CreateSignedInUserAsync(executive, "manager");
        var id = user.GetProperty("id").GetInt32();
        var cookie = session.RefreshCookie;

        for (var round = 0; round < 10; round++)
        {
            var current = await AccessApi.Json(await api.SendAsync(HttpMethod.Get, $"/api/users/{id}/permissions", executive));
            var edit = api.SendAsync(HttpMethod.Put, $"/api/users/{id}/permissions/accounts", executive,
                new { actions = round % 2 == 0 ? new[] { "View" } : Array.Empty<string>(), rowVersion = current.GetProperty("rowVersion").GetString() });
            var refresh = api.RefreshAsync(cookie);
            var responses = await Task.WhenAll(edit, refresh);

            Assert.All(responses, response => Assert.True((int)response.StatusCode < 500, $"Round {round}: {(int)response.StatusCode}"));
            Assert.Equal(HttpStatusCode.OK, responses[1].StatusCode);
            cookie = AccessApi.RefreshCookieOf(responses[1]);
        }
    }
}
