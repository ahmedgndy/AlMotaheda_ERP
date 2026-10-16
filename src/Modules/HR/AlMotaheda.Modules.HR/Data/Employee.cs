namespace AlMotaheda.Modules.HR.Data;

/// <summary>A department of the company (accounts, engineering, administration…). Never deleted; deactivated instead.</summary>
internal sealed class Department
{
    public int Id { get; set; }

    public string NameAr { get; set; } = "";
    public string NameEn { get; set; } = "";

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// An employee on the monthly payroll: office employees, engineers and staff. Daily-paid workers and guards are in
/// the Workforce module. Never deleted: an employee who leaves is ended, and their history stays.
/// </summary>
/// <remarks>An employee is not a user account. <see cref="UserId"/> links the two when the employee also signs in.</remarks>
internal sealed class Employee
{
    public int Id { get; set; }

    /// <summary><c>EMP-000123</c>, computed by the database from the id.</summary>
    public string EmployeeNo { get; private set; } = "";

    public string NameAr { get; set; } = "";
    public string NameEn { get; set; } = "";

    /// <summary>The 14-digit Egyptian national id. Unique.</summary>
    public string NationalId { get; set; } = "";

    public string? Phone { get; set; }
    public string? Email { get; set; }

    public string JobTitle { get; set; } = "";

    public int DepartmentId { get; set; }

    public EmployeeKind Kind { get; set; }

    public DateOnly HireDate { get; set; }

    public EmployeeStatus Status { get; set; } = EmployeeStatus.Active;

    /// <summary>The last working day, set when the employee is ended.</summary>
    public DateOnly? EndDate { get; set; }
    public string? EndReason { get; set; }

    /// <summary>The employee's sign-in account, when they have one (Access module). One employee per account.</summary>
    public int? UserId { get; set; }

    /// <summary>Where the salary goes. Shown only with <c>hr</c> See money.</summary>
    public string? BankName { get; set; }
    public string? Iban { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];

    /// <summary>On the payroll of the month <paramref name="first"/> … <paramref name="last"/>: hired by its end, not ended before its start.</summary>
    public static bool WorksIn(DateOnly hireDate, DateOnly? endDate, DateOnly first, DateOnly last) =>
        hireDate <= last && (endDate is null || endDate >= first);
}

/// <summary>Stored by name.</summary>
internal enum EmployeeKind
{
    OfficeEmployee,
    Engineer,
    Staff,
}

/// <summary>Stored by name.</summary>
internal enum EmployeeStatus
{
    Active,
    Ended,
}

/// <summary>A document an employee gave HR (id card, contract, qualification…). Metadata only: the file is kept on paper for now.</summary>
internal sealed class EmployeeDocument
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }

    public EmployeeDocumentKind Kind { get; set; }

    public string? Number { get; set; }

    public DateOnly? IssueDate { get; set; }
    public DateOnly? ExpiryDate { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedBy { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Stored by name.</summary>
internal enum EmployeeDocumentKind
{
    NationalIdCard,
    Contract,
    Qualification,
    MilitaryStatus,
    SocialInsurance,
    Other,
}
