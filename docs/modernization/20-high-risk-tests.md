# High-risk tests (brief Phase 9)

Date: 2026-10-16. New: `tests/AlMotaheda.Integration.Tests/HighRiskTests.cs` (2 SQL Server tests, real seeded roles).

## 1. The brief's list and where each item is proved

| Risk | Proved by | Notes |
|---|---|---|
| Can't pay an unapproved record | Finance `A_bank_payment_goes_submitted_approved_sent_paid_...`; Projects `A_payment_cannot_..._pay_an_unapproved_record`; **new** HR: a Submitted sheet can't ask for payment (409 `invalid_status`) | Every payee type |
| Can't edit a paid record | Finance `..._paid_and_final_requests_cannot_move`; **new** HR: a Paid sheet can't be returned, resubmitted, paid again or get new inputs | |
| An amount change after approval needs a new approval | Projects `A_returned_expense_is_resubmitted_with_its_old_review_cleared`; approved expenses change only through adjustments the Executive approves (`ExpenseAdjustmentTests`, `EquipmentCostAdjustmentTests`); payment requests are never edited (doc 08); **new** HR: the month is locked while submitted or approved; returned, its total changes and it must be approved again before it can be paid | **One gap, see §3** |
| Duplicate payment prevention (idempotency) | Projects / Subcontractors `The_same_idempotency_key_returns_the_first_request_...`; Finance unique indexes (`UX_PaymentRequest_IdempotencyKey`, `..._SourcePayment`); HR `A_month_..._is_paid_once`; **new**: the same salary form sent twice is one request, the ledger holds one payment | |
| Concurrency conflict (RowVersion) | `concurrency_conflict` tested in Projects, Workforce, Subcontractors, Fleet, Archive, Access, Finance, Procurement, Platform; **new** HR: approving with the Draft's row version → 409; Finance: paying again with the pre-payment row version → 409 | |
| Payroll calculation | Workforce `Overtime_is_hours_times_rate_over_eight_times_one_and_a_half`, `Allowances_add_and_deductions_subtract`, `Thirds_add_up_...`; HR `Net_is_basic_plus_allowance_plus_bonuses_minus_deductions_and_recoveries`; **new** on SQL Server: 9,000 + 1,000 + 500 − 300 = 10,200 per line and on the sheet | Employee overtime isn't built (HR open question 3) |
| Engineer can't see other projects | Projects `Records_of_one_project_never_show_on_another_and_an_outsider_gets_404_everywhere`; **new** with the real `engineer` role: only their project in the list, 404 on another project's screens, 403 on money screens their role never has | |
| Accountant can't edit employees | **new** with the real `accountant` role: 403 on add employee, edit employee, add salary | |
| HR can't pay | HR `The_hr_role_has_no_payments_access`; **new** with the real `hr` role: 403 on payment requests (list, approve, approve & pay, confirm), the ledger and the ledger report; and on project money | |
| Report totals equal the ledger | Integration `ChainTests` (5 chains); Reports `ReportFlowTests` | |
| Backup keeps exactly N verified backups | Platform `Retention_keeps_the_newest_verified_backups_and_never_counts_failed_ones` (N = 2, 3; failed and unverified never counted; the newest always stays); `BackupTests` on SQL Server (three real backups, the oldest removed with its documents zip when keeping two) | |
| Real SQL Server for constraints and transactions | Every module's `Database/` tests (Testcontainers, a fresh database per test); schema tests check the check constraints, unique indexes and "no cascade deletes" | |

## 2. What the new tests found

Nothing broken. Two expectations in the first draft of the test were wrong and were fixed in the test, not the code:
a returned sheet keeps the name of who returned it (history), and a money screen answers 403 to an Engineer before
the project check (their role never sees money anywhere).

## 3. Gap found (Needs Confirmation)

**Purchase order revisions.** Purchasing (`procurement` Edit) can revise an open order's quantities and unit prices
with a reason; a numbered revision keeps the old lines. A revision is capped by the requisition quantity and can't
change an invoiced price, but **it is not approved again**, even when it raises the price chosen by the Executive in
the quotation step. The brief's rule ("an amount change after approval invalidates the approval") suggests a price
increase should go back to the Executive. Options: (a) any price increase needs the Executive; (b) only above the
policy's price tolerance; (c) keep as built. Not changed: this is the owner's decision.

## 4. Results

See section "Tests" of `21-final-report.md`.
