# Treasury, custody, payroll funds, vehicles: audit (2026-10-20)

Base: `integration/e2e-qa` at `9ae0d68` (clean; the cost-management, financial-workspace and Price Control work is
committed there). Work branch: `feature/treasury-custody-fleet`. The Office module stays on `feature/office-management`
(its custody, cash counts and attachments are the pattern copied here for vehicles and accountant custody).

Classification: **working** (built and tested), **incomplete** (built, a rule or a screen is missing),
**backend-only**, **Flutter-only**, **missing**, **defective**.

## 1. Company treasury

| Capability | Status | Where | What this round does |
|---|---|---|---|
| Money movements per Bank / Cash | working | `finance.LedgerEntry` (single entry, account tags, signed, reversals) | kept; the one book every balance reconciles to |
| Opening balance, running balance per Bank / Cash | missing | — | `finance.TreasuryAccount` (seeded Bank and Cash; opening balance set once by an Executive); balance = opening + Σ ledger entries of that account |
| Internal transfer treasury → custody | incomplete | `CustodyFunding` payment request (project custody only, purpose `CustodyAdvance`) | kept for project custody; `CustodyTransfer` adds treasury → accountant / payroll batch / vehicle with a Requested → Approved → Disbursed → Received lifecycle, reservations and reversals |
| A transfer is never an expense | working (rule), incomplete (reports) | `PaymentPurposes.CustodyAdvance` is money out, not a cost; FIN-CASH counts it as "money out" with no distinction | ledger purposes `CustodyAdvance` / `CustodyReturn` are shown apart as internal transfers in FIN-CASH, FIN-TREASURY and FIN-CUSTODY |
| Payment from a custody (accountant pays a supplier in cash from money already drawn) | missing | every paid request posts to Bank or Cash | `PaymentRequest.SourceCustodyAccountId`: the ledger entry is tagged `Custody` (the treasury is unchanged, it already paid out at funding) and the custody account gets the outflow, once |

## 2. Cash custody

| Capability | Status | Where | What this round does |
|---|---|---|---|
| Engineer's project custody (fundings, returns, purchases, weekly payroll) | working | `projects.CustodyEntry`, `CustodyBalance.Compute`, `Projects:CustodyBalanceCheck` | reused as the engineer-project account; transfers in and out are written as `Funding` / `Return` entries with source `CustodyTransfer` |
| Accountant custody, payroll batch custody | missing | — | `finance.CustodyAccount` (kind, holder, parent, purpose) + append-only `finance.CustodyMovement` |
| Vehicle operating custody | missing | — | `fleet.VehicleCustodyEntry` (same pattern as the project custody), one per vehicle, accountable driver on every entry |
| Office petty cash | backend-only (other branch) | `feature/office-management` | untouched; a `CustodyAccount` of kind `Office` is reserved in the code list, not opened here |
| Hierarchy Executive → Accountant → Engineer → worker | missing | — | `CustodyAccount.ParentAccountId`; a transfer's source must be held or reviewed by the caller (resource check, not only role) |
| Pending exposure vs posted balance | working for project custody (payroll Pending), missing elsewhere | `CustodyEntryStatus.Pending` | vehicle claims hold a Pending entry; Finance accounts report posted, pending, reserved (open transfers out) and available |
| Cash count, adjustment with evidence | missing (Office branch has it) | — | `finance.CustodyCashCount` (never changes a balance) and an `Adjustment` movement with reason and evidence by a reviewer who is not the holder |
| Monthly period close | missing (Office branch has periods) | — | `finance.CustodyPeriodClose`: closing balance snapshot, entries kept, later movements dated inside a closed period refused; carry-forward policy `Finance:CustodyPeriodCarryForward` |
| Idempotency, locks, row versions, no self-approval | working | custody fundings, returns, settlements | the same on every new write |

## 3. Payroll fund distribution

