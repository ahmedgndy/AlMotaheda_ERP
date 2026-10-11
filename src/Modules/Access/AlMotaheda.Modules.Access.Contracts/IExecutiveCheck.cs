namespace AlMotaheda.Modules.Access.Contracts;

/// <summary>
/// For changes that grant or remove access in other modules, such as adding someone to a project.
/// The caller's token can be up to 15 minutes old, so its role claim alone is not enough.
/// </summary>
public interface IExecutiveCheck
{
    /// <summary>
    /// True only when, right now, the user exists, is active, has the Executive role, has no pending password
    /// change, and the sign-in <paramref name="sessionId"/> (the token's <c>sid</c>) has not ended.
    /// These are the same rules the Access module applies before any user-management change.
    /// </summary>
    Task<bool> IsActiveExecutiveAsync(int userId, Guid sessionId, CancellationToken cancellationToken);
}
