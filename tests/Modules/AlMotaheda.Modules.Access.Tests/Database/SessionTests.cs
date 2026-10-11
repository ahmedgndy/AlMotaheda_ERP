using System.Net;
using AlMotaheda.Modules.Access.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Access.Tests.Database;

/// <summary>Refresh-token rotation, replay detection and the ways a sign-in ends.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class SessionTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task Refresh_swaps_the_cookie_and_returns_a_new_access_token()
    {
        await using var api = await AccessApi.StartAsync(database);
        var session = await api.SignInExecutiveAsync();

        var response = await api.RefreshAsync(session.RefreshCookie);
        var refreshed = await Session.FromAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.NotNull(refreshed.RefreshCookie);
        Assert.NotEqual(session.RefreshCookie, refreshed.RefreshCookie);
        Assert.NotEqual(session.AccessToken, refreshed.AccessToken);
        Assert.Equal(HttpStatusCode.OK, (await api.SendAsync(HttpMethod.Get, "/api/me", refreshed)).StatusCode);
    }

    [DatabaseFact]
    public async Task Reusing_a_swapped_refresh_token_ends_the_whole_sign_in()
    {
        await using var api = await AccessApi.StartAsync(database);
        var session = await api.SignInExecutiveAsync();
        var refreshed = await Session.FromAsync(await api.RefreshAsync(session.RefreshCookie));

        var replay = await api.RefreshAsync(session.RefreshCookie);
        var legitimateAfterReplay = await api.RefreshAsync(refreshed.RefreshCookie);
        var tokens = await api.QueryAsync(db => db.RefreshTokens.AsNoTracking().ToListAsync());

        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, legitimateAfterReplay.StatusCode);
        Assert.Contains(tokens, token => token.RevokedReason == RefreshTokenRevocation.ReuseDetected);
        Assert.DoesNotContain(tokens, token => token.RevokedAt == null);
    }

    [DatabaseFact]
    public async Task Rotation_keeps_the_original_end_time()
    {
        await using var api = await AccessApi.StartAsync(database);
        var session = await api.SignInExecutiveAsync();

        await api.RefreshAsync(session.RefreshCookie);
        var tokens = await api.QueryAsync(db => db.RefreshTokens.AsNoTracking().OrderBy(token => token.Id).ToListAsync());

        var family = tokens.GroupBy(token => token.FamilyId).Single(group => group.Count() == 2).ToList();
        Assert.Equal(family[0].ExpiresAt, family[1].ExpiresAt);
        Assert.Equal(RefreshTokenRevocation.Rotated, family[0].RevokedReason);
        Assert.Null(family[1].RevokedAt);
    }

    [DatabaseFact]
    public async Task Two_refreshes_with_the_same_cookie_cannot_both_succeed()
    {
        await using var api = await AccessApi.StartAsync(database);
        var session = await api.SignInExecutiveAsync();

        var responses = await Task.WhenAll(api.RefreshAsync(session.RefreshCookie), api.RefreshAsync(session.RefreshCookie));

        Assert.True(responses.Count(response => response.StatusCode == HttpStatusCode.OK) <= 1);
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.Unauthorized);
    }

    [DatabaseFact]
    public async Task Sign_out_ends_the_sign_in()
    {
        await using var api = await AccessApi.StartAsync(database);
        var session = await api.SignInExecutiveAsync();

        var logout = await api.SendAsync(HttpMethod.Post, "/api/auth/logout", session);
        var refresh = await api.RefreshAsync(session.RefreshCookie);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [DatabaseFact]
    public async Task Changing_the_password_ends_other_sign_ins_but_keeps_this_one()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var otherBrowser = await api.LoginAsync(AccessApi.ExecutiveEmail, AccessApi.ExecutivePassword);

        var changed = await api.SendAsync(HttpMethod.Post, "/api/auth/change-password", executive,
            new { currentPassword = AccessApi.ExecutivePassword, newPassword = "yet-another-password" });
        var thisBrowser = await Session.FromAsync(changed);

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.RefreshAsync(otherBrowser.RefreshCookie)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.RefreshAsync(thisBrowser.RefreshCookie)).StatusCode);
    }

    [DatabaseFact]
    public async Task Disabling_a_user_ends_their_sign_ins()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (user, userSession) = await api.CreateSignedInUserAsync(executive, "engineer");
        var current = await AccessApi.Json(await api.SendAsync(HttpMethod.Get, $"/api/users/{user.GetProperty("id").GetInt32()}", executive));

        await api.SendAsync(HttpMethod.Post, $"/api/users/{user.GetProperty("id").GetInt32()}/disable", executive,
            new { rowVersion = current.GetProperty("rowVersion").GetString() });

        Assert.Equal(HttpStatusCode.Unauthorized, (await api.RefreshAsync(userSession.RefreshCookie)).StatusCode);
    }

    [DatabaseFact]
    public async Task Refresh_picks_up_a_new_role()
    {
        await using var api = await AccessApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (user, userSession) = await api.CreateSignedInUserAsync(executive, "staff");
        var id = user.GetProperty("id").GetInt32();
        var current = await AccessApi.Json(await api.SendAsync(HttpMethod.Get, $"/api/users/{id}", executive));

        var update = await api.SendAsync(HttpMethod.Put, $"/api/users/{id}", executive, new
        {
            email = current.GetProperty("email").GetString(),
            nameAr = current.GetProperty("nameAr").GetString(),
            nameEn = current.GetProperty("nameEn").GetString(),
            role = "accountant",
            rowVersion = current.GetProperty("rowVersion").GetString(),
        });
        var before = await api.SendAsync(HttpMethod.Get, "/api/users", userSession);
        var refreshed = await Session.FromAsync(await api.RefreshAsync(userSession.RefreshCookie));
        var after = await api.SendAsync(HttpMethod.Get, "/api/users", refreshed);

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);
        Assert.Equal("accountant", refreshed.Body.GetProperty("user").GetProperty("role").GetString());
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }
}
