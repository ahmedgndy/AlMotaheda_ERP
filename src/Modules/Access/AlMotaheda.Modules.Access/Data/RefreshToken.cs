namespace AlMotaheda.Modules.Access.Data;

/// <summary>
/// A long-lived sign-in token, stored only as a hash. Every refresh revokes the used token and adds a new
/// one in the same family, so a stolen token works at most once.
/// Not audited: the hash is a secret, and sign-ins are in the security log.
/// </summary>
[NotAudited]
internal sealed class RefreshToken
{
    public long Id { get; set; }

    public int UserId { get; set; }
    public User User { get; set; } = null!;

    /// <summary>SHA-256 of the token. The token itself is never stored.</summary>
    public byte[] TokenHash { get; set; } = [];

    /// <summary>Shared by every rotation of one sign-in. Revoking the family ends that sign-in.</summary>
    public Guid FamilyId { get; set; }

    /// <summary>"Remember me": the cookie survives closing the browser.</summary>
    public bool IsPersistent { get; set; }

    /// <summary>Fixed at sign-in. Rotation keeps it, so refreshing never extends a sign-in.</summary>
    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? RevokedAt { get; set; }
    public RefreshTokenRevocation? RevokedReason { get; set; }
}

internal enum RefreshTokenRevocation
{
    /// <summary>Swapped for a new token by a normal refresh.</summary>
    Rotated,
    SignedOut,

    /// <summary>An already-rotated token came back, so the whole family was ended.</summary>
    ReuseDetected,
    PasswordChanged,
    PasswordReset,
    UserDisabled,
}
