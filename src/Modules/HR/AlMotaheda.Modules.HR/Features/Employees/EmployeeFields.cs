using System.Text.RegularExpressions;
using AlMotaheda.BuildingBlocks.Validation;
using AlMotaheda.Modules.HR.Data;
using AlMotaheda.Modules.HR.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.HR.Features.Employees;

/// <summary>The employee form, as sent on create and on edit.</summary>
internal interface IEmployeeFieldsRequest
{
    string? NameAr { get; }
    string? NameEn { get; }
    string? NationalId { get; }
    string? Phone { get; }
    string? Email { get; }
    string? JobTitle { get; }
    int? DepartmentId { get; }
    string? Kind { get; }
    string? HireDate { get; }
    int? UserId { get; }
    string? BankName { get; }
    string? Iban { get; }
    string? Notes { get; }
}

/// <summary>The employee form after validation.</summary>
internal sealed record EmployeeFields(
    string NameAr,
    string NameEn,
    string NationalId,
    string? Phone,
    string? Email,
    string JobTitle,
    int DepartmentId,
    EmployeeKind Kind,
    DateOnly HireDate,
    int? UserId,
    string? BankName,
    string? Iban,
    string? Notes)
{
    private static readonly Regex NationalIdPattern = new(@"^[0-9]{14}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex PhonePattern = new(@"^\+?[0-9 ()\-]{3,30}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex EmailPattern = new(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    /// <summary>An IBAN: country letters, two check digits, then up to 30 letters or digits (Egypt: EG + 27 digits).</summary>
    private static readonly Regex IbanPattern = new(@"^[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static EmployeeFields? Validate(IEmployeeFieldsRequest request, ValidationErrors errors)
    {
        var nameAr = Input.RequiredText(request.NameAr, EmployeeConfiguration.NameMaxLength, errors, "nameAr", "The Arabic name");
        var nameEn = Input.RequiredText(request.NameEn, EmployeeConfiguration.NameMaxLength, errors, "nameEn", "The English name");

        var nationalId = Input.RequiredText(request.NationalId, EmployeeConfiguration.NationalIdLength, errors, "nationalId", "The national id");
        if (nationalId is not null && !errors.Has("nationalId") && !NationalIdPattern.IsMatch(nationalId))
        {
            errors.Add("nationalId", "The national id must be 14 digits.");
        }

        var phone = Input.OptionalText(request.Phone, EmployeeConfiguration.PhoneMaxLength, errors, "phone", "The phone");
        if (phone is not null && !errors.Has("phone") && !PhonePattern.IsMatch(phone))
        {
            errors.Add("phone", "The phone may hold only digits, spaces, and + - ( ).");
        }

        var email = Input.OptionalText(request.Email, EmployeeConfiguration.EmailMaxLength, errors, "email", "The email");
        if (email is not null && !errors.Has("email") && (!EmailPattern.IsMatch(email) || !email.All(char.IsAscii)))
        {
            errors.Add("email", "The email is not valid.");
        }

        var jobTitle = Input.RequiredText(request.JobTitle, EmployeeConfiguration.JobTitleMaxLength, errors, "jobTitle", "The job title");

        if (request.DepartmentId is not > 0)
        {
            errors.Add("departmentId", "The department is required.");
        }

        var kind = Input.RequiredName<EmployeeKind>(request.Kind, errors, "kind");
        var hireDate = Input.RequiredDate(request.HireDate, errors, "hireDate", "The hiring date");

        if (request.UserId is <= 0)
        {
            errors.Add("userId", "userId must be a user id, or left out.");
        }

        var bankName = Input.OptionalText(request.BankName, EmployeeConfiguration.BankNameMaxLength, errors, "bankName", "The bank name");
        var iban = Input.Clean(request.Iban)?.Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
        if (iban is not null && !IbanPattern.IsMatch(iban))
        {
            errors.Add("iban", "The IBAN is not valid (for example EG followed by 27 digits).");
        }

        var notes = Input.OptionalText(request.Notes, EmployeeConfiguration.NotesMaxLength, errors, "notes", "Notes");

        return errors.HasErrors
            ? null
            : new EmployeeFields(nameAr!, nameEn!, nationalId!, phone, email, jobTitle!, request.DepartmentId!.Value, kind!.Value,
                hireDate!.Value, request.UserId, bankName, iban, notes);
    }

    /// <param name="withBankDetails">Bank details are written only by callers who may see them (hr See money).</param>
    public void ApplyTo(Employee employee, bool withBankDetails)
    {
        employee.NameAr = NameAr;
        employee.NameEn = NameEn;
        employee.NationalId = NationalId;
        employee.Phone = Phone;
        employee.Email = Email;
        employee.JobTitle = JobTitle;
        employee.DepartmentId = DepartmentId;
        employee.Kind = Kind;
        employee.HireDate = HireDate;
        employee.UserId = UserId;
        employee.Notes = Notes;

        if (withBankDetails)
        {
            employee.BankName = BankName;
            employee.Iban = Iban;
        }
    }
}

/// <summary>One employee. Bank details are <c>null</c> without hr See money.</summary>
internal sealed record EmployeeView(
    int Id,
    string EmployeeNo,
    string NameAr,
    string NameEn,
    string NationalId,
    string? Phone,
    string? Email,
    string JobTitle,
    DepartmentRef Department,
    string Kind,
    DateOnly HireDate,
    string Status,
    DateOnly? EndDate,
    string? EndReason,
    int? UserId,
    string? BankName,
    string? Iban,
    string? Notes,
    DateTime CreatedAt,
    int CreatedBy,
    DateTime? UpdatedAt,
    int? UpdatedBy,
    string RowVersion)
{
    public static EmployeeView From(Employee employee, Department department, bool seesMoney) => new(
        employee.Id,
        employee.EmployeeNo,
        employee.NameAr,
        employee.NameEn,
        employee.NationalId,
        employee.Phone,
        employee.Email,
        employee.JobTitle,
        new DepartmentRef(department.Id, department.NameAr, department.NameEn),
        employee.Kind.ToString(),
        employee.HireDate,
        employee.Status.ToString(),
        employee.EndDate,
        employee.EndReason,
        employee.UserId,
        seesMoney ? employee.BankName : null,
        seesMoney ? employee.Iban : null,
        employee.Notes,
        employee.CreatedAt,
        employee.CreatedBy,
        employee.UpdatedAt,
        employee.UpdatedBy,
        RowVersions.Format(employee.RowVersion));

    public static async Task<EmployeeView?> LoadAsync(HrDbContext db, int employeeId, bool seesMoney, CancellationToken cancellationToken)
    {
        var found = await db.Employees.AsNoTracking()
            .Where(employee => employee.Id == employeeId)
            .Join(db.Departments, employee => employee.DepartmentId, department => department.Id, (employee, department) => new { employee, department })
            .SingleOrDefaultAsync(cancellationToken);
        return found is null ? null : From(found.employee, found.department, seesMoney);
    }
}

internal sealed record DepartmentRef(int Id, string NameAr, string NameEn);

/// <summary>The employee list query, as <see cref="IQueryable{T}"/> so a test can prove it translates.</summary>
internal static class EmployeeQueries
{
    internal sealed record Row(Employee Employee, Department Department);

    /// <summary>Search in the names, number, national id and phone; filter by department, kind and status; order by English name.</summary>
    public static IQueryable<Row> Search(
        HrDbContext db, string? search, int? departmentId, EmployeeKind? kind, EmployeeStatus? status)
    {
        var query = db.Employees.AsNoTracking();

        if (search is { } text)
        {
            query = query.Where(employee =>
                employee.NameEn.Contains(text)
                || employee.NameAr.Contains(text)
                || employee.EmployeeNo.Contains(text)
                || employee.NationalId.Contains(text)
                || (employee.Phone != null && employee.Phone.Contains(text)));
        }

        if (departmentId is { } wantedDepartment)
        {
            query = query.Where(employee => employee.DepartmentId == wantedDepartment);
        }

        if (kind is { } wantedKind)
        {
            query = query.Where(employee => employee.Kind == wantedKind);
        }

        if (status is { } wantedStatus)
        {
            query = query.Where(employee => employee.Status == wantedStatus);
        }

        return query
            .Join(db.Departments, employee => employee.DepartmentId, department => department.Id, (employee, department) => new { employee, department })
            .OrderBy(pair => pair.employee.NameEn).ThenBy(pair => pair.employee.Id)
            .Select(pair => new Row(pair.employee, pair.department));
    }
}
