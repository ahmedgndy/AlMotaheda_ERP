using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Security;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AlMotaheda.Modules.Access.Tests.Unit;

/// <summary>Tokens the module issues must pass exactly the validation the API applies to every request.</summary>
public sealed class AccessTokenTests
{
    private static readonly JsonWebTokenHandler Handler = new() { MapInboundClaims = false };

    [Fact]
    public async Task Issued_token_validates_and_carries_user_role_and_permissions()
    {
        var token = TestTokens.Create(userId: 42, role: "accountant", permissions: [("accounts", "V"), ("finance", "VCEAF")]);

        var result = await Handler.ValidateTokenAsync(token, AccessTokenIssuer.CreateValidationParameters(TestTokens.Options));

        Assert.True(result.IsValid, result.Exception?.Message);
        Assert.Equal("42", result.Claims[AccessClaims.UserId]);
        Assert.Equal("accountant", result.Claims[AccessClaims.Role]);
        var permissions = Assert.IsAssignableFrom<IEnumerable<object>>(result.Claims[AccessClaims.Permission]).Select(value => value.ToString());
        Assert.Equal(["finance:VCEAF", "accounts:V"], permissions);
        Assert.False(result.Claims.ContainsKey(AccessClaims.PasswordChangeRequired));
    }

    [Fact]
    public async Task Pending_password_change_is_in_the_token()
    {
        var token = TestTokens.Create(mustChangePassword: true);

        var result = await Handler.ValidateTokenAsync(token, AccessTokenIssuer.CreateValidationParameters(TestTokens.Options));

        Assert.Equal("true", result.Claims[AccessClaims.PasswordChangeRequired]);
    }

    [Fact]
    public async Task Token_expires_after_15_minutes_plus_30_seconds_of_clock_skew()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow.AddMinutes(-15).AddSeconds(-31));
        var token = TestTokens.Create(time: clock);

        var result = await Handler.ValidateTokenAsync(token, AccessTokenIssuer.CreateValidationParameters(TestTokens.Options));

        Assert.False(result.IsValid);
        Assert.IsType<SecurityTokenExpiredException>(result.Exception);
    }

    [Fact]
    public async Task Token_signed_with_another_key_is_rejected()
    {
        var otherOptions = new AccessTokenOptions { SigningKey = Convert.ToBase64String(new byte[48]) };
        var forged = new AccessTokenIssuer(Microsoft.Extensions.Options.Options.Create(otherOptions), TimeProvider.System)
            .Issue(1, "executive", false, Guid.NewGuid(), TestTokens.Permissions(("accounts", "VCEAF"))).Token;

        var result = await Handler.ValidateTokenAsync(forged, AccessTokenIssuer.CreateValidationParameters(TestTokens.Options));

        Assert.False(result.IsValid);
    }

    [Fact]
    public async Task Unsigned_token_is_rejected()
    {
        var unsigned = Handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = TestTokens.Options.Issuer,
            Audience = TestTokens.Options.Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { [AccessClaims.Role] = "executive" },
        });

        var result = await Handler.ValidateTokenAsync(unsigned, AccessTokenIssuer.CreateValidationParameters(TestTokens.Options));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Signing_key_must_be_base64_for_at_least_32_random_bytes()
    {
        var validator = new AccessTokenOptionsValidator();

        Assert.True(validator.Validate(null, new AccessTokenOptions { SigningKey = "" }).Failed);
        Assert.True(validator.Validate(null, new AccessTokenOptions { SigningKey = Convert.ToBase64String(new byte[31]) }).Failed);
        Assert.True(validator.Validate(null, new AccessTokenOptions { SigningKey = "a human passphrase that is long enough!!" }).Failed);
        Assert.True(validator.Validate(null, new AccessTokenOptions { SigningKey = Convert.ToBase64String(new byte[32]) }).Succeeded);
    }

    [Fact]
    public void Lockout_and_session_settings_are_range_checked()
    {
        Assert.True(new AccessLockoutOptionsValidator().Validate(null, new AccessLockoutOptions { MaxFailedAttempts = 40_000 }).Failed);
        Assert.True(new AccessLockoutOptionsValidator().Validate(null, new AccessLockoutOptions()).Succeeded);
        Assert.True(new AccessSessionOptionsValidator().Validate(null, new AccessSessionOptions { PersistentDuration = TimeSpan.FromHours(1) }).Failed);
        Assert.True(new AccessSessionOptionsValidator().Validate(null, new AccessSessionOptions()).Succeeded);
    }

    [Fact]
    public async Task Token_carries_the_sign_in_it_belongs_to()
    {
        var sessionId = Guid.NewGuid();
        var token = new AccessTokenIssuer(Microsoft.Extensions.Options.Options.Create(TestTokens.Options), TimeProvider.System)
            .Issue(3, "staff", false, sessionId, TestTokens.Permissions()).Token;

        var result = await Handler.ValidateTokenAsync(token, AccessTokenIssuer.CreateValidationParameters(TestTokens.Options));

        Assert.Equal(sessionId.ToString(), result.Claims[AccessClaims.SessionId]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64url!!")]
    [InlineData("AAAA")]
    public void Malformed_refresh_tokens_are_ignored(string? value)
    {
        Assert.Null(RefreshTokenValues.TryHash(value));
    }

    [Fact]
    public void Refresh_token_hash_is_sha256_of_the_32_random_bytes()
    {
        var bytes = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
        var token = System.Buffers.Text.Base64Url.EncodeToString(bytes);

        Assert.Equal(System.Security.Cryptography.SHA256.HashData(bytes), RefreshTokenValues.TryHash(token));
    }
}
