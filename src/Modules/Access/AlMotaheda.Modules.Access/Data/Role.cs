namespace AlMotaheda.Modules.Access.Data;

/// <summary>
/// One of the six fixed roles. Rows are seeded; there is no endpoint to add or delete roles.
/// Role names live in the frontend's translations, keyed by <see cref="Code"/>.
/// </summary>
internal sealed class Role
{
    public int Id { get; set; }

    /// <summary>Stable code, for example <c>executive</c>. See <see cref="RoleCodes"/>.</summary>
    public required string Code { get; set; }

    public DateTime CreatedAt { get; set; }
    public int? CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    /// <summary>Changes on every update, so two people editing the same role's permissions can't overwrite each other.</summary>
    public byte[] RowVersion { get; set; } = [];

    public List<RolePermission> Permissions { get; } = [];
}

/// <summary>The role codes, as stored in <see cref="Role.Code"/> and sent in access tokens.</summary>
internal static class RoleCodes
{
    public const string Executive = "executive";
    public const string Manager = "manager";
    public const string Engineer = "engineer";
    public const string OfficeAdmin = "office_admin";
    public const string Accountant = "accountant";
    public const string Staff = "staff";
}
