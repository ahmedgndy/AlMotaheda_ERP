namespace AlMotaheda.Modules.HR.Contracts;

/// <summary>
/// Reads basic facts about employees for other modules (Fleet: a vehicle's driver is an employee). Never a second
/// person table: a driver, a guard or an engineer is one <c>hr.Employee</c> row, linked to a sign-in account when
/// one exists.
/// </summary>
public interface IEmployeeDirectory
{
    /// <summary>The employees with these ids, in one query. Unknown ids are left out.</summary>
    Task<IReadOnlyDictionary<int, EmployeeRef>> FindAsync(IReadOnlyCollection<int> employeeIds, CancellationToken cancellationToken);

    /// <summary>The employee linked to a sign-in account, or <c>null</c> when the user is not an employee.</summary>
    Task<EmployeeRef?> FindByUserAsync(int userId, CancellationToken cancellationToken);
}

/// <param name="UserId">The employee's sign-in account, when they have one.</param>
/// <param name="IsActive">False once the employee has ended.</param>
public sealed record EmployeeRef(int Id, string EmployeeNo, string NameAr, string NameEn, string Kind, int? UserId, bool IsActive);
