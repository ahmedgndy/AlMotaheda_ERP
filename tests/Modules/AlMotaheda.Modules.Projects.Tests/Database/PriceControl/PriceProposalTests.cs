using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database.CostManagement;
using AlMotaheda.Modules.Projects.Tests.Database.Core;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Projects.Tests.Database.PriceControl;

/// <summary>
/// Section 14 on SQL Server: a reference price is a proposal until the Executive approves it, whoever makes it. A
/// pending proposal changes no calculation; a rejected one stays in the history and the old price keeps applying;
/// an approved one is effective from its date, and the previous range is still there.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class PriceProposalTests(SqlServerFixture database)
{
    private static string Today => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Africa/Cairo")).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    [DatabaseFact]
    public async Task A_proposed_price_never_applies_until_the_executive_approves_it_and_a_rejected_one_leaves_the_old_price_in_force()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var (_, purchasing) = await api.CreateSignedInUserAsync(cast.Executive, "purchasing");

        // The Executive's own item: its first range is approved. The steel reference is 1,300 per ton.
        var steel = await api.CreatePriceItemAsync(cast.Executive, "Steel 12 mm", "Ton", 1_200m, 1_300m);
        Assert.Equal(("WithinRange", 1_300m), await CheckAsync(api, cast.Engineer, steel, 1_300m));

        // The manager proposes 1,400 with a reason: the proposal is saved with who asked and when; 1,300 keeps applying.
        var proposed = await api.SendAsync(HttpMethod.Post, $"/api/price-items/{steel}/ranges", cast.Manager, new { minPrice = 1_300m, maxPrice = 1_400m, reason = "Supplier price list of October" });
        await EnsureStatus(proposed, HttpStatusCode.Created);
        var proposal = await Json(proposed);
        var requestedBy = proposal.GetProperty("requestedBy");
        Assert.Equal(("Pending", cast.Manager.UserId), (proposal.GetProperty("status").GetString(), requestedBy.ValueKind == JsonValueKind.Number ? requestedBy.GetInt32() : requestedBy.GetProperty("id").GetInt32()));
        Assert.Equal(("AboveRange", 1_300m), await CheckAsync(api, cast.Engineer, steel, 1_400m));

        // The queue shows it to the Executive, not to the manager who asked, and not to the accountant (view only on the catalog).
        var inbox = await Json(await api.SendAsync(HttpMethod.Get, "/api/inbox", cast.Executive));
        Assert.Contains(inbox.GetProperty("items").EnumerateArray(), item => item.GetProperty("recordType").GetString() == "PriceRange" && item.GetProperty("recordId").GetInt32() == proposal.Id() && item.GetProperty("projectId").ValueKind == JsonValueKind.Null);
        Assert.DoesNotContain((await Json(await api.SendAsync(HttpMethod.Get, "/api/inbox", cast.Manager))).GetProperty("items").EnumerateArray(), item => item.GetProperty("recordType").GetString() == "PriceRange");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/price-ranges/{proposal.Id()}/approve", cast.Accountant, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/price-ranges/{proposal.Id()}/approve", cast.Manager, new { })).StatusCode);

        // Rejected: the decision, who and why are kept; 1,300 is still the price; the rejected row stays in the item's history.
        var rejected = await api.SendAsync(HttpMethod.Post, $"/api/price-ranges/{proposal.Id()}/reject", cast.Executive, new { note = "Negotiate first" });
        await EnsureStatus(rejected, HttpStatusCode.OK);
        Assert.Equal(("Rejected", "Negotiate first"), ((await Json(rejected)).GetProperty("status").GetString(), (await Json(rejected)).GetProperty("decisionNote").GetString()));
        Assert.Equal(("AboveRange", 1_300m), await CheckAsync(api, cast.Engineer, steel, 1_400m));
        var history = (await Json(await api.SendAsync(HttpMethod.Get, $"/api/price-items/{steel}", cast.Executive))).GetProperty("ranges").EnumerateArray().ToList();
        Assert.Equal(2, history.Count);
        Assert.Contains(history, range => range.GetProperty("status").GetString() == "Rejected" && range.GetProperty("maxPrice").GetDecimal() == 1_400m);
        Assert.Contains(history, range => range.GetProperty("status").GetString() == "Approved" && range.GetProperty("maxPrice").GetDecimal() == 1_300m);

        // Proposed again and approved: 1,400 applies from today; the 1,300 row is still there for the history.
        var second = await Json(await api.SendAsync(HttpMethod.Post, $"/api/price-items/{steel}/ranges", cast.Manager, new { minPrice = 1_300m, maxPrice = 1_400m, reason = "Negotiated" }));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/price-ranges/{second.Id()}/approve", cast.Manager, new { })).StatusCode);
        await EnsureStatus(await api.SendAsync(HttpMethod.Post, $"/api/price-ranges/{second.Id()}/approve", cast.Executive, new { }), HttpStatusCode.OK);
        Assert.Equal(("WithinRange", 1_400m), await CheckAsync(api, cast.Engineer, steel, 1_400m));
        Assert.Equal(3, (await Json(await api.SendAsync(HttpMethod.Get, $"/api/price-items/{steel}", cast.Executive))).GetProperty("ranges").GetArrayLength());

        // A new item made by anyone but a database-checked Executive starts with a pending range: no reference until approved.
        var created = await api.SendAsync(HttpMethod.Post, "/api/price-items", cast.Manager, CostManagementApi.PriceItemBody("Cement 52.5", "Ton", 1_500m, 1_700m));
        await EnsureStatus(created, HttpStatusCode.Created);
        var cement = (await Json(created)).GetProperty("item").Id();
        var pending = Assert.Single((await Json(created)).GetProperty("ranges").EnumerateArray());
        Assert.Equal("Pending", pending.GetProperty("status").GetString());
        Assert.Equal("NoReference", (await CheckAsync(api, cast.Engineer, cement, 1_600m)).Result);
        await EnsureStatus(await api.SendAsync(HttpMethod.Post, $"/api/price-ranges/{pending.Id()}/approve", cast.Executive, new { }), HttpStatusCode.OK);
        Assert.Equal(("WithinRange", 1_700m), await CheckAsync(api, cast.Engineer, cement, 1_600m));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, "/api/price-items", purchasing, CostManagementApi.PriceItemBody("Sand", "Ton", 100m, 120m))).StatusCode);
    }

    private static async Task<(string Result, decimal MaxPrice)> CheckAsync(ProjectsApi api, Session session, int priceItemId, decimal unitPrice)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/price-items/{priceItemId}/check?unitPrice={unitPrice}&quantity=1&date={Today}", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        var body = await Json(response);
        return (body.GetProperty("result").GetString()!, body.TryGetProperty("maxPrice", out var max) && max.ValueKind == JsonValueKind.Number ? max.GetDecimal() : 0m);
    }
}
