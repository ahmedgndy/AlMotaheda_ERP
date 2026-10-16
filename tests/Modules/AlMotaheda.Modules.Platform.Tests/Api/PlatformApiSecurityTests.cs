using System.Net;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Tests;
using static AlMotaheda.Api.Tests.ApiCalls;

namespace AlMotaheda.Modules.Platform.Tests.Api;

/// <summary>Every Platform endpoint over real HTTP without SQL Server: sign-in, the Executive check, validation.</summary>
public sealed class PlatformApiSecurityTests
{
    public static TheoryData<string, string> Endpoints => new()
    {
        { "GET", "/api/notifications" },
        { "GET", "/api/notifications/unread-count" },
        { "POST", "/api/notifications/1/read" },
        { "POST", "/api/notifications/read-all" },
        { "GET", "/api/audit-entries" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Requests_without_a_token_get_401(string method, string path)
    {
        await using var app = await TestApplication.StartAsync();

        var response = await Send(app, method, path, token: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthenticated", await CodeOf(response));
    }

    [Theory]
    [InlineData("manager")]
    [InlineData("accountant")]
    [InlineData("engineer")]
    public async Task Only_an_executive_reads_the_audit_trail(string role)
    {
        await using var app = await TestApplication.StartAsync();
        // Every permission of every module is not enough: the audit trail is a role check.
        var token = TestTokens.Create(role: role, permissions: [("settings", "VCEAF"), ("accounts", "VCEAF"), ("finance", "VCEAF")]);

        var response = await Send(app, "GET", "/api/audit-entries", token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", await CodeOf(response));
    }

    [Fact]
    public async Task A_user_who_must_change_their_password_gets_no_notifications()
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "engineer", mustChangePassword: true);

        var response = await Send(app, "GET", "/api/notifications", token);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("password_change_required", await CodeOf(response));
    }

    [Theory]
    [InlineData("/api/notifications?page=0", "page")]
    [InlineData("/api/notifications?pageSize=101", "pageSize")]
    [InlineData("/api/audit-entries?from=2026-10-10&to=2026-10-01", "to")]
    [InlineData("/api/audit-entries?from=10/01/2026", "from")]
    public async Task Bad_filters_answer_400_with_the_field(string path, string field)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(role: "executive");

        var response = await Send(app, "GET", path, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await ErrorFields(response));
    }

    [Fact]
    public async Task The_lists_run_their_real_queries()
    {
        var database = new StandInDatabase();
        await using var app = await TestApplication.StartAsync(configureBuilder: database.AddTo);
        var executive = TestTokens.Create(role: "executive");

        var notifications = await Send(app, "GET", "/api/notifications?unreadOnly=true", executive);
        var count = await Send(app, "GET", "/api/notifications/unread-count", executive);
        var audit = await Send(app, "GET", "/api/audit-entries?entityType=ProjectExpense&entityKey=5&from=2026-10-01&to=2026-10-31", executive);

        Assert.Equal(HttpStatusCode.OK, notifications.StatusCode);
        Assert.Equal(HttpStatusCode.OK, count.StatusCode);
        Assert.Equal(HttpStatusCode.OK, audit.StatusCode);
        Assert.Contains(database.Commands, sql => sql.Contains("[platform].[Notification]", StringComparison.Ordinal) && sql.Contains("[RecipientUserId] = ", StringComparison.Ordinal));
        Assert.Contains(database.Commands, sql => sql.Contains("[platform].[AuditEntry]", StringComparison.Ordinal));
    }
}
