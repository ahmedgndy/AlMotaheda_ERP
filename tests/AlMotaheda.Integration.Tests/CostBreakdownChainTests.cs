using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database;
using AlMotaheda.Modules.Workforce.Tests.Database;

namespace AlMotaheda.Integration.Tests;

/// <summary>
/// The work-item cost breakdown across modules on SQL Server: a daily team of internal workers (Workforce), a
/// subcontract line (Subcontractors) and the main item with its sub-item (Projects), with each record counted once.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class CostBreakdownChainTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task Ten_workers_at_500_and_a_subcontract_line_land_once_on_concrete_works_and_a_repeated_sheet_charges_nothing()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await WorkforceApi.CastAsync(api, "INT-COST-01");
        var concrete = Id(await Send(api, cast.Manager, HttpMethod.Post, $"/api/projects/{cast.ProjectId}/items",
            new { nameAr = "الأعمال الخرسانية", nameEn = "Concrete works", category = "Concrete", unit = "M3" }, HttpStatusCode.Created));
        var subWork = Id(await Send(api, cast.Manager, HttpMethod.Post, $"/api/projects/{cast.ProjectId}/items",
            new { nameAr = "مقاول الخرسانة", nameEn = "Concrete subcontractor", category = "Concrete", unit = "M3", parentItemId = concrete }, HttpStatusCode.Created));

        // The daily team: 10 workers at EGP 500 a day, recorded on the main item (the work is not more specific).
        var workers = new List<int>();
        for (var index = 1; index <= 10; index++)
        {
            workers.Add(await WorkforceApi.HireAsync(api, cast.Executive, cast.ProjectId, WorkforceApi.Mason, $"Mason {index}", dailyRate: 500m));
        }

        var day = WorkforceApi.Today(api).AddDays(-1);
        var lines = workers.Select(worker => WorkforceApi.Line(worker, 1m, [(concrete, 1m)])).ToArray();
        await WorkforceApi.RecordApprovedSheetAsync(api, cast, cast.ProjectId, day, lines);

        // The same team sent again (a retry, a second tap): refused, nothing more is charged.
        var repeated = await WorkforceApi.RecordSheetAsync(api, cast.Engineer, cast.ProjectId, day, submit: true, lines);
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        Assert.Contains(await AccessApi.CodeOf(repeated), new[] { "sheet_exists", "worker_already_recorded" });

        // A subcontract: 100 m³ at 1,000 on the sub-item, 40 m³ measured and approved.
        var company = Id(await Send(api, cast.Executive, HttpMethod.Post, "/api/subcontractors",
            new { nameAr = "شركة البناء", nameEn = "Build Co", specialty = "Civil" }, HttpStatusCode.Created));
        var contract = (await Send(api, cast.Manager, HttpMethod.Post, $"/api/projects/{cast.ProjectId}/subcontracts", new
        {
            subcontractorId = company, model = "UnitRate", value = 100_000m, retentionPercent = 0m,
            startDate = "2026-01-01", endDate = "2026-12-31", status = "Active", scope = "Concrete",
        }, HttpStatusCode.Created)).GetProperty("contract").GetProperty("id").GetInt32();
        var line = Id(await Send(api, cast.Manager, HttpMethod.Post, $"/api/projects/{cast.ProjectId}/subcontracts/{contract}/items",
            new { name = "Slab concrete", workType = "Concrete", unit = "M3", quantity = 100m, unitPrice = 1_000m, projectItemId = subWork }, HttpStatusCode.Created));
        var measured = await Send(api, cast.Manager, HttpMethod.Post, $"/api/projects/{cast.ProjectId}/subcontracts/{contract}/measurements",
            new { contractItemId = line, measuredOn = WorkforceApi.Iso(day), quantity = 40m }, HttpStatusCode.Created);
        await Send(api, cast.Executive, HttpMethod.Post, $"/api/projects/{cast.ProjectId}/subcontract-measurements/{Id(measured)}/approve",
            new { rowVersion = measured.GetProperty("rowVersion").GetString() });

        var breakdown = await Send(api, cast.Accountant, HttpMethod.Get, $"/api/projects/{cast.ProjectId}/items/{concrete}/cost-breakdown?pageSize=100", null);

        // Labor 10 × 500 = 5,000 on the main item; subcontract 40,000 on the sub-item; 45,000 in all, never more.
        var totals = breakdown.GetProperty("totals");
        Assert.Equal((5_000m, 40_000m, 45_000m), (Money(totals, "laborCost"), Money(totals, "subcontractorCost"), Money(totals, "totalCost")));
        Assert.Equal(45_000m, Money(breakdown.GetProperty("filtered"), "amount"));
        Assert.Equal(11, breakdown.GetProperty("totalCount").GetInt32());
        var team = breakdown.GetProperty("workers").EnumerateArray().ToList();
        Assert.Equal(10, team.Count);
        Assert.All(team, worker => Assert.Equal(500m, Money(worker, "cost")));

        // The contract value is a commitment: the 60,000 not executed yet is shown apart and is in no total.
        Assert.Equal(60_000m, Money(breakdown.GetProperty("commitments"), "subcontractRemaining"));
        var sub = Assert.Single(breakdown.GetProperty("subcontractors").EnumerateArray());
        Assert.Equal((40_000m, 0m, 40_000m, 60_000m), (Money(sub, "recognized"), Money(sub, "paid"), Money(sub, "outstanding"), Money(sub, "committed")));

        // The items table agrees: the main item's own labor, its roll-up with the sub-item, and project totals counted once.
        var items = await Send(api, cast.Accountant, HttpMethod.Get, $"/api/projects/{cast.ProjectId}/items", null);
        var main = items.GetProperty("items").EnumerateArray().Single(row => Id(row) == concrete);
        Assert.Equal((5_000m, 45_000m), (Money(main, "totalCost"), Money(main.GetProperty("withSubItems"), "totalCost")));
        Assert.Equal(45_000m, Money(items.GetProperty("totals"), "totalCost"));

        // The labor filter keeps only the team; the sub-item filter only the subcontract.
        var laborOnly = await Send(api, cast.Accountant, HttpMethod.Get, $"/api/projects/{cast.ProjectId}/items/{concrete}/cost-breakdown?type=Labor", null);
        Assert.Equal(5_000m, Money(laborOnly.GetProperty("filtered"), "amount"));
        var subOnly = await Send(api, cast.Accountant, HttpMethod.Get, $"/api/projects/{cast.ProjectId}/items/{concrete}/cost-breakdown?subItemId={subWork}", null);
        Assert.Equal(40_000m, Money(subOnly.GetProperty("filtered"), "amount"));
    }

    private static int Id(JsonElement element) => element.GetProperty("id").GetInt32();

    private static decimal Money(JsonElement element, string name) => element.GetProperty(name).GetDecimal();

    private static async Task<JsonElement> Send(ProjectsApi api, Session session, HttpMethod method, string path, object? body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await api.SendAsync(method, path, session, body);
        await AccessApi.EnsureStatus(response, expected);
        return await AccessApi.Json(response);
    }
}
