using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Platform.Push;
using AlMotaheda.Modules.Projects.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database.Core;
using Microsoft.Extensions.DependencyInjection;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Core.CoreApi;

namespace AlMotaheda.Modules.Platform.Tests.Database;

/// <summary>
/// Phones register for push; a stored notification is pushed to its recipient's phones only, after the commit, and a
/// token Firebase calls dead stops being used. Firebase itself is replaced by a recorder.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class DeviceAndPushTests(SqlServerFixture database)
{
    private const string DeadToken = "token-of-an-uninstalled-app";

    private sealed class RecordingSender : IPushSender
    {
        public ConcurrentQueue<(string Token, PushMessage Message)> Sent { get; } = new();

        public bool IsConfigured => true;

        public Task<PushResult> SendAsync(string deviceToken, PushMessage message, CancellationToken cancellationToken)
        {
            Sent.Enqueue((deviceToken, message));
            return Task.FromResult(deviceToken == DeadToken ? PushResult.TokenGone : PushResult.Sent);
        }
    }

    private static Task<HttpResponseMessage> RegisterAsync(ProjectsApi api, Session session, string token, string platform = "Android") =>
        api.SendAsync(HttpMethod.Post, "/api/devices", session, new { platform, token });

    [DatabaseFact]
    public async Task A_returned_record_is_pushed_to_its_recorders_phones_only_and_a_dead_token_is_dropped()
    {
        var sender = new RecordingSender();
        await using var api = await ProjectsApi.StartAsync(database, builder => builder.Services.AddSingleton<IPushSender>(sender));
        var cast = await api.CastAsync();
        var cemex = await api.CreateSupplierAsync(cast.Executive, "Cemex");

        await EnsureStatus(await RegisterAsync(api, cast.Engineer, "engineer-phone"), HttpStatusCode.NoContent);
        await EnsureStatus(await RegisterAsync(api, cast.Engineer, DeadToken), HttpStatusCode.NoContent);
        await EnsureStatus(await RegisterAsync(api, cast.Accountant, "accountant-phone"), HttpStatusCode.NoContent);

        var recorded = await api.RecordAsync(cast.Engineer, cast.ProjectId, SupplyBody(cemex, 10m, 100m, "Credit"));
        await EnsureStatus(await api.ReviewAsync(cast.Accountant, cast.ProjectId, recorded, "return", "Attach the delivery note"), HttpStatusCode.OK);

        await WaitUntilAsync(() => sender.Sent.Count >= 2);
        var pushed = sender.Sent.ToList();
        // The recorder's two phones; the accountant did the action and is told nothing.
        Assert.Equal(["engineer-phone", DeadToken], pushed.Select(push => push.Token).Order(StringComparer.Ordinal));
        var message = pushed[0].Message;
        Assert.Equal("RecordReturned", message.Type);
        Assert.Equal("ProjectExpense", message.EntityType);
        Assert.Equal(recorded.Id().ToString(), message.EntityKey);
        Assert.Equal(cast.ProjectId, message.ProjectId);

        // The dead token is disabled: the next notification only reaches the live phone.
        var fixedRecord = await Json(await api.SendAsync(HttpMethod.Put, $"/api/projects/{cast.ProjectId}/expenses/{recorded.Id()}", cast.Engineer,
            EditBody(await api.GetExpenseAsync(cast.Engineer, cast.ProjectId, recorded.Id()), new() { ["quantity"] = 11m })));
        await EnsureStatus(await api.ReviewAsync(cast.Accountant, cast.ProjectId, fixedRecord, "approve"), HttpStatusCode.OK);
        await WaitUntilAsync(() => sender.Sent.Count >= 3);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(["engineer-phone"], sender.Sent.Skip(2).Select(push => push.Token));
        Assert.Equal("RecordApproved", sender.Sent.Last().Message.Type);
    }

    [DatabaseFact]
    public async Task A_phone_belongs_to_whoever_registered_it_last_and_only_its_owner_can_unregister_it()
    {
        var sender = new RecordingSender();
        await using var api = await ProjectsApi.StartAsync(database, builder => builder.Services.AddSingleton<IPushSender>(sender));
        var cast = await api.CastAsync();
        var cemex = await api.CreateSupplierAsync(cast.Executive, "Cemex");

        // The engineer's phone is handed to the manager, who signs in on it.
        await EnsureStatus(await RegisterAsync(api, cast.Engineer, "shared-phone"), HttpStatusCode.NoContent);
        await EnsureStatus(await RegisterAsync(api, cast.Manager, "shared-phone"), HttpStatusCode.NoContent);
        // The engineer can't take it off the manager.
        await EnsureStatus(await api.SendAsync(HttpMethod.Post, "/api/devices/unregister", cast.Engineer, new { token = "shared-phone" }), HttpStatusCode.NoContent);

        // A notification for the engineer no longer reaches that phone.
        var recorded = await api.RecordAsync(cast.Engineer, cast.ProjectId, SupplyBody(cemex, 1m, 100m, "Credit"));
        await EnsureStatus(await api.ReviewAsync(cast.Accountant, cast.ProjectId, recorded, "return", "Wrong supplier"), HttpStatusCode.OK);
        await Task.Delay(TimeSpan.FromSeconds(3));
        // The manager, who now holds the phone, is told about the new cost on the project (CostRecorded); the
        // engineer's own notification (RecordReturned) never reaches it.
        Assert.DoesNotContain(sender.Sent, push => push.Message.Type == "RecordReturned");
        Assert.All(sender.Sent, push => Assert.Equal("CostRecorded", push.Message.Type));

        // The manager unregisters it on sign-out; registering again works.
        await EnsureStatus(await api.SendAsync(HttpMethod.Post, "/api/devices/unregister", cast.Manager, new { token = "shared-phone" }), HttpStatusCode.NoContent);
        await EnsureStatus(await RegisterAsync(api, cast.Manager, "shared-phone"), HttpStatusCode.NoContent);
    }

    [DatabaseFact]
    public async Task Registration_is_checked_and_needs_a_signed_in_user()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();

        var bad = await RegisterAsync(api, cast.Engineer, new string('x', 401), platform: "Windows");
        await EnsureStatus(bad, HttpStatusCode.BadRequest);
        var fields = (await Json(bad)).GetProperty("errors").EnumerateObject().Select(field => field.Name).ToList();
        Assert.Contains("platform", fields);
        Assert.Contains("token", fields);
        await EnsureStatus(await api.SendAsync(HttpMethod.Post, "/api/devices/unregister", cast.Engineer, new { token = "" }), HttpStatusCode.BadRequest);

        var anonymous = await api.Access.App.Client.PostAsJsonAsync("/api/devices", new { platform = "Android", token = "t" });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), "The push did not arrive within 30 s.");
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }
    }
}
