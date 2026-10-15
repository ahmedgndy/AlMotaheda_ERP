using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Core.CoreApi;

namespace AlMotaheda.Modules.Projects.Tests.Database.Core;

/// <summary>The work inbox (<c>GET /api/inbox</c>) follows a record from recording to approval, for each person.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class WorkInboxTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task A_recorded_expense_waits_for_the_reviewer_a_returned_one_for_its_recorder_and_an_approved_one_for_nobody()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var outsider = await api.CreateProjectAsync(cast.Executive, "OTHER-1", cast.Executive.UserId);
        var cemex = await api.CreateSupplierAsync(cast.Executive, "Cemex");

        var recorded = await api.RecordAsync(cast.Engineer, cast.ProjectId, SupplyBody(cemex, 10m, 100m, "Credit"));

        // The accountant (projects Approve and See money) sees it with its amount; the recorder and the manager
        // (no Approve by default) don't.
        var accountant = await InboxAsync(api, cast.Accountant);
        var item = Assert.Single(Items(accountant), entry => entry.GetProperty("recordId").GetInt32() == recorded.Id());
        Assert.Equal("Review", item.GetProperty("reason").GetString());
        Assert.Equal("Expense", item.GetProperty("recordType").GetString());
        Assert.Equal(cast.ProjectId, item.GetProperty("projectId").GetInt32());
        Assert.Equal(1_000m, item.GetProperty("amount").GetDecimal());
        Assert.Equal(cast.Engineer.UserId, item.GetProperty("from").GetProperty("userId").GetInt32());
        Assert.Empty(Items(await InboxAsync(api, cast.Engineer)));
        Assert.Empty(Items(await InboxAsync(api, cast.Manager)));

        // Returned: it leaves the reviewer's inbox and waits for the recorder, with the note.
        var returned = await api.ReviewAsync(cast.Accountant, cast.ProjectId, recorded, "return", "Attach the delivery note");
        await EnsureStatus(returned, HttpStatusCode.OK);
        Assert.DoesNotContain(Items(await InboxAsync(api, cast.Accountant)), entry => entry.GetProperty("recordId").GetInt32() == recorded.Id());
        var mine = Assert.Single(Items(await InboxAsync(api, cast.Engineer)));
        Assert.Equal("Returned", mine.GetProperty("reason").GetString());
        Assert.Equal("Attach the delivery note", mine.GetProperty("note").GetString());
        Assert.Equal(JsonValueKind.Null, mine.GetProperty("amount").ValueKind);

        // Fixed and resubmitted: back with the reviewer; approved: gone for everyone.
        var fixedOne = await api.SendAsync(HttpMethod.Put, $"/api/projects/{cast.ProjectId}/expenses/{recorded.Id()}", cast.Engineer,
            EditBody(await Json(returned), new() { ["deliveryNoteNo"] = "DN-1" }));
        await EnsureStatus(fixedOne, HttpStatusCode.OK);
        Assert.Empty(Items(await InboxAsync(api, cast.Engineer)));
        Assert.Single(Items(await InboxAsync(api, cast.Accountant)));
        await api.ApproveAsync(cast.Accountant, cast.ProjectId, await Json(fixedOne));
        Assert.Empty(Items(await InboxAsync(api, cast.Accountant)));

        // A record on a project the caller can't see never shows.
        await api.RecordAsync(cast.Executive, outsider.Id(), SupplyBody(cemex, 1m, 1m, "Credit"));
        Assert.Empty(Items(await InboxAsync(api, cast.Engineer)));
        Assert.Empty(Items(await InboxAsync(api, cast.Manager)));
    }

    [DatabaseFact]
    public async Task A_report_request_waits_for_its_recipient_then_for_the_requester()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();

        var created = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/report-requests", cast.Manager, new
        {
            isProactive = false,
            recipientUserId = cast.Engineer.UserId,
            type = "DailyReport",
            priority = "High",
            title = "Weekly progress",
        });
        await EnsureStatus(created, HttpStatusCode.Created);
        var request = await Json(created);

        var waiting = Assert.Single(Items(await InboxAsync(api, cast.Engineer)));
        Assert.Equal("Requested", waiting.GetProperty("reason").GetString());
        Assert.Equal("ReportRequest", waiting.GetProperty("recordType").GetString());
        Assert.Empty(Items(await InboxAsync(api, cast.Manager)));

        var submitted = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/report-requests/{request.Id()}/submit", cast.Engineer,
            new { responseText = "Done", rowVersion = request.GetProperty("rowVersion").GetString() });
        await EnsureStatus(submitted, HttpStatusCode.OK);

        Assert.Empty(Items(await InboxAsync(api, cast.Engineer)));
        var toComplete = Assert.Single(Items(await InboxAsync(api, cast.Manager)));
        Assert.Equal("Review", toComplete.GetProperty("reason").GetString());
    }

    [DatabaseFact]
    public async Task The_inbox_needs_a_signed_in_user()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var response = await api.Access.App.Client.GetAsync("/api/inbox");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task<JsonElement> InboxAsync(ProjectsApi api, Session session)
    {
        var response = await api.SendAsync(HttpMethod.Get, "/api/inbox", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    private static List<JsonElement> Items(JsonElement inbox) => inbox.GetProperty("items").EnumerateArray().ToList();
}
