using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Contracts;

namespace AlMotaheda.Modules.Fleet.Common;

/// <summary>A user shown next to a record: recorder, reviewer, responsible person.</summary>
internal sealed record FleetPerson(int UserId, string NameAr, string NameEn, bool IsActive)
{
    public static FleetPerson From(int userId, IReadOnlyDictionary<int, UserSummary> users) =>
        users.TryGetValue(userId, out var user)
            ? new FleetPerson(user.Id, user.NameAr, user.NameEn, user.IsActive)
            // Not expected: users are never deleted. Shown by id rather than failing the whole page.
            : new FleetPerson(userId, $"#{userId}", $"#{userId}", false);

    public static FleetPerson? FromOptional(int? userId, IReadOnlyDictionary<int, UserSummary> users) =>
        userId is { } id ? From(id, users) : null;
}

/// <summary>Name lookups for a page of records: one call per list, never one per row.</summary>
internal static class Lookups
{
    private static readonly Dictionary<int, UserSummary> NoUsers = [];
    private static readonly Dictionary<int, SupplierRef> NoSuppliers = [];

    public static async Task<IReadOnlyDictionary<int, UserSummary>> UsersAsync(
        IUserDirectory users, IEnumerable<int?> userIds, CancellationToken cancellationToken)
    {
        var ids = userIds.Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        return ids.Count == 0 ? NoUsers : await users.FindAsync(ids, cancellationToken);
    }

    public static async Task<IReadOnlyDictionary<int, SupplierRef>> SuppliersAsync(
        ISupplierDirectory suppliers, IEnumerable<int?> supplierIds, CancellationToken cancellationToken)
    {
        var ids = supplierIds.Where(id => id is not null).Select(id => id!.Value).Distinct().ToList();
        return ids.Count == 0 ? NoSuppliers : await suppliers.FindAsync(ids, cancellationToken);
    }
}

/// <summary>Checks a supplier chosen on a form: it must exist and be active for new records.</summary>
internal static class SupplierChoice
{
    public static void Check(int supplierId, IReadOnlyDictionary<int, SupplierRef> found, ValidationErrors errors, string field)
    {
        if (!found.TryGetValue(supplierId, out var supplier))
        {
            errors.Add(field, "The supplier was not found.");
        }
        else if (!supplier.IsActive)
        {
            errors.Add(field, "This supplier is inactive and can't be chosen for new records.");
        }
    }
}
