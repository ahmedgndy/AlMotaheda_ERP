using System.Net;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Tests;
using static AlMotaheda.Modules.Projects.Tests.Api.ProjectsApiSecurityTests;

namespace AlMotaheda.Modules.Projects.Tests.Api;

/// <summary>
/// Every Projects endpoint runs its real queries through EF Core's SQL Server translation, against
/// <see cref="StandInDatabase"/>. A query that can't be translated fails here with a 500, without needing SQL
/// Server. The SQL Server tests (Database folder) then check what the queries return.
/// </summary>
public sealed class QueryTranslationTests
{
    [Fact]
    public async Task A_manager_lists_only_projects_with_an_active_membership_filtered_in_sql()
    {
        var (app, database) = await StartAsync();
        await using var _ = app;
        var token = TestTokens.Create(userId: 7, role: "manager", permissions: [("projects", "VCEF")]);

        var response = await Send(app, "GET", "/api/projects", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json(response);
        Assert.Equal(0, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(5, body.GetProperty("countsByStatus").EnumerateObject().Count());

        var count = Assert.Single(database.Commands, sql => sql.TrimStart().StartsWith("SELECT COUNT(*)", StringComparison.Ordinal));
        Assert.Contains("EXISTS", count);
        Assert.Contains("[projects].[ProjectMember]", count);
        Assert.Contains("[EndDate] IS NULL", count);
    }

    [Theory]
    [InlineData("executive")]
    [InlineData("accountant")]
    public async Task Company_wide_roles_list_every_project_without_a_membership_filter(string role)
    {
        var (app, database) = await StartAsync();
        await using var _ = app;
        var token = TestTokens.Create(role: role, permissions: [("projects", "V")]);

        var response = await Send(app, "GET", "/api/projects", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var count = Assert.Single(database.Commands, sql => sql.TrimStart().StartsWith("SELECT COUNT(*)", StringComparison.Ordinal));
        Assert.DoesNotContain("ProjectMember", count);
    }

    [Theory]
    [InlineData("code")]
    [InlineData("name")]
    [InlineData("startDate")]
    [InlineData("endDate")]
    [InlineData("status")]
    public async Task Every_list_filter_and_sort_translates(string sort)
    {
        var (app, _) = await StartAsync();
        await using var __ = app;
        var token = TestTokens.Create(role: "engineer", permissions: [("projects", "VC")]);

        var response = await Send(app, "GET",
            $"/api/projects?search=mall&status=Active&status=onHold&assignedTo=me&sort={sort}&page=2&pageSize=10", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Search_text_is_sent_as_a_parameter_never_inside_the_sql()
    {
        var (app, database) = await StartAsync();
        await using var _ = app;
        var token = TestTokens.Create(role: "executive", permissions: [("projects", "V")]);

        var response = await Send(app, "GET", "/api/projects?search=x'%20OR%201=1--", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.All(database.Commands, sql => Assert.DoesNotContain("OR 1=1", sql));
    }

    [Theory]
    [InlineData("manager")]
    [InlineData("executive")]
    public async Task A_project_that_is_not_visible_answers_404(string role)
    {
        var (app, _) = await StartAsync();
        await using var __ = app;
        var token = TestTokens.Create(role: role, permissions: [("projects", "VCEF")]);

        var project = await Send(app, "GET", "/api/projects/12", token);
        var members = await Send(app, "GET", "/api/projects/12/members?includeEnded=true", token);
        var edit = await Send(app, "PUT", "/api/projects/12", token, new
        {
            code = "BDR-MALL-011",
            nameEn = "Badr Commercial Mall",
            clientNameEn = "Badr Urban Development Co.",
            startDate = "2025-03-01",
            endDate = "2026-12-31",
            status = "Active",
            rowVersion = "AAAAAAAAB9E=",
        });

        Assert.Equal(HttpStatusCode.NotFound, project.StatusCode);
        Assert.Equal("not_found", await CodeOf(project));
        Assert.Equal(HttpStatusCode.NotFound, members.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
    }

    [Fact]
    public async Task Membership_lists_translate_for_yourself_and_for_the_accounts_screen()
    {
        var (app, _) = await StartAsync();
        await using var __ = app;
        var self = TestTokens.Create(userId: 7, role: "engineer", permissions: [("projects", "VC")]);
        var accounts = TestTokens.Create(userId: 8, role: "manager", permissions: [("projects", "V"), ("accounts", "V")]);

        var own = await Send(app, "GET", "/api/projects/memberships?userId=7&includeEnded=true", self);
        var other = await Send(app, "GET", "/api/projects/memberships?userId=7", accounts);

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task Pickers_read_active_users_from_access()
    {
        var (app, database) = await StartAsync();
        await using var _ = app;
        var token = TestTokens.Create(role: "executive", permissions: [("projects", "VCEAF")]);

        var response = await Send(app, "GET", "/api/projects/assignable-users?responsibility=projectManager", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(database.Commands, sql => sql.Contains("[access].[User]", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Changes_that_grant_access_recheck_the_executive_in_the_database()
    {
        var (app, database) = await StartAsync();
        await using var _ = app;
        // A valid Executive token, but the stand-in database knows no such active Executive.
        var token = TestTokens.Create(userId: 1, role: "executive", permissions: [("projects", "VCEAF")]);

        var create = await Send(app, "POST", "/api/projects", token, new
        {
            code = "NEW-001",
            nameEn = "New project",
            clientNameEn = "Client",
            startDate = "2026-10-01",
            endDate = "2027-10-01",
            managerUserId = 2,
        });
        var manager = await Send(app, "PUT", "/api/projects/1/manager", token, new { userId = 2 });
        var add = await Send(app, "POST", "/api/projects/1/members", token, new { userId = 3, responsibility = "SiteEngineer" });
        var end = await Send(app, "POST", "/api/projects/1/members/4/end", token, new { reason = "Moved", rowVersion = "AAAAAAAAB9E=" });

        foreach (var response in new[] { create, manager, add, end })
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Contains(database.Commands, sql => sql.Contains("[access].[User]", StringComparison.Ordinal));
        Assert.DoesNotContain(database.Commands, sql => sql.Contains("[projects]", StringComparison.Ordinal));
    }

    private static async Task<(TestApplication App, StandInDatabase Database)> StartAsync()
    {
        var database = new StandInDatabase();
        var app = await TestApplication.StartAsync(configureBuilder: database.AddTo);
        return (app, database);
    }
}
