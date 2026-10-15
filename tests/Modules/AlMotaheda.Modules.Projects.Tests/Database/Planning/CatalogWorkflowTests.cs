using System.Net;
using AlMotaheda.Modules.Access.Tests.Database;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Core.CoreApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Planning.PlanningApi;

namespace AlMotaheda.Modules.Projects.Tests.Database.Planning;

/// <summary>The project price catalog and its price changes, end to end on SQL Server.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class CatalogWorkflowTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task A_price_changes_only_through_an_executives_approval_and_nothing_is_overwritten()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("BDR-CAT-001");
        var catalog = $"/api/projects/{cast.ProjectId}/catalog-items";

        var created = await ExpectAsync(await api.PostAsync(cast.Manager, catalog, new
        {
            nameAr = "أسمنت بورتلاندي",
            nameEn = "Portland cement",
            category = "CementAggregates",
            unit = "Bag",
            price = 150m,
            effectiveFrom = Date(CompanyToday.AddDays(-5)),
            estimatedQuantity = 1_000m,
        }), HttpStatusCode.Created);
        var item = created.GetProperty("item");
        Assert.Equal(150m, item.Money("currentPrice"));
        Assert.Equal("Active", item.GetProperty("priceStatus").GetString());
        Assert.Equal(150_000m, item.Money("estimatedValue"));

        var duplicate = await api.PostAsync(cast.Manager, catalog, new { nameAr = "أسمنت", nameEn = "Portland cement", category = "CementAggregates", unit = "Bag", price = 1m });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("catalog_item_exists", await CodeOf(duplicate));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.PostAsync(cast.Engineer, catalog, new { nameAr = "رمل", nameEn = "Sand", category = "CementAggregates", unit = "M3", price = 90m })).StatusCode);

        var changes = $"{catalog}/{item.Id()}/price-changes";
        var request = await ExpectAsync(
            await api.PostAsync(cast.Manager, changes, new { price = 160m, effectiveFrom = Date(CompanyToday.AddDays(-1)), reason = "The supplier raised the price" }),
            HttpStatusCode.Created);
        Assert.Equal("Pending", request.Status());
        Assert.Equal(150m, request.Money("previousPrice"));
        Assert.Equal(6.67m, request.Money("changePercent"));

        var secondRequest = await api.PostAsync(cast.Manager, changes, new { price = 170m, effectiveFrom = Date(CompanyToday), reason = "Again" });
        Assert.Equal(HttpStatusCode.Conflict, secondRequest.StatusCode);
        Assert.Equal("price_change_pending", await CodeOf(secondRequest));

        var decide = $"/api/projects/{cast.ProjectId}/price-changes/{request.Id()}";
        Assert.Equal(HttpStatusCode.Forbidden, (await api.PostAsync(cast.Manager, $"{decide}/approve")).StatusCode);
        var approved = await ExpectAsync(await api.PostAsync(cast.Executive, $"{decide}/approve"), HttpStatusCode.OK);
        Assert.Equal("Approved", approved.Status());

        var detail = await api.GetOkAsync(cast.Manager, $"{catalog}/{item.Id()}");
        Assert.Equal(160m, detail.GetProperty("item").Money("currentPrice"));
        Assert.Equal(150m, detail.GetProperty("item").Money("previousPrice"));
        Assert.Equal(2, detail.GetProperty("history").GetArrayLength());   // both prices are kept

        // A rejection needs a note and leaves the price as it was.
        var tooHigh = await ExpectAsync(
            await api.PostAsync(cast.Manager, changes, new { price = 400m, effectiveFrom = Date(CompanyToday), reason = "New supplier" }), HttpStatusCode.Created);
        var rejectPath = $"/api/projects/{cast.ProjectId}/price-changes/{tooHigh.Id()}/reject";
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync(cast.Executive, rejectPath)).StatusCode);
        Assert.Equal("Rejected", (await ExpectAsync(await api.PostAsync(cast.Executive, rejectPath, new { note = "Far above the market" }), HttpStatusCode.OK)).Status());

        // A price approved for a later date is scheduled: today's price does not change yet.
        var later = await ExpectAsync(
            await api.PostAsync(cast.Manager, changes, new { price = 175m, effectiveFrom = Date(CompanyToday.AddDays(30)), reason = "Price list for next month" }),
            HttpStatusCode.Created);
        await ExpectAsync(await api.PostAsync(cast.Executive, $"/api/projects/{cast.ProjectId}/price-changes/{later.Id()}/approve"), HttpStatusCode.OK);
        var scheduled = (await api.GetOkAsync(cast.Manager, $"{catalog}/{item.Id()}")).GetProperty("item");
        Assert.Equal(160m, scheduled.Money("currentPrice"));
        Assert.Equal("Scheduled", scheduled.GetProperty("priceStatus").GetString());
        Assert.Equal(175m, scheduled.GetProperty("scheduledPrice").Money("price"));
    }

    [DatabaseFact]
    public async Task Nobody_decides_their_own_price_change()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("BDR-CAT-002");
        var catalog = $"/api/projects/{cast.ProjectId}/catalog-items";
        var created = await ExpectAsync(await api.PostAsync(cast.Executive, catalog, new
        {
            nameAr = "حديد", nameEn = "Rebar 16mm", category = "Steel", unit = "Ton", price = 38_000m, effectiveFrom = Date(CompanyToday.AddDays(-5)),
        }), HttpStatusCode.Created);

        var request = await ExpectAsync(await api.PostAsync(cast.Executive, $"{catalog}/{created.GetProperty("item").Id()}/price-changes", new
        {
            price = 39_500m, effectiveFrom = Date(CompanyToday.AddDays(1)), reason = "Steel price rise",
        }), HttpStatusCode.Created);
        var own = await api.PostAsync(cast.Executive, $"/api/projects/{cast.ProjectId}/price-changes/{request.Id()}/approve");

        Assert.Equal(HttpStatusCode.Conflict, own.StatusCode);
        Assert.Equal("own_record", await CodeOf(own));
    }
}
