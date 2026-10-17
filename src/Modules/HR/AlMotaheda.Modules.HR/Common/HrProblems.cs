using AlMotaheda.BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlMotaheda.Modules.HR.Common;

/// <summary>The HR module's error responses. Shared codes come from <see cref="ApiProblem"/>.</summary>
internal static class HrProblems
{
    public const string DepartmentExists = "department_exists";
    public const string DepartmentInactive = "department_inactive";
    public const string NationalIdTaken = "national_id_exists";
    public const string UserAlreadyLinked = "user_already_linked";
    public const string UnknownUser = "unknown_user";
    public const string EmployeeEnded = "employee_ended";
    public const string SalaryVersionExists = "salary_version_exists";
    public const string MonthLocked = "month_locked";
    public const string SheetExists = "sheet_exists";
    public const string MissingSalary = "missing_salary";
    public const string NegativeNet = "negative_net";
    public const string EmptySheet = "empty_sheet";
    public const string InvalidStatus = "invalid_status";
    public const string OwnRecord = "own_record";
    public const string PaymentRequested = "payment_requested";
    public const string IdempotencyKeyReused = "idempotency_key_reused";
    public const string Busy = "busy";
    public const string DaysOverMonthCode = "days_over_month";
    public const string DaysPendingCode = "days_pending";
    public const string AllocationPolicyNotSetCode = "allocation_policy_not_set";

    public static ValidationProblem Validation(ValidationErrors errors) => ApiProblem.Validation(errors.ToDictionary());

    public static ProblemHttpResult NotFound(string what) => ApiProblem.NotFoundResult($"The {what} was not found.");

    public static ProblemHttpResult DepartmentNotFound() => NotFound("department");
    public static ProblemHttpResult EmployeeNotFound() => NotFound("employee");
    public static ProblemHttpResult DocumentNotFound() => NotFound("document");
    public static ProblemHttpResult InputNotFound() => NotFound("payroll input");
    public static ProblemHttpResult SheetNotFound() => NotFound("salary sheet");

    public static ProblemHttpResult DepartmentTaken() =>
        Conflict(DepartmentExists, "A department with this Arabic or English name already exists.");

    public static ProblemHttpResult InactiveDepartment() =>
        Conflict(DepartmentInactive, "The department is inactive. Choose an active department.");

    public static ProblemHttpResult NationalIdExists() =>
        Conflict(NationalIdTaken, "An employee with this national id already exists.");

    public static ProblemHttpResult UserLinked() =>
        Conflict(UserAlreadyLinked, "This user account is already linked to another employee.");

    public static ProblemHttpResult UserUnknown() =>
        ApiProblem.Create(StatusCodes.Status422UnprocessableEntity, UnknownUser, "The user account doesn't exist.");

    public static ProblemHttpResult Ended() =>
        Conflict(EmployeeEnded, "The employee has ended. Their history can be read but not changed.");

    public static ProblemHttpResult SalaryVersionTaken() =>
        Conflict(SalaryVersionExists, "The employee already has a salary starting on this date. Salaries are never changed: use another date.");

    public static ProblemHttpResult Locked(string month) =>
        Conflict(MonthLocked, $"The salary sheet of {month} is submitted, approved or paid, so its amounts can't change. The Accountant can return it.");

    public static ProblemHttpResult SheetTaken() =>
        Conflict(SheetExists, "This month already has a salary sheet. Recalculate it instead.");

    public static ProblemHttpResult NoSalary(IEnumerable<string> employeeNumbers) =>
        Conflict(MissingSalary, $"These employees have no salary in force on the first day of the month: {string.Join(", ", employeeNumbers)}.");

    public static ProblemHttpResult BelowZero(IEnumerable<string> employeeNumbers) =>
        Conflict(NegativeNet, $"The deductions are more than the salary of: {string.Join(", ", employeeNumbers)}. Fix the payroll inputs.");

    public static ProblemHttpResult Empty() =>
        Conflict(EmptySheet, "No employee is on the payroll of this month.");

    public static ProblemHttpResult WrongStatus(string title) => Conflict(InvalidStatus, title);

    public static ProblemHttpResult ReviewerIsSubmitter() =>
        Conflict(OwnRecord, "You submitted this salary sheet, so someone else must review it.");

    public static ProblemHttpResult AlreadyRequested() =>
        Conflict(PaymentRequested, "A payment request for this salary sheet is already waiting.");

    public static ProblemHttpResult KeyUsedElsewhere() =>
        Conflict(IdempotencyKeyReused, "This form was already sent with different details. Reload and try again.");

    public static ProblemHttpResult ReviewerIsRecorder() =>
        Conflict(OwnRecord, "You recorded these days, so someone else must review them.");

    public static ProblemHttpResult DaysOverMonth(int limit) =>
        Conflict(DaysOverMonthCode, $"The employee's days on all projects in this month would be more than the month's {limit} working days.");

    public static ProblemHttpResult DaysPending() =>
        Conflict(DaysPendingCode, "These days are already waiting for review for this employee, project and month. Fix that entry instead.");

    public static ProblemHttpResult AllocationPolicyNotSet() =>
        Conflict(AllocationPolicyNotSetCode, "The working days per month are not set yet. An Executive must set them before salaries are charged to projects.");

    public static ProblemHttpResult TryAgainLater() =>
        ApiProblem.Create(StatusCodes.Status503ServiceUnavailable, Busy, "The payroll is being changed by someone else. Try again in a moment.");

    private static ProblemHttpResult Conflict(string code, string title) => ApiProblem.Create(StatusCodes.Status409Conflict, code, title);
}
