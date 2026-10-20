using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Subcontractors.Tests.Database;

/// <summary>
/// One subcontractor company engaged per main work item of several projects, on SQL Server: the engagements, the
/// value changes (variations) and the company's account across projects, which must be the sum of its engagements.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class EngagementTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task A_company_owed_100k_and_60k_on_one_project_and_40k_on_another_is_owed_200k_then_180k_after_a_20k_payment()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("BDR-ENG-001");
        var delta = await api.CreateCompanyAsync(cast.Executive, "Delta Concrete");
        var concrete = await api.CreateProjectItemAsync(cast.Executive, cast.ProjectId, "Concrete works");
        var masonry = await api.CreateProjectItemAsync(cast.Executive, cast.ProjectId, "Masonry works");

        // One engagement per main work item: Concrete works and Masonry works on the same project.
        var onConcrete = (await api.CreateContractAsync(cast.Manager, cast.ProjectId,
            SubcontractsApi.ContractBody(delta, "UnitRate", 300_000m, new() { ["projectItemId"] = concrete.Id() }))).ContractId();
        var onMasonry = (await api.CreateContractAsync(cast.Manager, cast.ProjectId,
            SubcontractsApi.ContractBody(delta, "UnitRate", 100_000m, new() { ["projectItemId"] = masonry.Id() }))).ContractId();
        var twice = await api.PostContractAsync(cast.Manager, cast.ProjectId,
            SubcontractsApi.ContractBody(delta, "UnitRate", 10_000m, new() { ["projectItemId"] = concrete.Id() }));
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);

        // A second project, with its own manager.
        var (otherManagerId, otherManager) = await api.CreateSignedInUserAsync(cast.Executive, "manager");
        var second = (await api.CreateProjectAsync(cast.Executive, "BDR-ENG-002", otherManagerId)).GetProperty("id").GetInt32();
        var secondConcrete = await api.CreateProjectItemAsync(cast.Executive, second, "Concrete works");
        var onSecond = (await api.CreateContractAsync(otherManager, second,
            SubcontractsApi.ContractBody(delta, "UnitRate", 80_000m, new() { ["projectItemId"] = secondConcrete.Id() }))).ContractId();

        // Approved work: 100k and 60k on the first project, 40k on the second.
        await ApprovedWorkAsync(api, cast.Manager, cast.Executive, cast.ProjectId, onConcrete, 100_000m);
        await ApprovedWorkAsync(api, cast.Manager, cast.Executive, cast.ProjectId, onMasonry, 60_000m);
        await ApprovedWorkAsync(api, otherManager, cast.Executive, second, onSecond, 40_000m);

        var before = await AccountAsync(api, cast.Accountant, delta);
        Assert.Equal(200_000m, before.GetProperty("totals").Money("outstandingWork"));

        // 20k paid on the first project's Concrete works engagement, through Finance.
        await api.PaidAsync(cast.Accountant, cast.ProjectId, onConcrete, 20_000m);

        var account = await AccountAsync(api, cast.Accountant, delta);
        var totals = account.GetProperty("totals");
        Assert.Equal((200_000m, 20_000m, 180_000m), (totals.Money("executed"), totals.Money("paidTotal"), totals.Money("outstandingWork")));
        var perProject = account.GetProperty("projects").EnumerateArray()
            .ToDictionary(project => project.GetProperty("projectId").GetInt32(), project => project.GetProperty("totals").Money("outstandingWork"));
        Assert.Equal(140_000m, perProject[cast.ProjectId]);
        Assert.Equal(40_000m, perProject[second]);

        // Remaining to execute is a different figure from what is owed: 300k − 100k on Concrete works.
        var concreteEngagement = account.GetProperty("engagements").EnumerateArray().Single(row => row.GetProperty("contractId").GetInt32() == onConcrete);
        Assert.Equal(concrete.Id(), concreteEngagement.GetProperty("projectItemId").GetInt32());
        Assert.Equal((200_000m, 80_000m), (concreteEngagement.GetProperty("account").Money("remainingToExecute"),
            concreteEngagement.GetProperty("account").Money("outstandingWork")));

        // The ledger runs oldest first; its last balance (newest line first on screen) is what is owed.
        var ledger = account.GetProperty("ledger").EnumerateArray().ToList();
        Assert.Equal(180_000m, ledger[0].Money("balance"));
        Assert.Equal("Payment", ledger[0].GetProperty("type").GetString());

        // The first project's manager sees only the first project's part; the project's own figures agree.
        var managerView = await AccountAsync(api, cast.Manager, delta);
        Assert.Equal(140_000m, managerView.GetProperty("totals").Money("outstandingWork"));
        Assert.Single(managerView.GetProperty("projects").EnumerateArray());
        var projectTotal = (await api.ContractAsync(cast.Manager, cast.ProjectId, onConcrete)).Account().Money("outstandingWork")
                           + (await api.ContractAsync(cast.Manager, cast.ProjectId, onMasonry)).Account().Money("outstandingWork");
        Assert.Equal(140_000m, projectTotal);
    }

    [DatabaseFact]
    public async Task An_engagement_is_for_a_main_work_item_of_its_project_and_its_lines_stay_inside_that_item()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("BDR-ENG-003");
        var delta = await api.CreateCompanyAsync(cast.Executive, "Delta Concrete");
        var concrete = await api.CreateProjectItemAsync(cast.Executive, cast.ProjectId, "Concrete works");
        var masonry = await api.CreateProjectItemAsync(cast.Executive, cast.ProjectId, "Masonry works");
        var footings = await PostSubItemAsync(api, cast.Executive, cast.ProjectId, concrete.Id(), "Footings");
        var other = (await api.CreateProjectAsync(cast.Executive, "BDR-ENG-004", cast.ManagerId)).GetProperty("id").GetInt32();
        var foreign = await api.CreateProjectItemAsync(cast.Executive, other, "Foreign");

        var onSubItem = await api.PostContractAsync(cast.Manager, cast.ProjectId,
            SubcontractsApi.ContractBody(delta, "UnitRate", 10_000m, new() { ["projectItemId"] = footings }));
        Assert.Equal("contract_item_not_main", await CodeOf(onSubItem));
        var crossProject = await api.PostContractAsync(cast.Manager, cast.ProjectId,
            SubcontractsApi.ContractBody(delta, "UnitRate", 10_000m, new() { ["projectItemId"] = foreign.Id() }));
        Assert.Equal(HttpStatusCode.BadRequest, crossProject.StatusCode);

        var contract = (await api.CreateContractAsync(cast.Manager, cast.ProjectId,
            SubcontractsApi.ContractBody(delta, "UnitRate", 300_000m, new() { ["projectItemId"] = concrete.Id() }))).ContractId();

        // A line may go to the engagement's item or one of its sub-items, never to another main item.
        await api.AddItemAsync(cast.Manager, cast.ProjectId, contract, 1m, 1_000m, projectItemId: footings);
        var outside = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/subcontracts/{contract}/items", cast.Manager, new
        {
            name = "Blockwork", workType = "Concrete", unit = "M3", quantity = 1m, unitPrice = 1_000m, projectItemId = masonry.Id(),
        });
        Assert.Equal("item_outside_contract_scope", await CodeOf(outside));
    }

    [DatabaseFact]
    public async Task A_variation_changes_the_value_only_once_approved_by_someone_else_and_never_below_the_work_done()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("BDR-ENG-005");
        var delta = await api.CreateCompanyAsync(cast.Executive, "Delta Concrete");
        var concrete = await api.CreateProjectItemAsync(cast.Executive, cast.ProjectId, "Concrete works");
        var contract = (await api.CreateContractAsync(cast.Manager, cast.ProjectId,
            SubcontractsApi.ContractBody(delta, "UnitRate", 300_000m, new() { ["projectItemId"] = concrete.Id() }))).ContractId();
        await ApprovedWorkAsync(api, cast.Manager, cast.Executive, cast.ProjectId, contract, 100_000m);

        var requested = await RequestVariationAsync(api, cast.Manager, cast.ProjectId, contract, 50_000m);
        await EnsureStatus(requested, HttpStatusCode.Created);
        var variation = await Json(requested);
        Assert.Equal("variation_pending", await CodeOf(await RequestVariationAsync(api, cast.Manager, cast.ProjectId, contract, 1_000m)));

        // It waits in the Executive's work inbox, under its contract.
        var inbox = await Json(await api.SendAsync(HttpMethod.Get, "/api/inbox", cast.Executive));
        Assert.Contains(inbox.GetProperty("items").EnumerateArray(), item => item.GetProperty("recordType").GetString() == "ContractVariation"
            && item.GetProperty("recordId").GetInt32() == variation.Id() && item.GetProperty("parentId").GetInt32() == contract);

        // Still 300k until decided; the manager can't approve (no subcontractors Approve).
        Assert.Equal(300_000m, (await api.ContractAsync(cast.Manager, cast.ProjectId, contract)).Account().Money("value"));
        Assert.Equal(HttpStatusCode.Forbidden, (await DecideAsync(api, cast.Manager, cast.ProjectId, variation.Id(), "approve")).StatusCode);
        await EnsureStatus(await DecideAsync(api, cast.Executive, cast.ProjectId, variation.Id(), "approve"), HttpStatusCode.OK);

        var account = (await api.ContractAsync(cast.Manager, cast.ProjectId, contract)).Account();
        Assert.Equal((350_000m, 250_000m), (account.Money("value"), account.Money("remainingToExecute")));

        // Down to less than the 100k already executed is refused when decided.
        var cut = await Json(await RequestVariationAsync(api, cast.Manager, cast.ProjectId, contract, -300_000m));
        var refused = await DecideAsync(api, cast.Executive, cast.ProjectId, cut.Id(), "approve");
        Assert.Equal("variation_below_executed", await CodeOf(refused));
        await EnsureStatus(await DecideAsync(api, cast.Executive, cast.ProjectId, cut.Id(), "reject", "Below the work done"), HttpStatusCode.OK);
        Assert.Equal(350_000m, (await api.ContractAsync(cast.Manager, cast.ProjectId, contract)).Account().Money("value"));
    }

    /// <summary>A contract line worth <paramref name="amount"/>, measured in full and approved by <paramref name="reviewer"/>.</summary>
    private static async Task ApprovedWorkAsync(ProjectsApi api, Session recorder, Session reviewer, int projectId, int contractId, decimal amount)
    {
        var line = await api.AddItemAsync(recorder, projectId, contractId, quantity: 10m, unitPrice: amount / 10m);
        var measured = await api.MeasuredAsync(recorder, projectId, contractId, line.Id(), 10m);
        await api.ApprovedAsync(reviewer, projectId, "measurements", measured);
    }

    private static async Task<JsonElement> AccountAsync(ProjectsApi api, Session session, int subcontractorId)
    {
        var response = await api.SendAsync(HttpMethod.Get, $"/api/subcontractors/{subcontractorId}/account?pageSize=100", session);
        await EnsureStatus(response, HttpStatusCode.OK);
        return await Json(response);
    }

    private static Task<HttpResponseMessage> RequestVariationAsync(ProjectsApi api, Session session, int projectId, int contractId, decimal amount) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/subcontracts/{contractId}/variations", session,
            new { amount, reason = "Extra slab approved by the client", reference = "VO-01" });

    private static Task<HttpResponseMessage> DecideAsync(ProjectsApi api, Session session, int projectId, int variationId, string action, string? note = null) =>
        api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/subcontract-variations/{variationId}/{action}", session, new { note });

    private static async Task<int> PostSubItemAsync(ProjectsApi api, Session session, int projectId, int parentItemId, string nameEn)
    {
        var response = await api.SendAsync(HttpMethod.Post, $"/api/projects/{projectId}/items", session, new
        {
            nameAr = $"بند {nameEn}", nameEn, category = "Concrete", unit = "M3", parentItemId,
        });
        await EnsureStatus(response, HttpStatusCode.Created);
        return (await Json(response)).GetProperty("id").GetInt32();
    }
}
