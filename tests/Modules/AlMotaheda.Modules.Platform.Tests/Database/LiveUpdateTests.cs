using System.Collections.Concurrent;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Platform.Tests.Api;
using AlMotaheda.Modules.Projects.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database.Core;
using Microsoft.AspNetCore.SignalR.Client;
using static AlMotaheda.Modules.Projects.Tests.Database.Core.CoreApi;

namespace AlMotaheda.Modules.Platform.Tests.Database;

/// <summary>
/// Live updates on SQL Server: after the commit, the outbox sends each person their notification and each group the
/// changes it may see, and an engineer never hears about a project's money.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class LiveUpdateTests(SqlServerFixture database)
{
    private sealed class Listener : IAsyncDisposable
    {
        public Listener(HubConnection connection)
        {
            Connection = connection;
            connection.On<JsonElement>("changed", signal => Changed.Add(signal));
            connection.On<JsonElement>("notification", signal => Notifications.Add(signal));
        }

        public HubConnection Connection { get; }

        public ConcurrentBag<JsonElement> Changed { get; } = [];

        public ConcurrentBag<JsonElement> Notifications { get; } = [];

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    [DatabaseFact]
    public async Task Each_person_hears_what_they_may_see_after_the_commit()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("LIVE-001");
        var cemex = await api.CreateSupplierAsync(cast.Executive, "Cemex");

        await using var engineer = new Listener(LiveHubTests.Connect(api.Access.App, cast.Engineer.AccessToken));
        await using var accountant = new Listener(LiveHubTests.Connect(api.Access.App, cast.Accountant.AccessToken));
        await engineer.Connection.StartAsync();
        await accountant.Connection.StartAsync();

        // The engineer is on the project but has no projects See money and no finance.
        Assert.True(await engineer.Connection.InvokeAsync<bool>("Join", $"project:{cast.ProjectId}"));
        Assert.False(await engineer.Connection.InvokeAsync<bool>("Join", $"project:{cast.ProjectId}:money"));
        Assert.False(await engineer.Connection.InvokeAsync<bool>("Join", "company:finance"));
        Assert.True(await accountant.Connection.InvokeAsync<bool>("Join", $"project:{cast.ProjectId}:money"));
        Assert.True(await accountant.Connection.InvokeAsync<bool>("Join", "company:finance"));

        // A project the engineer isn't on can't be joined.
        var other = await api.CreateProjectAsync(cast.Executive, "LIVE-002", managerUserId: (await api.CreateSignedInUserAsync(cast.Executive, "manager")).Id);
        Assert.False(await engineer.Connection.InvokeAsync<bool>("Join", $"project:{other.Id()}"));

        var recorded = await api.RecordAsync(cast.Engineer, cast.ProjectId, SupplyBody(cemex, 10m, 100m, "Credit"));
        await api.ApproveAsync(cast.Accountant, cast.ProjectId, recorded);

        // The engineer: their record was approved (a notification) and the project's list changed.
        var notice = await WaitAsync(() => engineer.Notifications.FirstOrDefault(signal => signal.GetProperty("type").GetString() == "RecordApproved"));
        Assert.Equal(("ProjectExpense", recorded.Id().ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (notice.GetProperty("entityType").GetString(), notice.GetProperty("entityKey").GetString()));
        await WaitAsync(() => engineer.Changed.FirstOrDefault(signal => signal.GetProperty("scope").GetString() == $"project:{cast.ProjectId}"));

        // The accountant: the project's money and the company's finance figures changed.
        var money = await WaitAsync(() => accountant.Changed.FirstOrDefault(signal => signal.GetProperty("scope").GetString() == $"project:{cast.ProjectId}:money"));
        Assert.Contains("ProjectOverview", money.GetProperty("aggregates").EnumerateArray().Select(aggregate => aggregate.GetString()));
        await WaitAsync(() => accountant.Changed.FirstOrDefault(signal => signal.GetProperty("scope").GetString() == "company:finance"));

        // Nothing about money reached the engineer, and no signal carries an amount.
        Assert.DoesNotContain(engineer.Changed, signal => signal.GetProperty("scope").GetString()!.EndsWith(":money", StringComparison.Ordinal)
                                                          || signal.GetProperty("scope").GetString()!.StartsWith("company:", StringComparison.Ordinal));
        Assert.All(accountant.Changed.Concat(engineer.Changed), signal =>
            Assert.DoesNotContain(signal.EnumerateObject(), property => property.Name is "amount" or "paid" or "total"));
    }

    private static async Task<JsonElement> WaitAsync(Func<JsonElement> probe)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            var found = probe();
            if (found.ValueKind != JsonValueKind.Undefined)
            {
                return found;
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("Waited 30 seconds for a live signal.");
    }
}
