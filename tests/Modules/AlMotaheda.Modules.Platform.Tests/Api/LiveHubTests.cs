using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Tests;
using Microsoft.AspNetCore.SignalR.Client;

namespace AlMotaheda.Modules.Platform.Tests.Api;

/// <summary>The hub over a real connection, without SQL Server: sign-in and the company-wide join checks.</summary>
public sealed class LiveHubTests
{
    internal static HubConnection Connect(TestApplication app, string? token) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(app.Client.BaseAddress!, "/api/hubs/erp"), options =>
            {
                if (token is not null)
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                }
            })
            .Build();

    [Fact]
    public async Task A_connection_without_a_token_is_refused()
    {
        await using var app = await TestApplication.StartAsync();
        await using var connection = Connect(app, token: null);

        var refused = await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    [Fact]
    public async Task A_user_who_must_change_their_password_cant_connect()
    {
        await using var app = await TestApplication.StartAsync();
        await using var connection = Connect(app, TestTokens.Create(role: "accountant", mustChangePassword: true, permissions: [("finance", "VCEAF")]));

        await Assert.ThrowsAsync<HttpRequestException>(() => connection.StartAsync());
    }

    [Theory]
    [InlineData("engineer", "projects:VC,site:VC,workers:VC", "company:finance", false)]
    [InlineData("engineer", "projects:VC,site:VC,workers:VC", "company:hr", false)]
    [InlineData("engineer", "projects:VC,site:VC,workers:VC", "company:procurement", false)]
    [InlineData("accountant", "finance:VCEAF", "company:finance", true)]
    [InlineData("executive", "payments:A", "company:finance", true)]
    [InlineData("hr", "hr:VCEF", "company:hr", true)]
    [InlineData("hr", "hr:VCEF", "company:finance", false)]
    [InlineData("purchasing", "procurement:VCEF", "company:procurement", true)]
    [InlineData("manager", "procurement:VCAF", "company:procurement", false)]
    [InlineData("accountant", "finance:VCEAF", "user:1000", true)]
    [InlineData("accountant", "finance:VCEAF", "user:1001", false)]
    [InlineData("accountant", "finance:VCEAF", "everything", false)]
    [InlineData("accountant", "finance:VCEAF", "project:abc", false)]
    public async Task Company_groups_need_their_permission_and_nobody_joins_another_users_group(string role, string permissions, string scope, bool allowed)
    {
        await using var app = await TestApplication.StartAsync();
        var token = TestTokens.Create(userId: 1000, role: role, permissions: [.. permissions.Split(',').Select(pair => (pair.Split(':')[0], pair.Split(':')[1]))]);
        await using var connection = Connect(app, token);
        await connection.StartAsync();

        Assert.Equal(allowed, await connection.InvokeAsync<bool>("Join", scope));
    }
}
