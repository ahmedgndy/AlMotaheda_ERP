using System.Net;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Projects.Tests.Database;

/// <summary>Creating and editing projects, with the rules the database itself enforces.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class ProjectLifecycleTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task Executive_creates_a_project_with_its_manager_site_engineer_and_team()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var manager = await api.CreateUserAsync(executive, "manager");
        var engineer = await api.CreateUserAsync(executive, "engineer");
        var driver = await api.CreateUserAsync(executive, "staff");

        var response = await api.SendAsync(HttpMethod.Post, "/api/projects", executive, ProjectsApi.ProjectBody("bdr-mall-011", manager, new()
        {
            ["status"] = null,
            ["siteEngineerUserIds"] = new[] { engineer },
            // The manager named again in the team is not an error: the manager is always on the team.
            ["teamUserIds"] = new[] { driver, manager },
        }));
        var body = await Json(response);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = body.GetProperty("id").GetInt32();
        Assert.Equal($"/api/projects/{id}", response.Headers.Location?.OriginalString);
        Assert.Equal("BDR-MALL-011", body.GetProperty("code").GetString());
        Assert.Equal("Planning", body.GetProperty("status").GetString());
        Assert.Equal(manager, body.GetProperty("manager").GetProperty("userId").GetInt32());
        Assert.Equal(3, body.GetProperty("memberCount").GetInt32());
        Assert.True(body.GetProperty("can").GetProperty("manageMembers").GetBoolean());
        Assert.Equal(30.1395m, body.GetProperty("siteLatitude").GetDecimal());

        var members = await api.MembersAsync(executive, id);
        Assert.Equal("ProjectManager", members[0].GetProperty("responsibility").GetString());
        Assert.Equal(
            new[] { (manager, "ProjectManager"), (engineer, "SiteEngineer"), (driver, "TeamMember") }.OrderBy(pair => pair.Item1),
            members.Select(member => (member.GetProperty("userId").GetInt32(), member.GetProperty("responsibility").GetString()!)).OrderBy(pair => pair.Item1));
        Assert.All(members, member => Assert.True(member.GetProperty("fitsResponsibility").GetBoolean()));

        var stored = await api.QueryAsync(db => db.Projects.AsNoTracking().SingleAsync(project => project.Id == id));
        Assert.Equal(executive.UserId, stored.CreatedBy);
    }

    [DatabaseFact]
    public async Task Project_codes_are_unique_ignoring_case()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var manager = await api.CreateUserAsync(executive, "manager");
        await api.CreateProjectAsync(executive, "LKW-PIL-007", manager);

        var duplicate = await api.SendAsync(HttpMethod.Post, "/api/projects", executive, ProjectsApi.ProjectBody("lkw-pil-007", manager));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("project_code_taken", await CodeOf(duplicate));
    }

    [DatabaseFact]
    public async Task People_whose_role_or_status_does_not_fit_are_refused()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var engineer = await api.CreateUserAsync(executive, "engineer");
        var accountant = await api.CreateUserAsync(executive, "accountant");
        var disabledManager = await api.CreateUserAsync(executive, "manager");
        await EnsureStatus(await api.SendAsync(HttpMethod.Post, $"/api/users/{disabledManager}/disable", executive, new { }), HttpStatusCode.OK);

        var engineerAsManager = await api.SendAsync(HttpMethod.Post, "/api/projects", executive, ProjectsApi.ProjectBody("P-1", engineer));
        var disabled = await api.SendAsync(HttpMethod.Post, "/api/projects", executive, ProjectsApi.ProjectBody("P-2", disabledManager));
        var accountantOnTeam = await api.SendAsync(HttpMethod.Post, "/api/projects", executive,
            ProjectsApi.ProjectBody("P-3", executive.UserId, new() { ["teamUserIds"] = new[] { accountant } }));
        var unknown = await api.SendAsync(HttpMethod.Post, "/api/projects", executive, ProjectsApi.ProjectBody("P-4", 99999));

        Assert.Equal(HttpStatusCode.BadRequest, engineerAsManager.StatusCode);
        Assert.True((await Json(engineerAsManager)).GetProperty("errors").TryGetProperty("managerUserId", out _));
        Assert.Equal(HttpStatusCode.BadRequest, disabled.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, accountantOnTeam.StatusCode);
        Assert.True((await Json(accountantOnTeam)).GetProperty("errors").TryGetProperty("teamUserIds", out _));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(0, await api.QueryAsync(db => db.Projects.CountAsync()));
    }

    [DatabaseFact]
    public async Task The_project_manager_edits_their_project_but_only_an_executive_changes_its_code()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (managerId, manager) = await api.CreateSignedInUserAsync(executive, "manager");
        var created = await api.CreateProjectAsync(executive, "MDR-MKT-003", managerId);
        var id = created.GetProperty("id").GetInt32();

        var asManager = await api.GetProjectAsync(manager, id);
        Assert.True(asManager.GetProperty("can").GetProperty("edit").GetBoolean());
        Assert.False(asManager.GetProperty("can").GetProperty("changeCode").GetBoolean());
        Assert.Equal("ProjectManager", asManager.GetProperty("myResponsibility").GetString());

        var renamed = await api.SendAsync(HttpMethod.Put, $"/api/projects/{id}", manager,
            ProjectsApi.EditBody(asManager, new() { ["nameEn"] = "Midar Market", ["status"] = "Delayed" }));
        await EnsureStatus(renamed, HttpStatusCode.OK);
        var afterRename = await Json(renamed);
        Assert.Equal("Midar Market", afterRename.GetProperty("nameEn").GetString());
        Assert.Equal("Delayed", afterRename.GetProperty("status").GetString());
        Assert.Equal(manager.UserId, afterRename.GetProperty("updatedBy").GetInt32());

        var codeByManager = await api.SendAsync(HttpMethod.Put, $"/api/projects/{id}", manager,
            ProjectsApi.EditBody(afterRename, new() { ["code"] = "MDR-MKT-004" }));
        Assert.Equal(HttpStatusCode.Forbidden, codeByManager.StatusCode);

        var codeByExecutive = await api.SendAsync(HttpMethod.Put, $"/api/projects/{id}", executive,
            ProjectsApi.EditBody(afterRename, new() { ["code"] = "mdr-mkt-004" }));
        await EnsureStatus(codeByExecutive, HttpStatusCode.OK);
        Assert.Equal("MDR-MKT-004", (await Json(codeByExecutive)).GetProperty("code").GetString());
    }

    [DatabaseFact]
    public async Task An_edit_based_on_an_old_read_is_refused()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var created = await api.CreateProjectAsync(executive, "BDR-RES-015", executive.UserId);
        var id = created.GetProperty("id").GetInt32();

        var first = await api.SendAsync(HttpMethod.Put, $"/api/projects/{id}", executive, ProjectsApi.EditBody(created, new() { ["notes"] = "first" }));
        var second = await api.SendAsync(HttpMethod.Put, $"/api/projects/{id}", executive, ProjectsApi.EditBody(created, new() { ["notes"] = "second" }));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("concurrency_conflict", await CodeOf(second));
    }

    [DatabaseFact]
    public async Task A_completed_project_can_only_be_changed_or_reopened_by_an_executive()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var (managerId, manager) = await api.CreateSignedInUserAsync(executive, "manager");
        var id = (await api.CreateProjectAsync(executive, "BDR-VIL-021", managerId)).GetProperty("id").GetInt32();

        var completed = await api.SendAsync(HttpMethod.Put, $"/api/projects/{id}", manager,
            ProjectsApi.EditBody(await api.GetProjectAsync(manager, id), new() { ["status"] = "Completed" }));
        await EnsureStatus(completed, HttpStatusCode.OK);
        Assert.False((await Json(completed)).GetProperty("can").GetProperty("edit").GetBoolean());

        var reopenedByManager = await api.SendAsync(HttpMethod.Put, $"/api/projects/{id}", manager,
            ProjectsApi.EditBody(await api.GetProjectAsync(manager, id), new() { ["status"] = "Active" }));
        Assert.Equal(HttpStatusCode.Conflict, reopenedByManager.StatusCode);
        Assert.Equal("project_completed", await CodeOf(reopenedByManager));

        var reopenedByExecutive = await api.SendAsync(HttpMethod.Put, $"/api/projects/{id}", executive,
            ProjectsApi.EditBody(await api.GetProjectAsync(executive, id), new() { ["status"] = "Active" }));
        await EnsureStatus(reopenedByExecutive, HttpStatusCode.OK);
    }

    [DatabaseFact]
    public async Task The_database_refuses_bad_rows_even_if_the_code_had_a_bug()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var executive = await api.SignInExecutiveAsync();
        var created = await api.CreateProjectAsync(executive, "BDR-SCH-012", executive.UserId);
        var id = created.GetProperty("id").GetInt32();

        Task<int> Insert(Action<ProjectsDbContext> add) => api.QueryAsync(db =>
        {
            add(db);
            return db.SaveChangesAsync();
        });

        // One coordinate without the other.
        var halfSite = await Assert.ThrowsAsync<DbUpdateException>(() => Insert(db => db.Projects.Add(new Project
        {
            Code = "HALF-SITE", NameAr = "x", NameEn = "x", ClientNameEn = "x", SiteLatitude = 30,
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 2, 1), CreatedBy = executive.UserId,
        })));
        Assert.Contains("CK_Project_Site", halfSite.InnerException!.Message);

        // A second active manager.
        var otherManager = await api.CreateUserAsync(executive, "manager");
        var secondManager = await Assert.ThrowsAsync<DbUpdateException>(() => Insert(db => db.ProjectMembers.Add(new ProjectMember
        {
            ProjectId = id, UserId = otherManager, Responsibility = MemberResponsibility.ProjectManager,
            StartDate = new DateOnly(2026, 1, 1), AssignedBy = executive.UserId,
        })));
        Assert.Contains("UX_ProjectMember_ActiveManager", Assert.IsType<SqlException>(secondManager.InnerException).Message);

        // A member who isn't a user.
        var notAUser = await Assert.ThrowsAsync<DbUpdateException>(() => Insert(db => db.ProjectMembers.Add(new ProjectMember
        {
            ProjectId = id, UserId = 424242, Responsibility = MemberResponsibility.TeamMember,
            StartDate = new DateOnly(2026, 1, 1), AssignedBy = executive.UserId,
        })));
        Assert.Contains("FK_ProjectMember_User_UserId", notAUser.InnerException!.Message);

        // A lower-case code, bypassing the API's upper-casing.
        var lowerCase = await Assert.ThrowsAsync<DbUpdateException>(() => Insert(db => db.Projects.Add(new Project
        {
            Code = "lower", NameAr = "x", NameEn = "x", ClientNameEn = "x",
            StartDate = new DateOnly(2026, 1, 1), EndDate = new DateOnly(2026, 2, 1), CreatedBy = executive.UserId,
        })));
        Assert.Contains("CK_Project_Code", lowerCase.InnerException!.Message);
    }
}
