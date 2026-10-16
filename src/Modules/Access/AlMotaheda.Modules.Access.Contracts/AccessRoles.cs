namespace AlMotaheda.Modules.Access.Contracts;

/// <summary>
/// The fixed role codes, exactly as they appear in the access token's <c>role</c> claim.
/// </summary>
/// <remarks>Roles are seeded by the Access module; there is no endpoint to add or remove roles.</remarks>
public static class AccessRoles
{
    public const string Executive = "executive";
    public const string Manager = "manager";
    public const string Engineer = "engineer";
    public const string OfficeAdmin = "office_admin";
    public const string Accountant = "accountant";
    public const string Staff = "staff";

    /// <summary>Human resources: employees, salary structures and the monthly salary sheet (no payments).</summary>
    public const string Hr = "hr";

    /// <summary>Purchasing: requisitions it receives, quotations, purchase orders and suppliers (no payments, no receipts).</summary>
    public const string Purchasing = "purchasing";
}
