using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Projects.Tests.Database;

/// <summary>
/// Membership is access, so only an active Executive changes it, checked in the database. History is kept.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class MembershipTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task Replacing_the_manager_keeps_history_and_moves_access()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (oldManager, oldManagerSession) = await api.CreateSignedInUserAsync(executive, "manager");
        var (newManager, newManagerSession) = await api.CreateSignedInUserAsync(executive, "manager");
        var id = (await api.CreateProjectAsync(executive, "MGR-001", oldManager)).GetProperty("id").GetInt32();

        var response = await api.SendAsync(HttpMethod.Put, $"/api/projects/{id}/manager", executive,
            new { userId = newManager, reason = "Moved to the Lake Wall project" });

        await EnsureStatus(response, HttpStatusCode.OK);
        var current = await Json(response);
        Assert.Equal(newManager, current.GetProperty("userId").GetInt32());
        Assert.Equal("ProjectManager", current.GetProperty("responsibility").GetString());

        var history = await api.MembersAsync(executive, id, includeEnded: true);
        var ended = history.Single(member => member.GetProperty("userId").GetInt32() == oldManager);
        Assert.Equal("Moved to the Lake Wall project", ended.GetProperty("endReason").GetString());
        Assert.Equal(executive.UserId, ended.GetProperty("endedBy").GetInt32());
        Assert.Single(await api.MembersAsync(executive, id));

        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, $"/api/projects/{id}", oldManagerSession)).StatusCode);
        var asNewManager = await api.GetProjectAsync(newManagerSession, id);
        Assert.Equal("ProjectManager", asNewManager.GetProperty("myResponsibility").GetString());

        // The replaced manager's edit is refused like any project they can't see.
        var lateEdit = await api.SendAsync(HttpMethod.Put, $"/api/projects/{id}", oldManagerSession, ProjectsApi.EditBody(asNewManager));
        Assert.Equal(HttpStatusCode.NotFound, lateEdit.StatusCode);

        // The end reason is the Executive's note: other members see the history without it.
        var historyForManager = await api.MembersAsync(newManagerSession, id, includeEnded: true);
        Assert.Equal(JsonValueKind.Null, historyForManager.Single(member => member.GetProperty("userId").GetInt32() == oldManager)
            .GetProperty("endReason").ValueKind);
    }

    [DatabaseFact]
    public async Task A_team_member_promoted_to_manager_keeps_one_active_membership()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var oldManager = await api.CreateUserAsync(executive, "manager");
        var teamManager = await api.CreateUserAsync(executive, "manager");
        var id = (await api.CreateProjectAsync(executive, "MGR-002", oldManager, team: [teamManager])).GetProperty("id").GetInt32();

        await EnsureStatus(await api.SendAsync(HttpMethod.Put, $"/api/projects/{id}/manager", executive, new { userId = teamManager }), HttpStatusCode.OK);
        var again = await api.SendAsync(HttpMethod.Put, $"/api/projects/{id}/manager", executive, new { userId = teamManager });

        await EnsureStatus(again, HttpStatusCode.OK);
        var rows = await api.QueryAsync(db => db.ProjectMembers.AsNoTracking().Where(member => member.ProjectId == id).ToListAsync());
        var promoted = rows.Where(member => member.UserId == teamManager).ToList();
        Assert.Equal(2, promoted.Count);
        Assert.Single(promoted, member => member.EndDate == null && member.Responsibility == MemberResponsibility.ProjectManager);
        Assert.Single(promoted, member => member.EndReason == "Became the project manager");
        Assert.Single(rows, member => member.EndDate == null);
    }

    [DatabaseFact]
    public async Task Adding_ending_and_the_rules_around_them()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var engineer = await api.CreateUserAsync(executive, "engineer");
        var driver = await api.CreateUserAsync(executive, "staff");
        var projectA = (await api.CreateProjectAsync(executive, "ADD-001", executive.UserId)).GetProperty("id").GetInt32();
        var projectB = (await api.CreateProjectAsync(executive, "ADD-002", executive.UserId)).GetProperty("id").GetInt32();

        var added = await api.SendAsync(HttpMethod.Post, $"/api/projects/{projectA}/members", executive, new { userId = engineer, responsibility = "siteEngineer" });
        await EnsureStatus(added, HttpStatusCode.Created);
        var member = await Json(added);

        var twice = await api.SendAsync(HttpMethod.Post, $"/api/projects/{projectA}/members", executive, new { userId = engineer, responsibility = "TeamMember" });
        var driverAsEngineer = await api.SendAsync(HttpMethod.Post, $"/api/projects/{projectA}/members", executive, new { userId = driver, responsibility = "SiteEngineer" });
        var wrongProject = await api.SendAsync(HttpMethod.Post, $"/api/projects/{projectB}/members/{member.GetProperty("memberId").GetInt32()}/end",
            executive, new { reason = "x", rowVersion = member.GetProperty("rowVersion").GetString() });
        var missingProject = await api.SendAsync(HttpMethod.Post, "/api/projects/987654/members", executive, new { userId = driver, responsibility = "TeamMember" });

        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);
        Assert.Equal("already_member", await CodeOf(twice));
        Assert.Equal(HttpStatusCode.BadRequest, driverAsEngineer.StatusCode);
        Assert.Equal("user_not_assignable", await CodeOf(driverAsEngineer));
        Assert.Equal(HttpStatusCode.NotFound, wrongProject.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missingProject.StatusCode);

        var endPath = $"/api/projects/{projectA}/members/{member.GetProperty("memberId").GetInt32()}/end";
        var ended = await api.SendAsync(HttpMethod.Post, endPath, executive, new { reason = "Finished", rowVersion = member.GetProperty("rowVersion").GetString() });
        await EnsureStatus(ended, HttpStatusCode.OK);
        var endedTwice = await api.SendAsync(HttpMethod.Post, endPath, executive, new { reason = "Again", rowVersion = (await Json(ended)).GetProperty("rowVersion").GetString() });
        Assert.Equal("membership_already_ended", await CodeOf(endedTwice));

        var manager = (await api.MembersAsync(executive, projectA)).Single(row => row.GetProperty("responsibility").GetString() == "ProjectManager");
        var endManager = await api.SendAsync(HttpMethod.Post, $"/api/projects/{projectA}/members/{manager.GetProperty("memberId").GetInt32()}/end",
            executive, new { reason = "x", rowVersion = manager.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.Conflict, endManager.StatusCode);
        Assert.Equal("manager_required", await CodeOf(endManager));

        // Ended rows stay; the person can be added again later.
        await EnsureStatus(await api.SendAsync(HttpMethod.Post, $"/api/projects/{projectA}/members", executive,
            new { userId = engineer, responsibility = "SiteEngineer" }), HttpStatusCode.Created);
        Assert.Equal(2, await api.QueryAsync(db => db.ProjectMembers.CountAsync(row => row.UserId == engineer)));
    }

    [DatabaseFact]
    public async Task A_signed_out_executives_token_can_no_longer_change_membership()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var engineer = await api.CreateUserAsync(executive, "engineer");
        var id = (await api.CreateProjectAsync(executive, "OUT-001", executive.UserId)).GetProperty("id").GetInt32();

        await EnsureStatus(await api.SendAsync(HttpMethod.Post, "/api/auth/logout", executive), HttpStatusCode.NoContent);

        // The access token itself is still valid for up to 15 minutes; the database check stops it.
        var add = await api.SendAsync(HttpMethod.Post, $"/api/projects/{id}/members", executive, new { userId = engineer, responsibility = "SiteEngineer" });

        Assert.Equal(HttpStatusCode.Forbidden, add.StatusCode);
        Assert.Equal(0, await api.QueryAsync(db => db.ProjectMembers.CountAsync(row => row.UserId == engineer)));
    }

    [DatabaseFact]
    public async Task Disabled_people_and_changed_roles_are_flagged_and_disabled_people_cannot_be_added()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var engineer = await api.CreateUserAsync(executive, "engineer");
        var driver = await api.CreateUserAsync(executive, "staff");
        var id = (await api.CreateProjectAsync(executive, "FLAG-001", executive.UserId, siteEngineers: [engineer], team: [driver]))
            .GetProperty("id").GetInt32();

        await EnsureStatus(await api.SendAsync(HttpMethod.Post, $"/api/users/{driver}/disable", executive, new { }), HttpStatusCode.OK);
        var user = await Json(await api.SendAsync(HttpMethod.Get, $"/api/users/{engineer}", executive));
        await EnsureStatus(await api.SendAsync(HttpMethod.Put, $"/api/users/{engineer}", executive, new
        {
            email = user.GetProperty("email").GetString(),
            nameAr = user.GetProperty("nameAr").GetString(),
            nameEn = user.GetProperty("nameEn").GetString(),
            role = "staff",
            rowVersion = user.GetProperty("rowVersion").GetString(),
        }), HttpStatusCode.OK);

        var members = await api.MembersAsync(executive, id);
        JsonElement Row(int userId) => members.Single(member => member.GetProperty("userId").GetInt32() == userId);

        Assert.False(Row(driver).GetProperty("userIsActive").GetBoolean());
        Assert.False(Row(driver).GetProperty("fitsResponsibility").GetBoolean());
        Assert.True(Row(engineer).GetProperty("userIsActive").GetBoolean());
        Assert.False(Row(engineer).GetProperty("fitsResponsibility").GetBoolean());

        var other = (await api.CreateProjectAsync(executive, "FLAG-002", executive.UserId)).GetProperty("id").GetInt32();
        var addDisabled = await api.SendAsync(HttpMethod.Post, $"/api/projects/{other}/members", executive, new { userId = driver, responsibility = "TeamMember" });
        Assert.Equal("user_not_assignable", await CodeOf(addDisabled));
    }

    [DatabaseFact]
    public async Task Pickers_list_only_active_users_whose_role_fits()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var manager = await api.CreateUserAsync(executive, "manager");
        var disabledManager = await api.CreateUserAsync(executive, "manager");
        var engineer = await api.CreateUserAsync(executive, "engineer");
        await EnsureStatus(await api.SendAsync(HttpMethod.Post, $"/api/users/{disabledManager}/disable", executive, new { }), HttpStatusCode.OK);

        var response = await api.SendAsync(HttpMethod.Get, "/api/projects/assignable-users?responsibility=ProjectManager", executive);

        var ids = (await Json(response)).GetProperty("items").EnumerateArray().Select(item => item.GetProperty("userId").GetInt32()).ToList();
        Assert.Contains(executive.UserId, ids);
        Assert.Contains(manager, ids);
        Assert.DoesNotContain(disabledManager, ids);
        Assert.DoesNotContain(engineer, ids);
    }
}
