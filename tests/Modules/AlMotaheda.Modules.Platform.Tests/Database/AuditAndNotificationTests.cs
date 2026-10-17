using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database.Core;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Core.CoreApi;

namespace AlMotaheda.Modules.Platform.Tests.Database;

/// <summary>
/// A change in another module (an expense in Projects) writes its audit entries and outbox message in the same
/// transaction, and the outbox becomes a notification for the right person only.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class AuditAndNotificationTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task Returning_an_expense_is_audited_with_old_and_new_values_and_tells_only_its_recorder()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var cemex = await api.CreateSupplierAsync(cast.Executive, "Cemex");

        var recorded = await api.RecordAsync(cast.Engineer, cast.ProjectId, SupplyBody(cemex, 10m, 100m, "Credit"));
        var returned = await api.ReviewAsync(cast.Accountant, cast.ProjectId, recorded, "return", "Attach the delivery note");
        await EnsureStatus(returned, HttpStatusCode.OK);

        // Audit: who did what, with the old and new status, in order.
        var entries = await AuditAsync(api, cast.Executive, $"entityType=ProjectExpense&entityKey={recorded.Id()}");
        Assert.Equal(["Modified", "Added"], entries.Select(entry => entry.GetProperty("action").GetString()));
        var review = entries[0];
        Assert.Equal(cast.Accountant.UserId, review.GetProperty("userId").GetInt32());
        Assert.Equal(cast.ProjectId, review.GetProperty("projectId").GetInt32());
        Assert.Equal("projects", review.GetProperty("module").GetString());
        var status = review.GetProperty("changes").GetProperty("Status");
        Assert.Equal(["Recorded", "Returned"], status.EnumerateArray().Select(value => value.GetString()));
        Assert.Equal("Attach the delivery note", review.GetProperty("changes").GetProperty("ReviewNote")[1].GetString());
        Assert.Equal(cast.Engineer.UserId, entries[1].GetProperty("userId").GetInt32());

        // Notification: the recorder hears about it; the reviewer, who did it, doesn't.
        var notification = await WaitForSingleNotificationAsync(api, cast.Engineer);
        Assert.Equal("RecordReturned", notification.GetProperty("type").GetString());
        Assert.Equal("ProjectExpense", notification.GetProperty("entityType").GetString());
        Assert.Equal(recorded.Id().ToString(), notification.GetProperty("entityKey").GetString());
        Assert.Equal(cast.ProjectId, notification.GetProperty("projectId").GetInt32());
        Assert.Equal("Your expense or supply was returned for changes", notification.GetProperty("titleEn").GetString());
        Assert.Equal("تمت إعادة المصروف أو التوريد للتعديل", notification.GetProperty("titleAr").GetString());

        // The reviewer heard only that the record arrived for review (docs/cost-flow §2.8), never about their own return.
        var forReviewer = await WaitForSingleNotificationAsync(api, cast.Accountant);
        Assert.Equal("ReviewRequested", forReviewer.GetProperty("type").GetString());
        Assert.Equal("A new expense or supply waits for your review", forReviewer.GetProperty("titleEn").GetString());

        // Read: once, by its recipient only. Someone else's notification is 404, like one that doesn't exist.
        var id = notification.GetProperty("id").GetInt64();
        await EnsureStatus(await api.SendAsync(HttpMethod.Post, $"/api/notifications/{id}/read", cast.Accountant), HttpStatusCode.NotFound);
        Assert.Equal(1, await UnreadCountAsync(api, cast.Engineer));
        await EnsureStatus(await api.SendAsync(HttpMethod.Post, $"/api/notifications/{id}/read", cast.Engineer), HttpStatusCode.NoContent);
        await EnsureStatus(await api.SendAsync(HttpMethod.Post, $"/api/notifications/{id}/read", cast.Engineer), HttpStatusCode.NoContent);
        Assert.Equal(0, await UnreadCountAsync(api, cast.Engineer));

        // The audit trail is the Executive's.
        await EnsureStatus(await api.SendAsync(HttpMethod.Get, "/api/audit-entries", cast.Accountant), HttpStatusCode.Forbidden);
    }

    [DatabaseFact]
    public async Task A_save_that_fails_leaves_no_audit_and_secrets_never_reach_the_trail()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        await api.CreateSupplierAsync(executive, "Cemex");

        // The duplicate is refused by the unique index, after EF tried to save it: its transaction rolls back.
        var duplicate = await api.SendAsync(HttpMethod.Post, "/api/suppliers", executive, new { nameAr = "مورد آخر", nameEn = "Cemex", kind = "Materials" });
        await EnsureStatus(duplicate, HttpStatusCode.Conflict);
        Assert.Single(await AuditAsync(api, executive, "entityType=Supplier"));

        // Users are audited (the bootstrap Executive, the password change), but never their password hash, and the
        // sign-in bookkeeping and refresh tokens are not audited at all.
        var users = await AuditAsync(api, executive, "entityType=User");
        Assert.NotEmpty(users);
        Assert.All(users, entry => Assert.False(entry.GetProperty("changes").TryGetProperty("PasswordHash", out _)));
        Assert.Empty(await AuditAsync(api, executive, "entityType=RefreshToken"));
        Assert.Empty(await AuditAsync(api, executive, "entityType=UserSignInState"));
    }

    private static async Task<List<JsonElement>> AuditAsync(ProjectsApi api, Session executive, string query)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/audit-entries?{query}&pageSize=100", executive);
        await EnsureStatus(response, HttpStatusCode.OK);
        return (await Json(response)).GetProperty("items").EnumerateArray().ToList();
    }

    private static async Task<int> UnreadCountAsync(ProjectsApi api, Session session)
    {
        var response = await api.SendAsync(HttpMethod.Get, "/api/notifications/unread-count", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return (await Json(response)).GetProperty("count").GetInt32();
    }

    /// <summary>The dispatcher runs in the background after the commit, so the notification appears within seconds.</summary>
    private static async Task<JsonElement> WaitForSingleNotificationAsync(ProjectsApi api, Session session)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var response = await api.SendAsync(HttpMethod.Get, "/api/notifications", session);
            await EnsureStatus(response, HttpStatusCode.OK);
            var body = await Json(response);
            var items = body.GetProperty("items").EnumerateArray().ToList();
            // Wait on the count, not the items: the list counts first and pages second, so a notification stored
            // in between shows in the items but not yet in the counts.
            if (body.GetProperty("totalCount").GetInt32() > 0 || timer.Elapsed > TimeSpan.FromSeconds(30))
            {
                Assert.Equal(1, body.GetProperty("unreadCount").GetInt32());
                return Assert.Single(items);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
    }
}