| Capability | Status | Where | What this round does |
|---|---|---|---|
| Daily labor entitlement (approved − paid − held) | working | Workforce `SettlementPlan`, `PayrollSettlement` | exposed to Finance through `IPayrollEntitlementSource` (no second engine) |
| Salaried entitlement | working | HR `SalarySheet` (Approved total) | same contract, implemented by HR |
| Funding a named batch, distributing to engineers with an over-distribution refusal | missing | — | `finance.PayrollBatch` (+ allocation per project) with its own custody account; distributions are custody transfers validated against the allocation and the entitlement (409 `over_distribution`) |
| Marking workers paid only on the accountant's confirmation | working | `PayrollSettlement` confirm → `WorkerPayment`, custody Posted | reused unchanged; a transfer to an engineer never writes a payment |
| Reconciliation per batch | missing | — | `GET /api/payroll-batches/{id}` and report FIN-PAYROLL-BATCH: funded, distributed, paid, remaining per custodian, unpaid, returned |

## 4. Suppliers and subcontractors

| Capability | Status | Notes |
|---|---|---|
| Supplier invoices → requests → payments with allocations, never above outstanding | working | Projects `RecordSupplierPayment`, `SupplierPaymentSource`, Price Control gate |
| Subcontract payments within the contract value | working | Subcontractors `SubcontractPaymentSource` |
| Procurement invoice posted once (`IInvoicePosting`) | working | Projects `InvoicePostingContract` |
| Source custody account on a payment | missing | added as above; a transfer to the accountant is never a supplier expense (tested) |

## 5. Fleet

| Capability | Status | What this round does |
|---|---|---|
| Equipment register, project assignment, equipment costs (fuel = litres × price) | working | kept; a vehicle is an `Equipment` row (category `Vehicle`) with a `fleet.Vehicle` extension (plate, odometer, GPS status): one register |
| Vehicles, drivers, driver assignment history | missing | `fleet.Vehicle`, `fleet.VehicleDriverAssignment` (driver = `hr.Employee`, overlap-checked, handover odometer) |
| Vehicle custody | missing | `fleet.VehicleCustodyEntry`; funded by a custody transfer to the vehicle account; expenses on one vehicle never touch another |
| Fuel / vehicle expense claims with odometer and invoice evidence | missing | `fleet.VehicleExpense` + `fleet.FleetAttachment` (photos / PDF, type from the bytes, size limits, private download, never replaced once approved); Submitted → Approved (posted once) / Rejected / EvidenceRequested / Escalated; never the submitter; Fuel category through Price Control |
| Duplicate and plausibility flags | missing | `fleet.VehicleExpenseFlag` rows (same invoice no., same receipt hash, same vehicle + amount + day, odometer not increasing / implausible distance): review flags, never blocks |
| Violations → HR | missing | `fleet.VehicleViolation` with the driver from the assignment history; `hr.HrReferral`; HR closes with or without a `PayrollInput` Deduction through the existing HR flow |
| Vehicle GPS | missing | `IVehicleLocationProvider` + `NullVehicleLocationProvider`; endpoints answer `providerStatus: NotConfigured` with no points; retention setting `Fleet:Gps:RetentionDays`; site-location pings of users are not reused |

## 6. People and assignments

| Capability | Status | Notes |
|---|---|---|
| Employees (HR), workers (Workforce), app users (Access) | working | no new person table; a driver is an `hr.Employee` |
| Employee ↔ project for salaried staff without a login | missing | `hr.EmployeeAssignment` (dated, role, history) + "who was assigned on a date" query |

## 7. Platform, reports, Flutter

| Capability | Status | Notes |
|---|---|---|
| Notifications, live scopes, work inbox, reports | working | rules added for transfers, claims, violations, referrals; new reports in Finance, Fleet and HR |
| Flutter screens for any of the above | missing | listed in the delivery report for the navigation-redesign branch |

## 8. Defects found

- `GET /api/ledger` and the FIN reports join every entry to a payment request; an entry from a custody transfer has
  none. Fixed by a left join (the ledger entry now carries either a request or a transfer).
- FIN-CASH reported custody advances as plain money out, indistinguishable from payments. Fixed (internal transfers
  shown apart).
- No other defect found in the existing custody, settlement or payment flows; their tests are unchanged.
