using AlMotaheda.Api.Tests;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Security;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Access.Tests;

/// <summary>Signs access tokens with the test hosts' key, for tests that need a caller without a database.</summary>
internal static class TestTokens
{
    public static AccessTokenOptions Options { get; } = new() { SigningKey = TestApplication.TestSigningKey };

    /// <param name="permissions">Module and letters, for example <c>("accounts", "V")</c>.</param>
    public static string Create(
        int userId = 1000,
        string role = "manager",
        bool mustChangePassword = false,
        TimeProvider? time = null,
        params (string Module, string Letters)[] permissions)
    {
        var issuer = new AccessTokenIssuer(Microsoft.Extensions.Options.Options.Create(Options), time ?? TimeProvider.System);
        return issuer.Issue(userId, role, mustChangePassword, Guid.NewGuid(), Permissions(permissions)).Token;
    }

    public static EffectivePermissions Permissions(params (string Module, string Letters)[] permissions) =>
        EffectivePermissions.Combine(
            permissions.SelectMany(entry => entry.Letters.Select(letter =>
                (entry.Module, PermissionActions.TryFromLetter(letter, out var action) ? action : throw new ArgumentException(letter.ToString())))),
            [],
            ignoreOverrides: false);
}

/// <summary>A clock a test can move.</summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
