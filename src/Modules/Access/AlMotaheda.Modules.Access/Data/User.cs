namespace AlMotaheda.Modules.Access.Data;

/// <summary>A person who can sign in. Users are disabled, never deleted.</summary>
internal sealed class User
{
    public int Id { get; set; }

    /// <summary>As typed, for display.</summary>
    public required string Email { get; set; }

    /// <summary>Trimmed and upper-cased. Unique; used to sign in, so uniqueness ignores case.</summary>
    public required string NormalizedEmail { get; set; }

    /// <summary><c>+</c> and digits only. Unique when present.</summary>
    public string? Phone { get; set; }

    public required string NameAr { get; set; }
    public required string NameEn { get; set; }
    public string? JobTitleAr { get; set; }
    public string? JobTitleEn { get; set; }

    public int RoleId { get; set; }
    public Role Role { get; set; } = null!;

    public UserStatus Status { get; set; } = UserStatus.Active;

    /// <summary>Salted PBKDF2 hash. Never returned by the API and never logged.</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>When set, the user's token only allows changing the password, reading their profile and signing out.</summary>
    public bool MustChangePassword { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public int? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    /// <summary>
    /// Changes whenever the user's record changes, including their permission overrides (that change also
    /// updates this row). Signing in doesn't change it: sign-in bookkeeping lives in <see cref="SignInState"/>.
    /// </summary>
    public byte[] RowVersion { get; set; } = [];

    /// <summary>Created together with the user; one row per user.</summary>
    public UserSignInState SignInState { get; set; } = null!;

    public List<UserPermissionOverride> PermissionOverrides { get; } = [];
}

/// <summary>
/// Sign-in bookkeeping, kept apart from <see cref="User"/> so that signing in (or failing to) never changes the
/// user's RowVersion. Otherwise every sign-in would make an Executive's open edit form fail with a conflict, and
/// someone who knows a password could keep an emergency "disable" from going through.
/// </summary>
internal sealed class UserSignInState
{
    public int UserId { get; set; }

    /// <summary>Wrong passwords in a row (including checks still running) since the last success or lockout.</summary>
    public short FailedLoginCount { get; set; }

    /// <summary>Sign-in is refused until this time (UTC).</summary>
    public DateTime? LockoutEndsAt { get; set; }

    public DateTime? LastLoginAt { get; set; }
}

internal enum UserStatus
{
    Active,
    Disabled,
}

/// <summary>
/// Grants or denies one permission to one user, replacing the role's setting for that module.
/// When a module is overridden, it has one row for each of the five actions.
/// </summary>
internal sealed class UserPermissionOverride
{
    public int UserId { get; set; }
    public int PermissionId { get; set; }

    /// <summary><c>true</c> = grant, <c>false</c> = deny.</summary>
    public bool IsGranted { get; set; }

    public DateTime CreatedAt { get; set; }
    public int? CreatedBy { get; set; }

    public Permission Permission { get; set; } = null!;
}
