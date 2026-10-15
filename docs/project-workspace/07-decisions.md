# Decisions taken, open business questions, and what is not built

## 1. Decisions taken (with the reason)

| # | Decision | Why |
|---|---|---|
| G1 | The Project Domain Analysis decision D1 ("no money is stored in this version") is replaced for the workspace: cost records, budgets and payments are now stored. The `Project` table itself still has no money columns | The workspace requirement needs them. Budget is a versioned table, not a column |
| G2 | Only **approved** records are cost. The prototype also counts unreviewed supplies | The requirement says a requested expense is not an approved expense; the prototype's own labels say "reviewed and approved only" |
| G3 | The ERD's Finance spine (SourceDocument, Payable, PaymentRequest, bank attempts, ledger: 21 tables) is **not** built. Payments are recorded as facts next to the records they settle (`SupplierPayment`, `WorkerPayment`, `SubPayment`, each with an allocation table and real foreign keys) | The ERD lists six unconfirmed assumptions about exactly this area (payroll runs, cash or bank, retention, VAT, book of record). The interim tables keep the ERD's invariants (approved amounts never change, a payment is a separate row, allocations have foreign keys, outstanding is calculated, idempotency keys) and can be moved under Finance later |
| G4 | Recording a payment needs `payments` Create; voiding needs `payments` Approve. One rule for suppliers, workers and subcontractors | "Payments" is the permission module of Transaction Management. By default: Accountant and Executive record, only the Executive voids |
| G5 | Calculations run in pure classes over flat rows loaded per project, not in SQL aggregates | One rule shared by the table and the details; testable to the pound without a database. If a project grows very large, the next step is SQL aggregation for fully paid and unpaid rows, not a stored total |
| G6 | Supplies and site expenses are one table, `ProjectExpense`, replacing the ERD's `SiteExpense` | The ERD has no table for supplier deliveries; the prototype keeps both in one collection with one workflow |
| I1 | Project items have no automatic linking rules (the prototype's optional "auto link by material, trade or work type") | The requirement describes an explicit, authorized, saved link. A stored link can be audited; a rule evaluated at read time can't |
| I2 | A supplier record belongs to one item. Labor can be split across items | Same as the prototype |
| B1 | A budget is approved by an Executive only | No rule exists. Budget approval is a management decision; this is the narrowest safe default |
| D2 | Reviewing a daily report does not create subcontract measurements (the prototype does) | The requirement forbids duplicating cost through reports. Measured work is recorded once, in Subcontractors, with its own approval |
| W1 | One labor sheet per engineer, project, day and shift (the ERD says one per project and day) | Two site engineers on one site each record their own crew. A worker can still be recorded only once per day and shift |
| W2 | A worker payment can't exceed the worker's approved unpaid pay (no worker advances) | The ERD has advances for salaried employees only |
| W3 | Daily wage only; days are 1 or 0.5 (ERD). Overtime = hours × rate ÷ 8 × 1.5, to the piastre | The prototype has hourly and weekly wages and rounds overtime to whole pounds; the ERD doesn't |
| S1 | Measured work is `Recorded` then `Approved` by someone else (the prototype saves it as approved at once) | Approved amounts become cost; the requirement separates recorded from approved |
| S2 | Retention is stored on the contract and shown on the contract account (`retentionHeld`, `dueNow`). It is not deducted from an item's outstanding amount | The ERD marks retention release as "confirm with the accountant" |
| S3 | Paying before work exists is allowed for subcontractors and kept as an unapplied advance | Advances are a payment purpose in the prototype. An advance is never a cost |
| F1 | Equipment costs are not split over project items | The prototype's items table has suppliers and labor only; no allocation rule exists for equipment |
| F2 | A `Credit` equipment cost is settled by one full payment. A void empties the payment and the cost can be paid again; every voided payment is first copied whole, with who voided it, when and why, to `fleet.VoidedEquipmentCostPayment` (insert only) | No partial payment in the prototype; avoids a fourth payment table before Finance exists. Refusing a new payment after a void was rejected: an approved cost can't be undone, so recording it again would count it twice, and the history table keeps "payments are never edited or deleted" |
| F3 | A supply or site expense (Projects) from a supplier of kind `Fuel` or `Equipment` is refused with 409 `use_equipment_costs` | Equipment fuel, rental and maintenance are recorded only in Site equipment (Fleet); the same cost in both modules would be counted twice. Project expenses already have no equipment category |
| A1 | Archive permissions reuse the `projects` permission module | There is no documents permission module among the 17 seeded ones |

## 2. Open business questions (need an answer from the company)

1. **Payment approval chain.** The prototype sends bank payments through request → accountant review →
   executive approval → bank. Here a user with `payments` Create records a payment that already happened.
   Should a payment need a second person's approval before it counts?
2. **Custody funds.** Custody-paid records count as paid when approved. Fund balances, top-ups and the custody
   review queue are not built. Are they needed in this system?
3. **Undo of an approval.** An approved record can't be changed or reversed yet. Who may reverse one, and should
   it be a reversal row (ERD) or a status?
4. **Weekly payroll.** One run per week for the company (ERD) or payments per worker as now?
5. **Retention and advances for subcontractors.** When is retention released? How is an advance recovered on
   each certificate?
6. **Who approves subcontract work.** Only roles with `subcontractors` Approve can (by default the Executive).
   The prototype also lets the Accountant approve manpower lines. Grant the permission to the Accountant role?
7. **Who records equipment costs.** `equipment` Create or Edit (Executive, Manager by default). Should site
   engineers record equipment fuel?
8. **VAT and withholding tax** are not modelled anywhere.
9. **Contract value, client certificates and money in** are not built, so the overview has no "certified",
   "received" or "margin".
10. **Opening balances** ("before the system") are not built.
11. **Budget approval by its author.** An Executive may approve a budget they drafted (B1 has no "own record"
    rule, unlike every other approval). Fine with one Executive; should a second person be required?

## 2a. Known technical limits found in the final review (accepted for now)

- Archive document links and daily-report links to subcontracts and equipment are checked for shape only, not
  that the target record belongs to the same project. Nothing leaks (every list is project-scoped), but a wrong
  link can be stored. Fix: small "exists on this project" contracts per module.
- Inline PDF preview is served with `Content-Security-Policy: sandbox`. Some browser PDF viewers refuse that.
  Test in the target browsers; if blocked, drop `sandbox` for PDFs only or preview with pdf.js in the client.
- The race "item deleted while a record is linked to it" answers 409 `item_removed`; it has no automated test
  (it needs two real SQL Server sessions).
- The voided equipment payment history is stored but not yet returned by the API.

## 3. Not built in this round

- The Flutter client (this round is backend only).
- Finance: payment requests, bank integration, ledger, reconciliation, client certificates and receipts.
- Custody funds, vehicles, fuel requests and reports, GPS tracking, staff salaries, office expenses.
- Staff (engineer) site attendance and the guard roster.
- Automatic item-linking rules; worker hourly and weekly wages; corrections of an approved labor sheet.
- Subcontract certificates (مستخلصات) and price versions of contract items.
- Notifications, the platform audit-log table (changes are in the structured log), exports.
