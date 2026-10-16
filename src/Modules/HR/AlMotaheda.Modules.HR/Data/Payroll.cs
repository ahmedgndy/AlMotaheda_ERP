namespace AlMotaheda.Modules.HR.Data;

/// <summary>
/// An employee's monthly salary from a date on: basic salary plus one fixed allowance. A raise adds a new version;
/// versions are never changed, so every past month can be explained.
/// </summary>
internal sealed class SalaryStructure
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }

    /// <summary>The first day this salary applies. A month uses the version in force on its first day.</summary>
    public DateOnly EffectiveFrom { get; set; }

    public decimal BasicSalary { get; set; }

    /// <summary>Zero when none.</summary>
    public decimal FixedAllowance { get; set; }

    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }
}

/// <summary>A one-off amount for one employee in one month: a bonus, a deduction, or the recovery of an advance.</summary>
internal sealed class PayrollInput
{
    public int Id { get; set; }

    public int EmployeeId { get; set; }

    /// <summary>The first day of the month the amount belongs to.</summary>
    public DateOnly Month { get; set; }

    public PayrollInputKind Kind { get; set; }

    /// <summary>Always positive; <see cref="Kind"/> says whether it adds or takes away.</summary>
    public decimal Amount { get; set; }

    public string Reason { get; set; } = "";

    public PayrollInputStatus Status { get; set; } = PayrollInputStatus.Active;

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }

    public DateTime? CancelledAt { get; set; }
    public int? CancelledBy { get; set; }
    public string? CancelReason { get; set; }

    public byte[] RowVersion { get; set; } = [];
}

/// <summary>Stored by name.</summary>
internal enum PayrollInputKind
{
    Bonus,
    Deduction,
    AdvanceRecovery,
}

/// <summary>Stored by name.</summary>
internal enum PayrollInputStatus
{
    Active,
    Cancelled,
}
