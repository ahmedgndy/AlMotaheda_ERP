using AlMotaheda.Modules.Access.Data;
using Microsoft.AspNetCore.Identity;

namespace AlMotaheda.Modules.Access.Security;

/// <summary>
/// Hashes and checks passwords with ASP.NET Core's <see cref="PasswordHasher{TUser}"/>:
/// PBKDF2 with HMAC-SHA512, 100,000 iterations and a random 128-bit salt per password.
/// </summary>
internal sealed class PasswordService
{
    private readonly PasswordHasher<User> _hasher = new();

    // A real hash of a random value. Checking a password against it when the email is unknown makes
    // "no such user" take as long as "wrong password", so response times don't reveal which emails exist.
    private readonly Lazy<string> _dummyHash;

    public PasswordService()
    {
        _dummyHash = new Lazy<string>(() => _hasher.HashPassword(null!, Guid.NewGuid().ToString("N")));
    }

    public string Hash(User user, string password) => _hasher.HashPassword(user, password);

    public PasswordCheck Verify(User user, string password) =>
        _hasher.VerifyHashedPassword(user, user.PasswordHash, password) switch
        {
            PasswordVerificationResult.Success => PasswordCheck.Correct,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.CorrectButOutdatedHash,
            _ => PasswordCheck.Wrong,
        };

    /// <summary>Spends the same time as a real check, for requests where there is no user to check against.</summary>
    public void VerifyAgainstDummy(string password) => _hasher.VerifyHashedPassword(null!, _dummyHash.Value, password);
}

internal enum PasswordCheck
{
    Wrong,
    Correct,

    /// <summary>Correct, but hashed with older settings: store a new hash.</summary>
    CorrectButOutdatedHash,
}
