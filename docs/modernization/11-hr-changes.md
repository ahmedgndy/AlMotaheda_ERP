# HR step — employees, salaries, monthly salary sheet (brief Phase 3)

Date: 2026-10-16. Design: `10-hr-design.md`. Permissions: `03-permission-matrix.md`.

## 1. Added

| Added | Why |
|---|---|
| Module `AlMotaheda.Modules.HR` (schema `hr`), between Archive and Finance in `ModuleCatalog` | Office employees, engineers and staff are paid monthly; Workforce keeps daily workers and guards |
| Role `hr` (id 7) and permission module `hr` (ids 86–90), appended | HR prepares, the Accountant reviews, nobody in HR touches payments |
| Tables `Department`, `Employee` (`EMP-000123`), `EmployeeDocument`, `SalaryStructure`, `PayrollInput`, `SalarySheet`, `SalarySheetLine` | See design §5 |
| `SalaryCalculator` (pure) | `net = basic + allowance + bonuses − deductions − advance recoveries` |
| `SalarySheetPaymentSource` (`IPaymentSource`) | Finance marks the sheet Paid when the money left, in its transaction |
| Finance codes: payee type `Employees`, purpose `Salaries`, source type `SalarySheetPayment` | One request pays the whole month; no project (company overhead) |
| `IPaymentRequests.HeldForPayeesAsync` | "Is this sheet already in an open request?" for requests without lines |
| Notifications: the person who submitted a sheet hears when it is approved or returned | Same rules as other reviews |

## 2. Endpoints (all under `/api/hr`, all need a signed-in user)

| Endpoint | Permission | Notes |
|---|---|---|
| `GET /departments`, `POST`, `PUT /{id}` | hr V / C / E | Names unique (409 `department_exists`) |
| `GET /employees` (search, departmentId, kind, status, paging), `GET /{id}` | hr V | `bankName`, `iban` are `null` without hr F |
| `POST /employees`, `PUT /employees/{id}` | hr C / E | 409 `national_id_exists`, `user_already_linked`, `department_inactive`, `employee_ended`; 422 `unknown_user` |
| `POST /employees/{id}/end` `{ endDate, reason, rowVersion }` | hr E | Never deleted |
| `GET /employees/{id}/documents`, `POST`; `PUT /employee-documents/{id}` | hr V / C / E | Metadata only |
| `GET /employees/{id}/salaries`, `POST` `{ effectiveFrom, basicSalary, fixedAllowance?, note? }` | hr V+F / E+F | Versions never change; 409 `salary_version_exists`, `month_locked` |
| `GET /payroll-inputs?month=yyyy-MM`, `POST` `{ employeeId, month, kind, amount, reason }`, `POST /payroll-inputs/{id}/cancel` | hr V+F / C+F / E+F | `kind`: `Bonus`, `Deduction`, `AdvanceRecovery`; 409 `month_locked` |
| `GET /salary-sheets`, `GET /salary-sheets/{id}` | hr V | Amounts `null` without hr F |
| `POST /salary-sheets` `{ month }` | hr C+F | 409 `sheet_exists`, `missing_salary`, `negative_net`, `empty_sheet` |
| `POST /salary-sheets/{id}/recalculate`, `/submit` | hr E+F | Submit recalculates one last time |
| `POST /salary-sheets/{id}/approve` `{ note? }`, `/return` `{ reason }` | hr A+F | 409 `own_record` for the submitter |
| `POST /salary-sheets/{id}/payment-request` `{ requestedDate, method, reference?, rowVersion }` | payments C + hr V | 201 with the Finance request; 409 `payment_requested`; same form again → 200 |

Status flow: Draft → Submitted → Approved or Returned; Returned → Submitted; Approved → Paid (Finance only).
A month is **locked** once its sheet is Submitted, Approved or Paid: no inputs added or cancelled, no salary version
starting in it.

## 3. Migrations

| Module | Migration |
|---|---|
| HR | `InitialHr` (7 tables, 18 check constraints, unique national id / employee number / month / salary date) |
| Access | `AddHrRole` (role 7, permissions 86–90, 12 role defaults: Executive VCEAF, Accountant VAF, HR VCEF) |
| Finance | `SalaryPayments` (the three check constraints accept the new codes) |

**Order:** Platform, Access, Projects, Workforce, Subcontractors, Fleet, Archive, **HR**, Finance.

## 4. Tests

New project `AlMotaheda.Modules.HR.Tests` (99, all passing): calculator and status flow, schema and SQL translation, every
endpoint's 401/403 per permission, validation, and two SQL Server flows (employees → salaries → sheet → returned →
approved → payment request → approve & pay → Paid + ledger; missing salary, own-record review, ended employee).
Access, Finance and Api tests updated for 7 roles, 90 permissions, the new migrations and module order.

## 5. Frontend contract changes

1. New role `hr` in user management; new module `hr` in the permission screens.
2. New menu **HR**: Employees (with documents and salary history), Payroll inputs, Salary sheets — shown with `hr` View.
3. Salary sheet screen: buttons by status and permission (Recalculate / Submit for hr E; Approve / Return for hr A;
   "Request payment" for payments C on an Approved sheet). Its payment requests: Finance list filtered by
   `payeeType=Employees&payeeId={sheetId}`.
4. Payment requests and ledger: new payee type `Employees`, purpose `Salaries`; such requests have `projectId: null`
   and are seen only by company-wide roles.

## 6. Open questions (Needs Confirmation)

1. Are engineers' salaries charged to their projects, or company overhead (built as overhead)?
2. Partial months: pay the full month (built) or prorate by days?
3. Leave balances, attendance-based deductions, employee overtime, social insurance and income tax: not built.
4. Paying an advance before payroll (money out): not built; HR records only its recovery.
5. Employee document files: metadata only; where should the files live (Archive is per project)?
6. A paid salary sheet can't be voided or corrected yet. Should corrections go into the next month's inputs?
7. National id is required and must be 14 digits (Egyptian). Are there non-Egyptian employees (passport instead)?

Results, run one project at a time: Api 33, Access 174, Finance 57, Platform 38, HR 99 — all pass.
