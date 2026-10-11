using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Access.Security;

/// <summary>Settings for the access tokens (section <c>Access:Tokens</c>).</summary>
internal sealed class AccessTokenOptions
{
    public const string SectionName = "Access:Tokens";

    /// <summary>The smallest signing key HS256 is safe with: 256 bits.</summary>
    public const int MinimumSigningKeyBytes = 32;

    public string Issuer { get; set; } = "AlMotaheda.Api";
    public string Audience { get; set; } = "AlMotaheda.Erp";

    /// <summary>
    /// The secret that signs every token: at least 32 random bytes, written as base64
    /// (for example the output of <c>openssl rand -base64 48</c>). Never put it in appsettings.json:
    /// use user secrets in development and the <c>Access__Tokens__SigningKey</c> environment variable on servers.
    /// </summary>
    /// <remarks>
    /// A random key, not a passphrase: anyone holding one token could try to guess a human-chosen key offline.
    /// </remarks>
    public string SigningKey { get; set; } = "";

    /// <summary>How long an access token works (Q5: 15 minutes).</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Allowed difference between server clocks. The framework default (5 minutes) is too loose for 15-minute tokens.</summary>
    public TimeSpan ClockSkew { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The decoded key, or an empty array when <see cref="SigningKey"/> isn't valid base64.</summary>
    public byte[] SigningKeyBytes
    {
        get
        {
            var buffer = new byte[SigningKey.Length];
            return Convert.TryFromBase64String(SigningKey.Trim(), buffer, out var written) ? buffer[..written] : [];
        }
    }
}

/// <summary>How long a sign-in lasts (section <c>Access:Sessions</c>, Q5).</summary>
internal sealed class AccessSessionOptions
{
    public const string SectionName = "Access:Sessions";

    /// <summary>Without "Remember me": 12 hours.</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromHours(12);

    /// <summary>With "Remember me": 30 days.</summary>
    public TimeSpan PersistentDuration { get; set; } = TimeSpan.FromDays(30);
}

/// <summary>Protection against password guessing (section <c>Access:Lockout</c>, Q6).</summary>
internal sealed class AccessLockoutOptions
{
    public const string SectionName = "Access:Lockout";

    /// <summary>Wrong passwords in a row that lock the account: 5.</summary>
    public int MaxFailedAttempts { get; set; } = 5;

    /// <summary>How long the lock lasts: 15 minutes.</summary>
    public TimeSpan Duration { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// The first Executive, created at startup only when the user table is empty (section <c>Access:Bootstrap</c>, Q13).
/// Leave it empty after the first start.
/// </summary>
internal sealed class AccessBootstrapOptions
{
    public const string SectionName = "Access:Bootstrap";

    public string? Email { get; set; }
    public string? Password { get; set; }
    public string NameAr { get; set; } = "المدير التنفيذي";
    public string NameEn { get; set; } = "Executive Manager";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Email);
}

/// <summary>Stops the API from starting with a missing or weak signing key, instead of failing on the first sign-in.</summary>
internal sealed class AccessTokenOptionsValidator : IValidateOptions<AccessTokenOptions>
{
    public ValidateOptionsResult Validate(string? name, AccessTokenOptions options)
    {
        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.SigningKey))
        {
            failures.Add(
                $"{AccessTokenOptions.SectionName}:SigningKey is missing. Set it with user secrets in development "
                + "or the Access__Tokens__SigningKey environment variable on the server.");
        }
        else if (options.SigningKeyBytes.Length < AccessTokenOptions.MinimumSigningKeyBytes)
        {
            failures.Add(
                $"{AccessTokenOptions.SectionName}:SigningKey must be base64 for at least {AccessTokenOptions.MinimumSigningKeyBytes} "
                + "random bytes. Create one with: openssl rand -base64 48");
        }

        if (string.IsNullOrWhiteSpace(options.Issuer) || string.IsNullOrWhiteSpace(options.Audience))
        {
            failures.Add($"{AccessTokenOptions.SectionName}:Issuer and Audience are required.");
        }

        if (options.Lifetime <= TimeSpan.Zero || options.Lifetime > TimeSpan.FromHours(1))
        {
            failures.Add($"{AccessTokenOptions.SectionName}:Lifetime must be between 0 and 1 hour.");
        }

        if (options.ClockSkew < TimeSpan.Zero || options.ClockSkew > TimeSpan.FromMinutes(5))
        {
            failures.Add($"{AccessTokenOptions.SectionName}:ClockSkew must be between 0 and 5 minutes.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>Session and lockout settings must be sensible numbers; a typo shouldn't silently weaken sign-in.</summary>
internal sealed class AccessSessionOptionsValidator : IValidateOptions<AccessSessionOptions>
{
    public ValidateOptionsResult Validate(string? name, AccessSessionOptions options) =>
        options.Duration > TimeSpan.Zero && options.PersistentDuration >= options.Duration && options.PersistentDuration <= TimeSpan.FromDays(90)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{AccessSessionOptions.SectionName}: Duration must be positive, and PersistentDuration between Duration and 90 days.");
}

internal sealed class AccessLockoutOptionsValidator : IValidateOptions<AccessLockoutOptions>
{
    public ValidateOptionsResult Validate(string? name, AccessLockoutOptions options) =>
        options.MaxFailedAttempts is >= 1 and <= 100 && options.Duration > TimeSpan.Zero && options.Duration <= TimeSpan.FromDays(1)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{AccessLockoutOptions.SectionName}: MaxFailedAttempts must be 1 to 100, and Duration between 0 and 1 day.");
}
