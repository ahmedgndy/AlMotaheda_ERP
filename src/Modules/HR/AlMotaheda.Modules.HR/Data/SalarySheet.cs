using AlMotaheda.BuildingBlocks.Domain;

namespace AlMotaheda.Modules.HR.Data;

/// <summary>
/// The salaries of one month: one line per employee on the payroll, copied from the salary structures and payroll
/// inputs when HR calculates it, so later changes never alter a prepared month.
/// </summary>
/// <remarks>
/// <para>HR prepares and submits it; the Accountant approves it or returns it with a reason (never the person who
/// submitted it); the Accountant then asks Finance to pay it as one payment request; Finance marks it Paid once the
/// money has left (docs/modernization/10-hr-design.md).</para>
/// <para>The status changes only through the methods below.</para>
/// </remarks>
internal sealed class SalarySheet
{
    private static readonly StatusFlow<SalarySheetStatus> Flow = new(
        (SalarySheetStatus.Draft, SalarySheetStatus.Submitted),
        (SalarySheetStatus.Returned, SalarySheetStatus.Submitted),
        (SalarySheetStatus.Submitted, SalarySheetStatus.Approved),
        (SalarySheetStatus.Submitted, SalarySheetStatus.Returned),
        (SalarySheetStatus.Approved, SalarySheetStatus.Paid));

    private SalarySheetStatus _status = SalarySheetStatus.Draft;

    public int Id { get; set; }

    /// <summary>The first day of the month.</summary>
    public DateOnly Month { get; set; }

    public SalarySheetStatus Status { get => _status; init => _status = value; }

    /// <summary>The sum of the lines' net salaries.</summary>
    public decimal Total { get; private set; }

    public int EmployeeCount { get; private set; }

    public DateTime CalculatedAt { get; private set; }
    public int CalculatedBy { get; private set; }

    public DateTime CreatedAt { get; set; }
    public int CreatedBy { get; set; }

    public DateTime? SubmittedAt { get; private set; }
    public int? SubmittedBy { get; private set; }

    public DateTime? ReviewedAt { get; private set; }
    public int? ReviewedBy { get; private set; }

    /// <summary>The Accountant's note on approval, or the reason a sheet was returned.</summary>
    public string? ReviewNote { get; private set; }

    /// <summary>Set when Finance confirmed the payment.</summary>
    public DateOnly? PaidOn { get; private set; }
    public int? PaymentRequestId { get; private set; }
    public DateTime? PaidAt { get; private set; }
    public int? PaidBy { get; private set; }

    public byte[] RowVersion { get; set; } = [];

    public List<SalarySheetLine> Lines { get; } = [];

    /// <summary>Lines can be (re)calculated while HR still holds the sheet.</summary>
    public bool IsEditable => Status is SalarySheetStatus.Draft or SalarySheetStatus.Returned;

    /// <summary>Replaces the lines with a new calculation. Only while <see cref="IsEditable"/>.</summary>
    public void Calculate(IEnumerable<SalarySheetLine> lines, int userId, DateTime now)
    {
        if (!IsEditable)
        {
            throw new InvalidOperationException($"A {Status} salary sheet can't be recalculated.");
        }

        Lines.Clear();
        Lines.AddRange(lines);
        Total = Lines.Sum(line => line.NetSalary);
        EmployeeCount = Lines.Count;
        CalculatedAt = now;
        CalculatedBy = userId;
    }

    public void Submit(int userId, DateTime now)
    {
        _status = Flow.Move(Status, SalarySheetStatus.Submitted);
        SubmittedAt = now;
        SubmittedBy = userId;
    }

    /// <summary>The Accountant's decision: approve, or return to HR with a reason.</summary>
    public void Review(bool approve, int userId, DateTime now, string? note)
    {
        _status = Flow.Move(Status, approve ? SalarySheetStatus.Approved : SalarySheetStatus.Returned);
        ReviewedAt = now;
        ReviewedBy = userId;
        ReviewNote = note;
    }

    /// <summary>Finance confirmed that the money left, under request <paramref name="requestId"/>.</summary>
    public void MarkPaid(int requestId, DateOnly paidOn, int confirmedBy, DateTime now)
    {
        _status = Flow.Move(Status, SalarySheetStatus.Paid);
        PaymentRequestId = requestId;
        PaidOn = paidOn;
        PaidAt = now;
        PaidBy = confirmedBy;
    }
}

/// <summary>Stored by name.</summary>
internal enum SalarySheetStatus
{
    Draft,
    Submitted,
    Returned,
    Approved,
    Paid,
}

/// <summary>One employee's salary in a sheet. A snapshot: names and amounts as they were when calculated.</summary>
internal sealed class SalarySheetLine
{
    public int Id { get; set; }

    public int SheetId { get; set; }

    public int EmployeeId { get; set; }

    public string EmployeeNo { get; set; } = "";
    public string NameAr { get; set; } = "";
    public string NameEn { get; set; } = "";
    public int DepartmentId { get; set; }

    public decimal BasicSalary { get; set; }
    public decimal FixedAllowance { get; set; }
    public decimal Bonuses { get; set; }
    public decimal Deductions { get; set; }
    public decimal AdvanceRecoveries { get; set; }

    /// <summary>Basic + allowance + bonuses − deductions − advance recoveries. Never negative.</summary>
    public decimal NetSalary { get; set; }
}
