using System.Net;
using Microsoft.Extensions.Logging;

namespace AlMotaheda.Modules.Access.Common;

/// <summary>
/// Structured security events for sign-in, users and permissions. Until the AuditLog table exists (G14),
/// these entries are the audit trail of who changed what.
/// </summary>
/// <remarks>
/// Never log passwords, tokens, token hashes or the Authorization header. Users are identified by id only.
/// </remarks>
internal sealed partial class SecurityLog
{
    private readonly ILogger<SecurityLog> _logger;

    public SecurityLog(ILogger<SecurityLog> logger) => _logger = logger;

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "User {UserId} signed in")]
    public partial void SignedIn(int userId);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "Sign-in failed: {Reason}, user {UserId}, client {ClientIp}")]
    public partial void SignInFailed(string reason, int? userId, IPAddress? clientIp);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Warning, Message = "User {UserId} locked out until {LockoutEndsAt:o} after repeated wrong passwords")]
    public partial void LockedOut(int userId, DateTime lockoutEndsAt);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information, Message = "User {UserId} signed out")]
    public partial void SignedOut(int userId);

    [LoggerMessage(EventId = 1005, Level = LogLevel.Warning, Message = "Refresh token replay detected for user {UserId}; sign-in {FamilyId} ended, client {ClientIp}")]
    public partial void RefreshTokenReplayed(int userId, Guid familyId, IPAddress? clientIp);

    [LoggerMessage(EventId = 1006, Level = LogLevel.Information, Message = "User {UserId} changed their password; other sessions ended")]
    public partial void PasswordChanged(int userId);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Information, Message = "User {ActorId} created user {UserId} with role {Role}")]
    public partial void UserCreated(int actorId, int userId, string role);

    [LoggerMessage(EventId = 1102, Level = LogLevel.Information, Message = "User {ActorId} updated user {UserId}: changed {ChangedFields}; role {OldRole} -> {NewRole}")]
    public partial void UserUpdated(int actorId, int userId, string changedFields, string oldRole, string newRole);

    [LoggerMessage(EventId = 1103, Level = LogLevel.Information, Message = "User {ActorId} set user {UserId} to {Status}")]
    public partial void UserStatusChanged(int actorId, int userId, string status);

    [LoggerMessage(EventId = 1104, Level = LogLevel.Information, Message = "User {ActorId} reset the password of user {UserId}; their sessions ended")]
    private partial void PasswordResetOfUser(int actorId, int userId);

    [LoggerMessage(EventId = 1105, Level = LogLevel.Warning, Message = "User {ActorId} reset the password of Executive {UserId}; their sessions ended")]
    private partial void PasswordResetOfExecutive(int actorId, int userId);

    /// <summary>Resetting an Executive's password lets the actor sign in as them, so it is logged as a warning.</summary>
    public void PasswordReset(int actorId, int userId, bool targetIsExecutive)
    {
        if (targetIsExecutive)
        {
            PasswordResetOfExecutive(actorId, userId);
        }
        else
        {
            PasswordResetOfUser(actorId, userId);
        }
    }

    [LoggerMessage(EventId = 1201, Level = LogLevel.Information, Message = "User {ActorId} changed role {Role} permissions on {Module}: {Before} -> {After}")]
    public partial void RolePermissionsChanged(int actorId, string role, string module, string before, string after);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Information, Message = "User {ActorId} changed user {UserId} permissions on {Module}: {Before} -> {After}")]
    public partial void UserPermissionsChanged(int actorId, int userId, string module, string before, string after);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Information, Message = "User {ActorId} reset user {UserId} to the role's permissions")]
    public partial void UserPermissionsReset(int actorId, int userId);

    [LoggerMessage(EventId = 1301, Level = LogLevel.Warning, Message = "User {ActorId} was refused {Action}: {Reason}")]
    public partial void ActionRefused(int? actorId, string action, string reason);

    [LoggerMessage(EventId = 1401, Level = LogLevel.Warning, Message = "Created the first Executive, user {UserId}, from configuration. Remove the Access:Bootstrap settings now.")]
    public partial void BootstrapExecutiveCreated(int userId);
}
