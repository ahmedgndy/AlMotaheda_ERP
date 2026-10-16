using System.Net;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Tests;
using static AlMotaheda.Api.Tests.ApiCalls;

namespace AlMotaheda.Modules.Platform.Tests.Api;

/// <summary>The backup endpoints without SQL Server: Executive only, and the settings form's validation.</summary>
public sealed class BackupApiTests
{
    public static TheoryData<string, string> Endpoints => new()
    {
        { "GET", "/api/settings/backup" },
        { "PUT", "/api/settings/backup" },
        { "POST", "/api/settings/backup/run" },
        { "GET", "/api/backups" },
    };

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Only_an_executive_reaches_the_backup_endpoints(string method, string path)
    {
        await using var app = await TestApplication.StartAsync();
        var accountant = TestTokens.Create(role: "accountant", permissions: [("finance", "VCEAF"), ("settings", "VCEAF")]);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(app, method, path, token: null, method == "GET" ? null : new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(app, method, path, accountant, method == "GET" ? null : new { })).StatusCode);
    }

    [Fact]
    public async Task The_settings_form_reports_every_field_before_any_database_work()
    {
        await using var app = await TestApplication.StartAsync();
        var executive = TestTokens.Create(role: "executive", permissions: [("settings", "VCEAF")]);

        var response = await Send(app, "PUT", "/api/settings/backup", executive, new { dailyAt = "25:00", folder = "relative\\folder", retentionCount = 0 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(["dailyAt", "enabled", "folder", "retentionCount", "rowVersion"], (await ErrorFields(response)).Order());
    }
}
