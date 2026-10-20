using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database.Workspace;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Platform.Tests.Database;

/// <summary>
/// A purchase paid in cash from custody, and the custody's funding, become stored notifications through the outbox
/// after the commit: the reviewers (manager, Executives, accountants) hear of the purchase, the holder of the funding,
/// nobody of their own action, and nobody off the project or outside those roles.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class CustodyNotificationTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task A_custody_purchase_is_stored_for_the_reviewers_and_the_funding_for_the_holder_only()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var team = await api.TeamAsync("BDR-NTF-001");
        var (_, otherEngineer) = await api.CreateSignedInUserAsync(team.Executive, "engineer");
        var concrete = await api.SendAsync(HttpMethod.Post, $"/api/projects/{team.ProjectId}/items", team.Manager,
            new { nameAr = "بند الخرسانة", nameEn = "Concrete works", category = "Concrete", unit = "M3" });
        await EnsureStatus(concrete, HttpStatusCode.Created);
        var concreteId = (await Json(concrete)).GetProperty("id").GetInt32();

        // Funded by the Executive's decision: the engineer (the holder) hears of it.
        await api.FundAsync(team.Engineer, team.Accountant, team.ProjectId, 1_000m);
        var forHolder = await WaitForAsync(api, team.Engineer, "CustodyChanged");
        Assert.Equal(team.ProjectId, forHolder.GetProperty("projectId").GetInt32());

        // 200 of sand from custody: the manager, the accountant and the Executive hear of it.
        await EnsureStatus(await api.SendAsync(HttpMethod.Post, $"/api/projects/{team.ProjectId}/expenses", team.Engineer,
            FinancialWorkspaceApi.CashPurchaseBody("Sand", 2m, 100m, concreteId)), HttpStatusCode.Created);
        foreach (var reviewer in new[] { team.Manager, team.Accountant, team.Executive })
        {
            var told = await WaitForAsync(api, reviewer, "CostRecorded");
            Assert.DoesNotContain("200", told.GetRawText(), StringComparison.Ordinal);
        }

        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain("CostRecorded", await TypesAsync(api, team.Engineer));
        Assert.Empty(await TypesAsync(api, otherEngineer));
    }

    private static async Task<List<string>> TypesAsync(ProjectsApi api, Session session)
    {
        var response = await api.SendAsync(HttpMethod.Get, "/api/notifications?pageSize=50", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return (await Json(response)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("type").GetString()!).ToList();
    }

    /// <summary>The dispatcher runs in the background after the commit: wait until one of <paramref name="type"/> is stored.</summary>
    private static async Task<JsonElement> WaitForAsync(ProjectsApi api, Session session, string type)
    {
        var timer = Stopwatch.StartNew();
        while (true)
        {
            var response = await api.SendAsync(HttpMethod.Get, "/api/notifications?pageSize=50", session);
            await EnsureStatus(response, HttpStatusCode.OK);
            var match = (await Json(response)).GetProperty("items").EnumerateArray().FirstOrDefault(item => item.GetProperty("type").GetString() == type);
            if (match.ValueKind == JsonValueKind.Object)
            {
                return match;
            }

            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), $"No {type} notification within 30 s.");
            await Task.Delay(250);
        }
    }
}
