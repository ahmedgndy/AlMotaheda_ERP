using AlMotaheda.BuildingBlocks.Authorization;

namespace AlMotaheda.Modules.Access.Data;

/// <summary>
/// The permission modules and the roles' default permissions, copied from the prototype's role definitions, plus the
/// HR and Purchasing roles and modules (docs/modernization/10-hr-design.md, 12-procurement-design.md).
/// </summary>
/// <remarks>
/// <para>This is the seed data of the migrations. Changing it later needs a new migration, and new roles and modules
/// are only ever appended, so existing ids never change.</para>
/// <para>Ids are fixed: role ids follow <see cref="Roles"/>, and permission ids are
/// <c>moduleIndex * 5 + actionIndex + 1</c>, so the seed rows never change id between migrations.</para>
/// </remarks>
internal static class PermissionCatalog
{
    /// <summary>The 19 permission modules: the prototype's 17 in its order, then <c>hr</c> and <c>procurement</c>.</summary>
    public static IReadOnlyList<string> Modules { get; } =
    [
        "projects", "subcontractors", "workers", "site", "fuel", "fleet", "equipment", "office", "custody",
        "income", "catalog", "reports", "monitoring", "payments", "finance", "accounts", "settings",
        HrModule, ProcurementModule,
    ];

    /// <summary>Employees, salary structures, payroll inputs and salary sheets.</summary>
    public const string HrModule = "hr";

    /// <summary>Requisitions, quotations, purchase orders, receipts and supplier invoices.</summary>
    public const string ProcurementModule = "procurement";

    /// <summary>The module that controls the users screens.</summary>
    public const string AccountsModule = "accounts";

    /// <summary>The eight roles, in id order (executive = 1, hr = 7, purchasing = 8).</summary>
    public static IReadOnlyList<string> Roles { get; } =
    [
        RoleCodes.Executive, RoleCodes.Manager, RoleCodes.Engineer,
        RoleCodes.OfficeAdmin, RoleCodes.Accountant, RoleCodes.Staff, RoleCodes.Hr, RoleCodes.Purchasing,
    ];

    /// <summary>
    /// Each role's default actions per module, as one letter per action
    /// (V = View, C = Create, E = Edit, A = Approve, F = See money). Modules not listed have no access.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> RoleDefaults { get; } =
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            [RoleCodes.Executive] = Modules.ToDictionary(module => module, module => module == "monitoring" ? "VE" : "VCEAF"),
            [RoleCodes.Manager] = new Dictionary<string, string>
            {
                ["projects"] = "VCEF", ["subcontractors"] = "VCEF", ["workers"] = "VCAF", ["site"] = "VCAF",
                ["fuel"] = "VAF", ["fleet"] = "VCF", ["equipment"] = "VEA", ["custody"] = "V", ["income"] = "V",
                ["catalog"] = "VCEF", ["reports"] = "VCEA", ["monitoring"] = "V", ["payments"] = "V",
                // Asks for materials and approves requisitions of the projects they manage (policy off).
                [ProcurementModule] = "VCAF",
            },
            [RoleCodes.Engineer] = new Dictionary<string, string>
            {
                ["projects"] = "VC", ["workers"] = "VC", ["site"] = "VC", ["fuel"] = "VC", ["fleet"] = "VC",
                ["equipment"] = "V", ["custody"] = "VC", ["catalog"] = "V", ["reports"] = "VC",
                [ProcurementModule] = "VC",
            },
            [RoleCodes.OfficeAdmin] = new Dictionary<string, string>
            {
                ["office"] = "VCE", ["custody"] = "VC", ["reports"] = "VC", ["payments"] = "V",
            },
            [RoleCodes.Accountant] = new Dictionary<string, string>
            {
                ["projects"] = "VAF", ["subcontractors"] = "VCEF", ["workers"] = "VCEAF", ["site"] = "VF",
                ["fuel"] = "VAF", ["fleet"] = "VAF", ["equipment"] = "VAF", ["office"] = "VCEAF", ["custody"] = "VAF",
                ["income"] = "VCEF", ["catalog"] = "VF", ["reports"] = "VCEA", ["payments"] = "VCF",
                ["finance"] = "VCEAF", ["accounts"] = "V",
                // Reviews the salary sheet (approve or return) and sees salaries; can't edit employees.
                [HrModule] = "VAF",
                // Second approval of requisitions (policy off); records invoices through finance.
                [ProcurementModule] = "VAF",
            },
            [RoleCodes.Staff] = new Dictionary<string, string>
            {
                ["projects"] = "V", ["fuel"] = "VC", ["fleet"] = "V", ["equipment"] = "V", ["custody"] = "VC",
                ["reports"] = "VC",
            },
            // Prepares everything up to the salary sheet; never approves it and has no payments access.
            [RoleCodes.Hr] = new Dictionary<string, string>
            {
                [HrModule] = "VCEF",
            },
            // Works the requisitions it receives; never approves them, never receives goods, never pays.
            [RoleCodes.Purchasing] = new Dictionary<string, string>
            {
                [ProcurementModule] = "VCEF",
            },
        };

    public static int RoleId(string roleCode)
    {
        var index = Roles.ToList().IndexOf(roleCode);
        return index >= 0 ? index + 1 : throw new ArgumentOutOfRangeException(nameof(roleCode), roleCode, "Unknown role.");
    }

    public static int PermissionId(string module, PermissionAction action)
    {
        var moduleIndex = Modules.ToList().IndexOf(module);
        if (moduleIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(module), module, "Unknown permission module.");
        }

        return moduleIndex * PermissionActions.All.Count + PermissionActions.All.ToList().IndexOf(action) + 1;
    }

    public static bool IsKnownModule(string? module) => module is not null && Modules.Contains(module);

    public static bool IsKnownRole(string? roleCode) => roleCode is not null && Roles.Contains(roleCode);

    /// <summary>The fixed timestamp of every seed row, so the migration is deterministic.</summary>
    public static readonly DateTime SeededAt = new(2026, 10, 11, 0, 0, 0, DateTimeKind.Utc);

    public static IEnumerable<Role> SeedRoles() =>
        Roles.Select(code => new Role { Id = RoleId(code), Code = code, CreatedAt = SeededAt });

    public static IEnumerable<Permission> SeedPermissions() =>
        from module in Modules
        from action in PermissionActions.All
        select new Permission
        {
            Id = PermissionId(module, action),
            Module = module,
            Action = action,
            CreatedAt = SeededAt,
        };

    public static IEnumerable<RolePermission> SeedRolePermissions() =>
        from role in RoleDefaults
        from modulePermissions in role.Value
        from letter in modulePermissions.Value
        select new RolePermission
        {
            RoleId = RoleId(role.Key),
            PermissionId = PermissionId(modulePermissions.Key, ParseLetter(letter)),
            CreatedAt = SeededAt,
        };

    private static PermissionAction ParseLetter(char letter) =>
        PermissionActions.TryFromLetter(letter, out var action)
            ? action
            : throw new InvalidOperationException($"Unknown permission letter '{letter}'.");
}
