namespace AlMotaheda.Modules.Access.Contracts;

/// <summary>
/// "Does this signed-in user still hold this permission?", read from the database. Asked before actions that move
/// money (deciding and confirming payments), because the permissions in an access token can be up to 15 minutes old.
/// </summary>
public interface IPermissionCheck
{
    /// <summary>
    /// True when the user is active, has no pending password change, the sign-in <paramref name="sessionId"/> is still
    /// open, and their role and overrides grant <paramref name="action"/> (a <c>PermissionAction</c> name, for
    /// example <c>Approve</c>) on <paramref name="module"/>.
    /// </summary>
    Task<bool> HoldsAsync(int userId, Guid sessionId, string module, string action, CancellationToken cancellationToken);
}
