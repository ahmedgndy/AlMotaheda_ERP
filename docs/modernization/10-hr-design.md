# HR — design (brief Phase 3)

Date: 2026-10-16. Labels: **[Company]** from the brief · **[Industry]** recommendation · **[Confirm]** Needs Confirmation.

## 1. What HR owns and what it doesn't

| Owner | What | Rule |
|---|---|---|
| HR (`hr` C/E) | Employees (office staff, engineers, staff), job title, department, hiring date, status; document metadata; salary structure; bonuses, deductions and advance recoveries; preparing the monthly salary sheet | [Company] |
| Accountant (`hr` V A F, no C/E) | Reviews the sheet (approve, or return with a reason); creates the salary payment request (`payments` C) | [Company]: can't edit employee data |
| Executive | Decides the payment request in Finance (reject / approve / approve & pay), as for every payment | [Company] unchanged flow |
| Nobody in HR | Payments: HR has no `payments` or `finance` access | [Company] |
| Workforce | Daily-paid workers and guards stay there (sheets, weekly pay) | [Company] |

There is no employee data today (only sign-in accounts), so nothing moves: HR starts empty. An employee can be
linked to a user account (optional), but they are separate things: not every employee signs in, and a user is not
an employee record.

## 2. Salary flow

```
HR prepares the month (Draft, lines calculated) → HR submits → Accountant approves or returns (with reason)
   → Accountant creates the payment request (Finance, one request for the whole sheet)
   → Executive rejects / approves / approves & pays → bank → confirmed → sheet Paid, ledger posted
```

- **One request per sheet** (the brief: "approve & pay the salary batch"). Finance gains a payee type `Employees`
  (the sheet), a purpose `Salaries`, and lines without a project.
- Salaries are **company overhead** in the ledger (no project) **[Confirm]**: whether engineers' salaries are
  charged to their projects (open question 6) had no proposal.
- The sheet's lines are **snapshots**: the salary structure and inputs at preparation time are copied with their
  amounts, so later changes never alter a prepared month [Company: historical values preserved].
- A sheet can't be requested twice while an open Finance request holds it; a rejected or cancelled request frees it.
  Its requests are listed by Finance (`GET /api/payment-requests?payeeType=Employees&payeeId={sheetId}`). Paid is
  written by Finance's confirmation, in the same transaction.

## 3. Calculation (pure, unit-tested)

`net = basic + fixed allowance + bonuses − deductions − advance recoveries`, per employee, to the piastre.

| Rule | Label |
|---|---|
| Salary structure = basic + one fixed monthly allowance, versioned by `effectiveFrom`; the month uses the version in force on its first day | [Industry] |
| Employees in a month: hired on or before its last day, and not ended before its first day | [Industry] |
| **No proration** for a partial first or last month | **[Confirm]** |
| A net below zero is refused at preparation (fix the inputs) | [Industry] |
| Leave, attendance-based deductions, employee overtime, social insurance, income tax | **[Confirm]** — not built |
| Paying an advance itself (money out before payroll) | **[Confirm]** — not built; HR records the recovery only |

## 4. Statuses

| Record | Flow |
|---|---|
| Employee | Active → Ended (with end date and reason). Never deleted |
| Salary sheet | Draft → Submitted → Approved / Returned; Returned → Submitted (after recalculation); Approved → Paid (Finance) |
| Payroll input | Active → Cancelled (only while its month's sheet isn't submitted or approved) |

## 5. Data and security

- Schema `hr`. Unique: employee national id (required for employees [Industry]: needed for contracts and payroll),
  employee number `EMP-000123`, one sheet per month, one salary version per employee and date.
- Salary amounts (structures, inputs, sheet lines and totals) need `hr` See money: only HR, Accountant, Executive.
- Bank account (IBAN) of an employee: stored, shown only with `hr` See money [Industry]; the real bank integration
  will use it.
- Documents: metadata only (kind, number, issue and expiry date). Storing the files themselves **[Confirm]** (the
  archive is per project today).

## 6. Permissions and navigation

New role `hr` and permission module `hr` (see `03-permission-matrix.md`): HR `VCEF`, Accountant `VAF`,
Executive all. `GET /api/me` returns them; the app shows HR → Employees / Salary sheets with `hr` View.

## 7. Endpoints

`/api/hr/departments` (list, create, update) · `/api/hr/employees` (list, get, create, update, end) ·
`/api/hr/employees/{id}/documents` (list, add) + `/api/hr/employee-documents/{id}` (update) ·
`/api/hr/employees/{id}/salaries` (history, new version) · `/api/hr/payroll-inputs` (list, add, cancel) ·
`/api/hr/salary-sheets` (list, get, prepare, recalculate, submit, approve, return, payment-request).
