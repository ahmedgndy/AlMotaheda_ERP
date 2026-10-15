using System.Net;
using AlMotaheda.Modules.Access.Tests.Database;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Core.CoreApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Planning.PlanningApi;

namespace AlMotaheda.Modules.Projects.Tests.Database.Planning;

/// <summary>Report requests and reports filed without a request, end to end on SQL Server.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class ReportRequestWorkflowTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task A_request_goes_from_the_requester_to_the_recipient_and_back_until_it_is_completed()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("BDR-REQ-001");
        var (outsiderId, _) = await api.CreateSignedInUserAsync(cast.Executive, "engineer");
        var requests = $"/api/projects/{cast.ProjectId}/report-requests";

        // The recipient must be able to open the project.
        var wrongPerson = await api.PostAsync(cast.Manager, requests, new { recipientUserId = outsiderId, type = "Materials", title = "Cement stock" });
        Assert.Equal(HttpStatusCode.BadRequest, wrongPerson.StatusCode);

        var request = await ExpectAsync(await api.PostAsync(cast.Manager, requests, new
        {
            recipientUserId = cast.Engineer.UserId,
            type = "Materials",
            priority = "High",
            title = "Cement stock count",
            dueDate = Date(CompanyToday.AddDays(3)),
        }), HttpStatusCode.Created);
        Assert.Equal("Requested", request.Status());
        var path = $"{requests}/{request.Id()}";

        // Only the recipient answers; the answer needs its text.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.PostAsync(cast.Manager, $"{path}/start", new { rowVersion = request.RowVersion() })).StatusCode);
        var started = await ExpectAsync(await api.PostAsync(cast.Engineer, $"{path}/start", new { rowVersion = request.RowVersion() }), HttpStatusCode.OK);
        Assert.Equal("InProgress", started.Status());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.PostAsync(cast.Engineer, $"{path}/submit", new { rowVersion = started.RowVersion() })).StatusCode);
        var submitted = await ExpectAsync(
            await api.PostAsync(cast.Engineer, $"{path}/submit", new { responseText = "40 bags in the store", rowVersion = started.RowVersion() }), HttpStatusCode.OK);
        Assert.Equal("Submitted", submitted.Status());

        var returned = await ExpectAsync(
            await api.PostAsync(cast.Manager, $"{path}/return", new { note = "Count the steel too", rowVersion = submitted.RowVersion() }), HttpStatusCode.OK);
        Assert.Equal("Returned", returned.Status());
        var resubmitted = await ExpectAsync(
            await api.PostAsync(cast.Engineer, $"{path}/submit", new { responseText = "40 bags, 3.5 t of steel", rowVersion = returned.RowVersion() }), HttpStatusCode.OK);
        var completed = await ExpectAsync(await api.PostAsync(cast.Manager, $"{path}/complete", new { rowVersion = resubmitted.RowVersion() }), HttpStatusCode.OK);
        Assert.Equal("Completed", completed.Status());
        Assert.Equal("40 bags, 3.5 t of steel", completed.GetProperty("responseText").GetString());

        var cancel = await api.PostAsync(cast.Manager, $"{path}/cancel", new { note = "Too late", rowVersion = completed.RowVersion() });
        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        Assert.Equal("invalid_status", await CodeOf(cancel));

        // The accountant sees the project but is not a party: the request is hidden.
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, path, cast.Accountant)).StatusCode);
        Assert.Equal(0, (await api.GetOkAsync(cast.Accountant, requests)).GetProperty("totalCount").GetInt32());
        Assert.Equal(1, (await api.GetOkAsync(cast.Executive, requests)).GetProperty("totalCount").GetInt32());
    }

    [DatabaseFact]
    public async Task An_engineer_files_a_report_nobody_asked_for_and_only_the_requester_cancels()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("BDR-REQ-002");
        var requests = $"/api/projects/{cast.ProjectId}/report-requests";

        var filed = await ExpectAsync(await api.PostAsync(cast.Engineer, requests, new
        {
            isProactive = true,
            sendToUserId = cast.Manager.UserId,
            type = "Equipment",
            title = "Loader breakdown",
            responseText = "The loader's hydraulic hose burst at 10:00. Work moved to manual digging.",
        }), HttpStatusCode.Created);
        Assert.Equal("Submitted", filed.Status());
        Assert.True(filed.GetProperty("isProactive").GetBoolean());
        Assert.Equal(cast.Engineer.UserId, filed.GetProperty("recipient").GetProperty("userId").GetInt32());
        await ExpectAsync(await api.PostAsync(cast.Manager, $"{requests}/{filed.Id()}/complete", new { rowVersion = filed.RowVersion() }), HttpStatusCode.OK);

        var asked = await ExpectAsync(await api.PostAsync(cast.Manager, requests, new
        {
            recipientUserId = cast.Engineer.UserId, type = "Labor", title = "Crew size this week",
        }), HttpStatusCode.Created);
        var byRecipient = await api.PostAsync(cast.Engineer, $"{requests}/{asked.Id()}/cancel", new { note = "No time", rowVersion = asked.RowVersion() });
        Assert.Equal(HttpStatusCode.Forbidden, byRecipient.StatusCode);
        var cancelled = await ExpectAsync(
            await api.PostAsync(cast.Manager, $"{requests}/{asked.Id()}/cancel", new { note = "Not needed any more", rowVersion = asked.RowVersion() }), HttpStatusCode.OK);
        Assert.Equal("Cancelled", cancelled.Status());
    }
}
