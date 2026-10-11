using System.Net;
using AlMotaheda.Modules.Access.Tests.Database;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Projects.Tests.Database;

/// <summary>
/// Who sees which project. Checked through the API with real tokens, including requests that guess another
/// project's id: the backend decides, never the frontend.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ProjectAccessTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task Each_role_sees_only_the_projects_it_may_see()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (managerA, managerASession) = await api.CreateSignedInUserAsync(executive, "manager");
        var (managerB, managerBSession) = await api.CreateSignedInUserAsync(executive, "manager");
        var (engineer, engineerSession) = await api.CreateSignedInUserAsync(executive, "engineer");
        var (_, accountantSession) = await api.CreateSignedInUserAsync(executive, "accountant");
        var (_, idleDriverSession) = await api.CreateSignedInUserAsync(executive, "staff");

        var projectA = await api.CreateProjectAsync(executive, "AAA-001", managerA, siteEngineers: [engineer]);
        var projectB = await api.CreateProjectAsync(executive, "BBB-002", managerB);
        var idB = projectB.GetProperty("id").GetInt32();

        Assert.Equal(["AAA-001", "BBB-002"], await api.VisibleCodesAsync(executive));
        Assert.Equal(["AAA-001", "BBB-002"], await api.VisibleCodesAsync(accountantSession));
        Assert.Equal(["AAA-001"], await api.VisibleCodesAsync(managerASession));
        Assert.Equal(["AAA-001"], await api.VisibleCodesAsync(engineerSession));
        Assert.Equal(["BBB-002"], await api.VisibleCodesAsync(managerBSession));
        Assert.Empty(await api.VisibleCodesAsync(idleDriverSession));

        // Guessing project B's id: the same 404 as a project that doesn't exist.
        var get = await api.SendAsync(HttpMethod.Get, $"/api/projects/{idB}", managerASession);
        var members = await api.SendAsync(HttpMethod.Get, $"/api/projects/{idB}/members", managerASession);
        var edit = await api.SendAsync(HttpMethod.Put, $"/api/projects/{idB}", managerASession, ProjectsApi.EditBody(projectB));
        var missing = await api.SendAsync(HttpMethod.Get, "/api/projects/987654", managerASession);

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, members.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        Assert.Equal((await Json(missing)).GetProperty("title").GetString(), (await Json(get)).GetProperty("title").GetString());
        Assert.Equal("Project BBB-002", (await api.GetProjectAsync(executive, idB)).GetProperty("nameEn").GetString());
        Assert.Equal(projectA.GetProperty("id").GetInt32(), (await api.GetProjectAsync(engineerSession, projectA.GetProperty("id").GetInt32())).GetProperty("id").GetInt32());
    }

    [DatabaseFact]
    public async Task Being_on_the_team_lets_a_manager_see_a_project_but_not_edit_it()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (managerA, _) = await api.CreateSignedInUserAsync(executive, "manager");
        var (managerB, managerBSession) = await api.CreateSignedInUserAsync(executive, "manager");
        var (engineer, engineerSession) = await api.CreateSignedInUserAsync(executive, "engineer");
        var id = (await api.CreateProjectAsync(executive, "TEAM-001", managerA, siteEngineers: [engineer], team: [managerB]))
            .GetProperty("id").GetInt32();

        var asTeamMember = await api.GetProjectAsync(managerBSession, id);
        Assert.Equal("TeamMember", asTeamMember.GetProperty("myResponsibility").GetString());
        Assert.False(asTeamMember.GetProperty("can").GetProperty("edit").GetBoolean());

        var editByTeamMember = await api.SendAsync(HttpMethod.Put, $"/api/projects/{id}", managerBSession, ProjectsApi.EditBody(asTeamMember));
        var editByEngineer = await api.SendAsync(HttpMethod.Put, $"/api/projects/{id}", engineerSession,
            ProjectsApi.EditBody(await api.GetProjectAsync(engineerSession, id)));

        Assert.Equal(HttpStatusCode.Forbidden, editByTeamMember.StatusCode);
        // Engineers have no projects Edit permission at all.
        Assert.Equal(HttpStatusCode.Forbidden, editByEngineer.StatusCode);
    }

    [DatabaseFact]
    public async Task Ending_a_membership_removes_access_at_once_even_with_the_same_token()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (engineer, engineerSession) = await api.CreateSignedInUserAsync(executive, "engineer");
        var id = (await api.CreateProjectAsync(executive, "END-001", executive.UserId, siteEngineers: [engineer])).GetProperty("id").GetInt32();
        Assert.Equal(["END-001"], await api.VisibleCodesAsync(engineerSession));

        var membership = (await api.MembersAsync(executive, id)).Single(member => member.GetProperty("userId").GetInt32() == engineer);
        var ended = await api.SendAsync(HttpMethod.Post, $"/api/projects/{id}/members/{membership.GetProperty("memberId").GetInt32()}/end",
            executive, new { reason = "Moved to another site", rowVersion = membership.GetProperty("rowVersion").GetString() });
        await EnsureStatus(ended, HttpStatusCode.OK);

        Assert.Empty(await api.VisibleCodesAsync(engineerSession));
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, $"/api/projects/{id}", engineerSession)).StatusCode);

        // Their own list doesn't keep showing the project's current details either.
        var ownHistory = await Json(await api.SendAsync(HttpMethod.Get, $"/api/projects/memberships?userId={engineer}&includeEnded=true", engineerSession));
        Assert.Empty(ownHistory.GetProperty("items").EnumerateArray());
    }

    [DatabaseFact]
    public async Task Assigned_to_me_and_the_status_counters_follow_the_callers_scope()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var manager = await api.CreateUserAsync(executive, "manager");
        await api.CreateProjectAsync(executive, "MINE-001", executive.UserId);
        var other = await api.CreateProjectAsync(executive, "OTHER-002", manager);
        await EnsureStatus(await api.SendAsync(HttpMethod.Put, $"/api/projects/{other.GetProperty("id").GetInt32()}", executive,
            ProjectsApi.EditBody(other, new() { ["status"] = "OnHold" })), HttpStatusCode.OK);

        Assert.Equal(["MINE-001"], await api.VisibleCodesAsync(executive, "&assignedTo=me"));
        Assert.Equal(["OTHER-002"], await api.VisibleCodesAsync(executive, "&status=onHold"));
        Assert.Equal(["MINE-001", "OTHER-002"], await api.VisibleCodesAsync(executive, "&search=-00"));

        var list = await Json(await api.SendAsync(HttpMethod.Get, "/api/projects?status=OnHold", executive));
        Assert.Equal(1, list.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, list.GetProperty("countsByStatus").GetProperty("Active").GetInt32());
        Assert.Equal(1, list.GetProperty("countsByStatus").GetProperty("OnHold").GetInt32());
        Assert.Equal(manager, list.GetProperty("items")[0].GetProperty("manager").GetProperty("userId").GetInt32());
    }

    [DatabaseFact]
    public async Task A_users_project_list_is_theirs_or_for_the_accounts_screen_only()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (engineer, engineerSession) = await api.CreateSignedInUserAsync(executive, "engineer");
        var (_, managerSession) = await api.CreateSignedInUserAsync(executive, "manager");
        var (_, accountantSession) = await api.CreateSignedInUserAsync(executive, "accountant");
        await api.CreateProjectAsync(executive, "LIST-001", executive.UserId, siteEngineers: [engineer]);

        var own = await Json(await api.SendAsync(HttpMethod.Get, $"/api/projects/memberships?userId={engineer}", engineerSession));
        var byAccountant = await api.SendAsync(HttpMethod.Get, $"/api/projects/memberships?userId={engineer}", accountantSession);
        var byManager = await api.SendAsync(HttpMethod.Get, $"/api/projects/memberships?userId={engineer}", managerSession);

        var item = Assert.Single(own.GetProperty("items").EnumerateArray());
        Assert.Equal("LIST-001", item.GetProperty("code").GetString());
        Assert.Equal("SiteEngineer", item.GetProperty("responsibility").GetString());
        Assert.Equal(HttpStatusCode.OK, byAccountant.StatusCode);
        Assert.Single((await Json(byAccountant)).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, byManager.StatusCode);
    }
}
