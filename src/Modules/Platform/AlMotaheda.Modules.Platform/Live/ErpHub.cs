using System.Globalization;
using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Platform.Live;

/// <summary>
/// The live-update hub at <c>/api/hubs/erp</c>. On connect a client is in its own <c>user:{id}</c> group; it joins
/// the scopes of the screens it shows with <see cref="Join"/>, which checks on the server, every time, that the caller
/// may see that scope. The connection closes when its access token expires, so the client reconnects with a fresh
/// token and its joins are checked again.
/// </summary>
internal sealed class ErpHub(IProjectAccess projects, ILogger<ErpHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        if (Context.User?.GetUserId() is { } userId)
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, LiveScopes.User(userId));
        }

        await base.OnConnectedAsync();
    }

    /// <summary>Joins a scope. <c>false</c> = not allowed (or unknown); nothing joined.</summary>
    public async Task<bool> Join(string scope)
    {
        if (Context.User is not { } caller || !await LiveAccess.MayJoinAsync(caller, scope, projects, Context.ConnectionAborted))
        {
            logger.LogInformation("Connection {ConnectionId} was refused scope {Scope}", Context.ConnectionId, scope);
            return false;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, scope);
        return true;
    }

    public async Task Leave(string scope) => await Groups.RemoveFromGroupAsync(Context.ConnectionId, scope);
}

/// <summary>
/// Who may join which scope (04-signalr-and-cache-maps.md §2). An Engineer never joins a company-wide group or a
/// project's money group without the permissions below.
/// </summary>
internal static class LiveAccess
{
    public static async Task<bool> MayJoinAsync(ClaimsPrincipal caller, string scope, IProjectAccess projects, CancellationToken cancellationToken)
    {
        var parts = scope.Split(':');
        switch (parts)
        {
            case ["user", var id]:
                return int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var userId) && caller.GetUserId() == userId;
            case ["company", "finance"]:
                return caller.HasPermission("finance", PermissionAction.View) || caller.HasPermission("payments", PermissionAction.Approve);
            case ["company", "hr"]:
                return caller.HasPermission("hr", PermissionAction.View);
            case ["company", "procurement"]:
                return caller.HasPermission("procurement", PermissionAction.Edit) || caller.GetRoleCode() == AccessRoles.Executive;
            case ["project", var id]:
                return ProjectId(id) is { } projectId && await projects.FindVisibleAsync(caller, projectId, cancellationToken) is not null;
            case ["project", var id, "money"]:
                return ProjectId(id) is { } moneyProjectId
                       && caller.HasPermission("projects", PermissionAction.SeeMoney)
                       && await projects.FindVisibleAsync(caller, moneyProjectId, cancellationToken) is not null;
            default:
                return false;
        }
    }

    private static int? ProjectId(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;
}

/// <summary>Sends live messages to the hub's groups. Implemented with SignalR; a test can replace it.</summary>
internal interface ILiveUpdates
{
    Task PublishAsync(IReadOnlyList<LiveMessage> messages, CancellationToken cancellationToken);
}

internal sealed class SignalRLiveUpdates(IHubContext<ErpHub> hub) : ILiveUpdates
{
    public async Task PublishAsync(IReadOnlyList<LiveMessage> messages, CancellationToken cancellationToken)
    {
        foreach (var message in messages)
        {
            await hub.Clients.Group(message.Group).SendAsync(message.Method, message.Payload, cancellationToken);
        }
    }
}
