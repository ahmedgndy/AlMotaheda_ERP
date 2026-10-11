using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Tests;

namespace AlMotaheda.Modules.Projects.Tests.Api;

/// <summary>
/// The real API over HTTP, for checks that stop a request before it reaches the database: sign-in, permission and
/// Executive checks, and validation. The frontend hiding a button is never the control; these are.
/// </summary>
public sealed class ProjectsApiSecurityTests
{
    public static TheoryData<string, string> Endpoints => new()
    {
        { "GET", "/api/projects" },
        { "GET", "/api/projects/1" },
        { "POST", "/api/projects" },
        { "PUT", "/api/projects/1" },
        { "GET", "/api/projects/1/members" },
        { "PUT", "/api/projects/1/manager" },
        { "POST", "/api/projects/1/members" },
        { "POST", "/api/projects/1/members/2/end" },
        { "GET", "/api/projects/memberships?userId=1" },
        { "GET", "/api/projects/assignable-users?responsibility=TeamMember" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_a_token_get_401(string method, string path)
    {
        await using var app = await TestApplication.StartAsync();

        var response = await Send(app, method, path, token: null, body: new { });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthenticated", await CodeOf(response));
    }

    [Theory]
    [InlineData("GET", "/api/projects")]
    [InlineData("GET", "/api/projects/1")]
    [InlineData("GET", "/api/projects/1/members")]
    public async Task Reading_projects_needs_projects_view(string method, string path)
    {
        await using var app = await TestApplication.StartAsync();
        // Office administrators have no projects permission by default.
        var token = TestTokens.Create(role: "office_admin", permissions: [("office", "VCE")]);

        var response = await Send(app, method, path, token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await CodeOf(response));
    }

    [Fact]
    public async Task Editing_needs_projects_edit()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "staff", permissions: [("projects", "V")]);

        var response = await Send(app, "PUT", "/api/projects/1", token, new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/projects")]
    [InlineData("PUT", "/api/projects/1/manager")]
    [InlineData("POST", "/api/projects/1/members")]
    [InlineData("POST", "/api/projects/1/members/2/end")]
    [InlineData("GET", "/api/projects/assignable-users?responsibility=TeamMember")]
    public async Task Creating_projects_and_changing_membership_is_executive_only_whatever_the_permissions(string method, string path)
    {
        await using var app = await TestApplication.StartAsync();
        // Every permission an Executive could grant, but not the Executive role: membership is access, so it
        // can't be unlocked by a permission (the same rule as user management).
        var token = TestTokens.Create(role: "manager", permissions: [("projects", "VCEAF"), ("accounts", "VCEAF")]);

        var response = await Send(app, method, path, token, new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_project_fields_are_reported_together_before_any_database_work()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "executive", permissions: [("projects", "VCEAF")]);

        var response = await Send(app, "POST", "/api/projects", token, new
        {
            code = "bad code",
            nameEn = "",
            clientNameEn = "Client",
            siteLatitude = 30.1,
            startDate = "2026-12-31",
            endDate = "2026-01-01",
            siteRadiusMeters = 10,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await Json(response);
        Assert.Equal("validation_failed", body.GetProperty("code").GetString());
        var fields = body.GetProperty("errors").EnumerateObject().Select(field => field.Name).ToList();
        Assert.Equal(["code", "endDate", "managerUserId", "nameEn", "siteLongitude", "siteRadiusMeters"], fields.Order());
    }

    [Fact]
    public async Task The_manager_is_set_on_its_own_endpoint_not_as_a_member()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "executive", permissions: [("projects", "VCEAF")]);

        var response = await Send(app, "POST", "/api/projects/1/members", token, new { userId = 5, responsibility = "ProjectManager" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("responsibility", (await Json(response)).GetProperty("errors").EnumerateObject().Select(field => field.Name));
    }

    [Theory]
    [InlineData("/api/projects?status=Archived", "status")]
    [InlineData("/api/projects?status=1", "status")]
    [InlineData("/api/projects?pageSize=0", "pageSize")]
    [InlineData("/api/projects?pageSize=101", "pageSize")]
    [InlineData("/api/projects?assignedTo=someone", "assignedTo")]
    [InlineData("/api/projects?sort=budget", "sort")]
    public async Task List_parameters_are_validated(string path, string field)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "manager", permissions: [("projects", "V")]);

        var response = await Send(app, "GET", path, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, (await Json(response)).GetProperty("errors").EnumerateObject().Select(error => error.Name));
    }

    [Fact]
    public async Task Reading_someone_elses_projects_needs_accounts_view()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(userId: 40, role: "manager", permissions: [("projects", "VCEF")]);

        var response = await Send(app, "GET", "/api/projects/memberships?userId=41", token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Membership_dates_cannot_be_in_the_future()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "executive", permissions: [("projects", "VCEAF")]);

        var add = await Send(app, "POST", "/api/projects/1/members", token, new { userId = 9, responsibility = "TeamMember", startDate = "2999-01-01" });
        var manager = await Send(app, "PUT", "/api/projects/1/manager", token, new { userId = 9, startDate = "2999-01-01" });
        var end = await Send(app, "POST", "/api/projects/1/members/2/end", token, new { endDate = "2999-01-01", reason = "x", rowVersion = "AAAAAAAAB9E=" });

        foreach (var response in new[] { add, manager, end })
        {
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var field = (await Json(response)).GetProperty("errors").EnumerateObject().Single().Name;
            Assert.True(field is "startDate" or "endDate", field);
        }
    }

    [Fact]
    public async Task A_users_project_list_needs_projects_view()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(userId: 40, role: "office_admin", permissions: [("accounts", "V")]);

        var response = await Send(app, "GET", "/api/projects/memberships?userId=40", token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Ending_a_membership_needs_a_reason_and_the_row_version()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "executive", permissions: [("projects", "VCEAF")]);

        var response = await Send(app, "POST", "/api/projects/1/members/2/end", token, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var fields = (await Json(response)).GetProperty("errors").EnumerateObject().Select(field => field.Name).ToList();
        Assert.Contains("reason", fields);
        Assert.Contains("rowVersion", fields);
    }

    internal static Task<HttpResponseMessage> Send(TestApplication app, string method, string path, string? token, object? body = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "GET" || body is null ? null : JsonContent.Create(body),
        };

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return app.Client.SendAsync(request);
    }

    internal static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    internal static async Task<string?> CodeOf(HttpResponseMessage response) =>
        (await Json(response)).TryGetProperty("code", out var code) ? code.GetString() : null;
}
