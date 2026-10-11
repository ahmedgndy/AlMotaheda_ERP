namespace AlMotaheda.BuildingBlocks.Http;

/// <summary>
/// Names of the rate-limit policies the host defines. Modules attach them to endpoints with
/// <c>RequireRateLimiting(RateLimitPolicies.Authentication)</c>; the limits themselves are host configuration.
/// </summary>
public static class RateLimitPolicies
{
    /// <summary>Sign-in and password change: a small budget per client address, to slow password guessing.</summary>
    public const string Authentication = "authentication";

    /// <summary>
    /// Token refresh: a larger budget per client address. Refresh tokens can't be guessed, and every page load
    /// refreshes, so offices sharing one address need room.
    /// </summary>
    public const string TokenRefresh = "token-refresh";
}
