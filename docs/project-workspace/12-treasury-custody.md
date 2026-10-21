# Treasury, custody hierarchy, payroll funding, vehicles and vehicle expenses

Modules `AlMotaheda.Modules.Finance` (schema `finance`, permission modules `finance`, `custody`, `payments`),
`AlMotaheda.Modules.Fleet` (schema `fleet`, permission modules `fleet`, `fuel`, `equipment`) and
`AlMotaheda.Modules.HR` (schema `hr`, permission module `hr`). Read `00-overview.md` first; the audit that led
here is `11-treasury-custody-audit.md`; the decisions are T1 to T9 in `07-decisions.md`. Backend only: the Flutter
screens this needs are listed at the end.

## 1. Design in one page

**One registry, several ledgers.** Finance keeps the registry of every custody account (`CustodyAccount`) and the
spine of every movement of cash between them (`CustodyTransfer`), but each module keeps the ledger it already had:

| Kind | Holder | Reference | Ledger (balance read from) |
|---|---|---|---|
| `Accountant` | a user | – | `finance.CustodyMovement` |
| `PayrollBatch` | the batch's custodian | the `PayrollBatch` | `finance.CustodyMovement` |
| `EngineerProject` | the site engineer | the project | `projects.CustodyEntry` (the existing project custody) |
| `Vehicle` | – (the driver of the day is accountable) | the vehicle | `fleet.VehicleCustodyEntry` |
| `Office` | the office custodian (from the Office record) | the `OfficeCustody` | `office.OfficeLedgerEntry` (the Office module's ledger; registered after the Office merge, T17) |

A transfer is a **linked pair of movements**: the source side is written when the cash leaves (`Disbursed`), the
destination side when the holder confirms it is in hand (`Received`). Finance writes its own ledger directly and the
other module's ledger through `ICustodyLedgerSource` (`ReceiveAsync` / `WithdrawAsync`) **in the same transaction**
(`SharedTransaction`), so a project custody funded by a transfer and the Finance account can never disagree. A
transfer is never an expense: its ledger entries have purpose `CustodyAdvance` or `CustodyReturn`, and the cash
movement report keeps them in their own columns.

**The treasury** is two rows in `TreasuryAccount` (Bank id 1, Cash id 2, seeded). The opening balance is set once by
an Executive; the running balance is never stored: it is the opening balance plus every `LedgerEntry` of that account
from the opening date. Reserved = approved transfers not yet disbursed + open payment requests; available = balance −
reserved.

**Balances move only on disbursement and receipt.** Requested and approved transfers reserve; cancelled and
rejected ones free the reservation; a reversal writes opposite movements pointing at the originals (append-only).

## 2. Tables

### Finance (`finance`)

- `TreasuryAccount`: `Account` (`Bank`, `Cash`, unique), names, `OpeningBalance`, `OpeningDate`, `OpeningSetAt/By`
  (set once: 409 `opening_balance_set`), `Notes`, `RowVersion`.
- `CustodyAccount`: `Number` (`CA-000001`, computed), `Kind`, `HolderUserId` (FK `access.User`, by hand),
  `ReferenceId`, `ParentAccountId` (self FK), names, `Status` (`Active`, `Closed`), `OpenedAt/By`, `ClosedAt/By`,
  `CloseReason`. One active account per (kind, holder, reference) (`UX_CustodyAccount_Active`, filtered).
- `CustodyMovement` (append only, Finance-ledgered kinds only): `AccountId`, `EntryDate`, `Kind` (`TransferIn`,
  `TransferOut`, `Payment`, `Adjustment`, `Reversal`…), `Status` (`Pending`, `Posted`, `Released`), signed `Amount`
  (≠ 0; pending only for outflows), `SourceType` + `SourceId` (unique per account: a source is ledgered once),
  `TransferId`, `PaymentRequestId`, `ReversesMovementId` (unique), `Reason`, `Reference`, who and when.
- `CustodyTransfer`: `Number` (`CT-`), `Status`, `Purpose` (`CustodyAdvance`, `CustodyReturn`), source and
  destination (`SourceKind` `Treasury`/`Custody`, `SourceTreasuryAccount`, `SourceAccountId`, same for the
  destination), `Amount` > 0, `PayrollBatchId`, `ProjectId` (copied from the project custody it funds), `Reference`,
  `Notes`, `EvidenceDocumentId`, `EvidenceReference`, `IdempotencyKey` (unique), the request, decision,
  disbursement, receipt and close columns, `RowVersion`. `CustodyTransferEvent` is its history,
  `CustodyTransferAlert` the rows that notify the Executives of a request.
- `PayrollBatch`: `Number` (`PB-`), `Kind` (`DailyLabor`, `Salaries`), `PeriodFrom/To`, `SalarySheetId`,
  `AccountId` (its own `PayrollBatch` custody account, unique), `HolderUserId`, names, `Status` (`Open`, `Closed`),
  close columns; `PayrollBatchAllocation` (`BatchId`, `ProjectId` FK `projects.Project` by hand, `Amount`,
  `EntitlementAtAllocation`), unique per batch and project.
- `CustodyCashCount`: `AccountId`, `CountDate`, `CountedAmount`, `BookBalance`, `Difference` (check: counted −
  book), notes, evidence, `IdempotencyKey`. **Changes nothing.**
- `CustodyAdjustment`: `AccountId`, `CashCountId`, `EntryDate`, signed `Amount`, `Reason`, `EvidenceReference`
  (required), `IdempotencyKey`; it writes one `CustodyMovement` (or the owning module's adjustment entry).
- `CustodyPeriodClose`: `AccountId`, `PeriodEnd` (unique per account), `ClosingBalance`, `PendingAtClose`, notes.
- `LedgerEntry` gained `CustodyTransferId`, `CustodyAccountId` and the account `Custody`; `PaymentRequestId` is
  now nullable with `CK_LedgerEntry_Origin` (a request or a transfer, never neither). `PaymentRequest` gained
  `SourceCustodyAccountId` (cash only): a payment paid out of a custody is a `Custody` ledger entry and a
  `Payment` movement on that account; the treasury is unchanged.

### Fleet (`fleet`)

- `Vehicle` (`Id` = the `Equipment` row of category `Vehicle`, one register row): `Plate` (unique), `Type`,
  `ModelYear`, `FuelType`, `OdometerKm` (never decreases), `OdometerUpdatedAt`, `GpsProvider`, `GpsDeviceId`,
  `GpsStatus` (`NotConfigured`, `Configured`, `Unavailable`), notes.
- `VehicleDriverAssignment`: `VehicleId`, `DriverEmployeeId` (FK `hr.Employee`, by hand), `StartDate`, `EndDate`,
  start and end odometer, `HandoverNote`, `HandoverCustodyBalance`, `HandoverPendingClaims`, who and when. One open
  driver per vehicle (`UX_VehicleDriverAssignment_Open`); a new stay never starts before the last one ended.
- `VehicleCustodyEntry` (append only): `VehicleId`, `DriverEmployeeId` (the accountable driver on the entry date,
  from the history), `Kind` (`Funding`, `Return`, `Expense`, `AdjustmentIn`, `AdjustmentOut`), `Amount` > 0,
  `EntryDate`, `Status` (`Pending` only for an expense, `Posted`, `Released`), `SourceType` + `SourceId` (unique),
  reference, notes.
- `VehicleExpense` (`VX-`): vehicle, driver, driver assignment, `ProjectId` (the vehicle's project on the expense
  date, if any), `Type` (`Fuel`, `Maintenance`, `Repair`, `Toll`, `Parking`, `Wash`, `Other`), `ExpenseDate`,
  `OdometerKm`, `FuelQuantity`, `UnitPrice`, `Amount` (fuel = litres × price, rounded once), vendor, supplier,
  invoice no., notes, the price-control columns (`PriceItemId`, `PriceCheck`, `ReferenceMaxPrice`,
  `PriceJustification`, `PriceExceptionId`), GPS point typed by the app, `Status`, `Round`, `PostingStatus`,
  `CustodyEntryId` (unique), posting and reversal columns, `HrReferralId` (only when `Rejected`), `IdempotencyKey`
  (unique), review columns. `VehicleExpenseFlag` (duplicates, odometer oddities, price: shown, never blocking),
  `VehicleExpenseAlert` (who was told of a submission).
- `FleetAttachment`: `Purpose` (`Odometer`, `Invoice`, `Receipt`, `Ticket`, `Supporting`), file name, content type
  (from the bytes: JPEG, PNG, WebP, HEIC, PDF; an odometer is always a photo), size, `Sha256`, `StorageKey`
  (`fleet/yyyy/MM/guid`, unique), `CapturedAt`, uploader, `OwnerType` + `OwnerId` bound by the record's save,
  `IdempotencyKey`. Files live in `Fleet:Storage:RootPath` (default: `Archive:Storage:RootPath/fleet-module`,
  else `App_Data/fleet`), served only through `GET /api/fleet/attachments/{id}/content`.
- `VehicleViolation` (`VV-`): vehicle, the driver on the date (from the history, never typed), date, `Category`
  (`Speeding`, `Parking`, `Traffic`, `Accident`, `Damage`, `Misuse`, `Other`), description, amount, ticket no.,
  trip reference, GPS point, `Status` (`Recorded`, `UnderReview`, `Resolved`, `Dismissed`), review, `HrReferralId`.

### HR (`hr`)

- `EmployeeAssignment`: `EmployeeId`, `ProjectId` (FK `projects.Project`, by hand), `Role` (`SiteEngineer`,
  `Driver`, `Supervisor`, `Administrator`, `Guard`, `Other`), `StartDate`, `EndDate`, notes, who and when, end
  reason. One open row per employee and project; a new row never starts before the last one ended.
- `HrReferral` (`HRR-`): `SourceType` (`VehicleExpense`, `VehicleViolation`) + `SourceId` (unique), employee,
  vehicle, subject, details, amount, reference no., `Status` (`Open`, `UnderInvestigation`, `Closed`), who referred,
  investigation and close columns, `Outcome` (`DeductionApplied`, `Warning`, `NoAction`), `DeductionInputId`
  (required with `DeductionApplied`, forbidden otherwise).

Every foreign key is `NO ACTION`; cross-schema keys are written by hand in the migrations
(`TreasuryAndCustody`, `VehiclesAndCustody`, `AssignmentsAndReferrals`, `CustodyAdjustmentsAndVehicleExpenseCases`).
**Migration order:** Platform, Access, Projects, **HR, Workforce, Subcontractors, Fleet**, Archive, Procurement,
Reports, Finance (HR moved before Fleet: Fleet's driver keys point at `hr.Employee`).

## 3. Rules

- **Opening balance**: once, by an active Executive (token and database), never in the future. 409
  `opening_balance_set` afterwards.
- **Custody figures** (every kind): posted = Σ posted movements; pending = Σ pending outflows (claims under review);
  reserved = transfers `Requested`/`Approved` out + open payment requests out; in transit = transfers `Disbursed`
  into it; **available = posted − pending − reserved**. The balance policy is the company's one rule
  `Projects:CustodyBalanceCheck`: `Warn` (default) lets it go negative, `Block` refuses (409 `custody_insufficient`,
  `treasury_insufficient`).
- **Transfer lifecycle**: `Requested` → `Approved` (payments Approve; never the requester: 409 `own_record`) or
  `Rejected` (a note) or `Cancelled`; `Approved` → `Disbursed` (treasury: finance Edit; custody: its holder or a
  custody reviewer) or `Cancelled`; `Disbursed` → `Received` (destination holder or custody reviewer; a vehicle
  account has no holder, so a reviewer) or `Reversed`; `Received` → `Reversed` (custody Approve, a reason). Requests
  are idempotent by `idempotencyKey` (same requester → the same transfer, 200; another user → 409
  `idempotency_key_reused`). Cash out of the treasury additionally needs finance Create to ask for it.
- **Who sees what**: a holder sees their own accounts and transfers; custody or finance See money sees every one;
  a reviewer (custody Approve) may operate any account.
- **Payments out of a custody**: a cash payment request may name `sourceCustodyAccountId`; the payer must be the
  holder of a Finance-ledgered account (`CheckCustodySourceAsync`: `NotFound`, `NotHolder`, `NotLedgered`). Paying
  it writes a `Custody` ledger entry and a `Payment` movement under the account's lock, after the policy and
  period checks; reversing the request reverses the movement.
- **Cash counts** never change a balance. An **adjustment** is the only way a counted difference reaches the
  balance: custody Approve, never the holder (409 `own_record`), a reason and an evidence reference, optionally the
  count it settles, idempotent.
- **Period close**: custody Approve; a snapshot (`closingBalance`, `pendingAtClose`); every entry is kept; nothing
  may be dated on or before the last period end afterwards (409 `period_closed`: adjustments, receipts and
  disbursements of Finance-ledgered accounts, payments out of them). `Finance:CustodyPeriodCarryForward`:
  `CarryForward` (default) or `RequireZeroBalance` (409 `period_balance_not_zero`).
- **Payroll batches**: created by custody Approve with its own `PayrollBatch` account under the Accountant's; a
  `DailyLabor` batch has one allocation per project, each checked against `IPayrollEntitlementSource`
  (Workforce: approved − paid − held of the period, the settlement screen's arithmetic); a `Salaries` batch names
  the HR salary sheet. A distribution (a transfer out of the batch's account naming `payrollBatchId`) never
  exceeds the project's remaining allocation nor what the module still says is owed (409 `over_distribution`).
  Entitlements are never recalculated by Finance; **only the Accountant's existing settlement confirmation marks
  wages paid**; the batch view reconciles funded / distributed / returned / paid from batch / batch balance /
  discrepancy against the entitlement. A closed batch takes no more transfers (409 `batch_closed`).
- **Vehicles**: a vehicle is an `Equipment` row of category `Vehicle` plus the `Vehicle` row (same id); plate
  unique (409 `plate_taken`); the odometer never decreases. One driver at a time (409 `driver_assigned`); ending a
  stay records the custody balance and open claims at that moment.
- **Vehicle custody**: funded only by a transfer (Finance writes `Funding`/`Return`/adjustment entries through
  `VehicleCustodyLedgerSource`, naming the driver of the day). A claim creates a `Pending` expense entry
  (exposure); approval posts it **once** (409 `already_posted`, the vehicle's app lock, the unique source index);
  rejection releases it; a reversal releases it and keeps the claim and its posting history.
- **Claims**: submitted by the driver assigned to the vehicle on the expense date (a user linked to the HR
  employee), or by fleet Edit naming that driver (403 otherwise; 409 `no_driver_on_date`); the odometer photo and
  the invoice are required (422 `evidence_required` through validation; uploads are checked by type and size: 415
  `file_type_not_allowed`, 413 `file_too_large`); the expense date is not in the future and at most
  `Fleet:MaxClaimAgeDays` old. `Submitted` → `Approved` (posted) / `Rejected` (final, reason kept) /
  `EvidenceRequested` (the submitter resubmits: a new round, earlier evidence kept) / `Escalated` (an Executive
  decides; also automatic for a fuel claim gated by price control when the vehicle is on no project). The
  submitter and the driver never review their own (409 `own_record`); a claim above
  `Fleet:ClaimExecutiveApprovalAbove` needs an Executive (403 `executive_required`). Evidence is never replaced once
  approved (the attachment rows and files are kept; a resubmission adds, never removes).
- **Fuel through Price Control**: a fuel claim names a price item (unit `Liter`); `IPriceControl.CheckAsync`
  compares the price per litre: `Block` → 409 `price_above_range`; gated (`ExecutiveApproval`) → a justification is
  required and a `PriceException` of source `VehicleExpense` is opened when the vehicle is on a project (approval
  waits for the Executive: 409 `price_exception_pending`/`price_exception_rejected`); off-project the claim is
  `Escalated` instead. `Warn` → saved with the flag.
- **Flags** (`DuplicateInvoiceNo`, `DuplicateReceipt` (same SHA-256), `SameAmountVehicleDay`,
  `OdometerNotIncreasing`, `ImplausibleDistance` above `Fleet:ImplausibleDistanceKmPerDay`, `PriceAboveRange`,
  `NoReferencePrice`) are shown to the reviewer and never block.
- **Violations**: the driver is read from the history at the violation date; reviewed (`Resolved`/`Dismissed`,
  a note) by fleet Approve, never the recorder; referred to HR by fleet Approve.
- **HR referrals**: Fleet refers a rejected claim or a violation through `IHrReferrals.ReferAsync` (one referral
  per source: 409 `already_referred`); HR starts an investigation and closes with an outcome. Only
  `DeductionApplied` touches payroll, and only by raising an ordinary `PayrollInput` of kind `Deduction` through
  HR's own rules (hr Approve + Create + See money, an open month, the employee works in it, under the payroll
  lock) in the same transaction; the salary sheet's review then approves it as any other input. Nothing is ever
  deducted automatically.
- **GPS**: `IVehicleLocationProvider` is the abstraction; `NullVehicleLocationProvider` is the only implementation
  shipped. With `Fleet:Gps:Provider` = `None` every location endpoint answers 200 with `status: "NotConfigured"`
  and empty data, and `GET /api/fleet/gps-status` says so. No tracking is simulated and the site-location pings of
  users are never shown as vehicle tracking.
- **Notifications** (`NotificationRules`): a transfer request → the Executives (`TransferRequested`); approved,
  rejected, received (also the disburser), reversed → the requester; a submitted claim → the accountants and
  Executives (`ClaimSubmitted`); approved, rejected, evidence requested, escalated → the submitter; a violation
  review → its recorder; a closed referral → whoever referred it. `LiveScopes` routes every new record to the
  finance (or HR) group and, for a transfer or claim on a project, to the project's money group.
- **Work inbox** (`IWorkInboxSource`): claims waiting for review reach fuel approvers only when the vehicle is on
  a project (the inbox is project-scoped); evidence requests reach the submitter. Off-project claims are found in
  the claims queue and through the notifications.

## 4. Endpoints

| Endpoint | Needs |
|---|---|
| `GET /api/treasury` | finance See money |
| `PUT /api/treasury/{Bank\|Cash}/opening-balance` | finance Approve + Executive. `{ openingBalance, openingDate, notes?, rowVersion }` |
| `GET /api/custody-accounts`, `GET /{id}` | custody View (own, or all with See money / Approve) |
| `POST /api/custody-accounts` | custody Approve. `{ kind, holderUserId?, referenceId?, parentAccountId?, nameAr?, nameEn?, notes? }` |
| `POST /api/custody-accounts/{id}/close` | custody Approve. `{ reason, rowVersion }`. 409 `custody_not_empty` |
| `GET/POST /api/custody-accounts/{id}/cash-counts` | View / Create (holder or reviewer). `{ countDate?, countedAmount, notes?, evidenceReference?, idempotencyKey }` |
| `GET/POST /api/custody-accounts/{id}/adjustments` | View / Approve, not the holder. `{ amount, entryDate, reason, evidenceReference, cashCountId?, idempotencyKey }` |
| `GET/POST /api/custody-accounts/{id}/period-closes` | View / Approve. `{ periodEnd, notes? }` |
| `GET /api/custody-transfers`, `GET /{id}` | custody View |
| `POST /api/custody-transfers` | custody Create or Approve (treasury source: + finance Create). `{ source: { kind: Treasury\|Custody, treasuryAccount?, accountId? \| accountKind + holderUserId + referenceId }, destination: {…}, amount, purpose?, payrollBatchId?, reference?, notes?, evidenceDocumentId?, evidenceReference?, idempotencyKey }` |
| `POST /api/custody-transfers/{id}/approve`, `/reject` | payments Approve, not the requester. `{ note?, rowVersion }` (reject: note required) |
| `POST /api/custody-transfers/{id}/disburse`, `/receive` | custody View; rules in §3. `{ on?, reference?, note?, rowVersion }` |
| `POST /api/custody-transfers/{id}/cancel` | the requester, a reviewer or an Executive. `{ reason, rowVersion }` |
| `POST /api/custody-transfers/{id}/reverse` | custody Approve. `{ reason, rowVersion }` |
| `GET /api/payroll-batches`, `GET /{id}` | custody View + See money |
| `POST /api/payroll-batches` | custody Approve. `{ kind, periodFrom, periodTo, salarySheetId?, holderUserId?, parentAccountId?, nameAr?, nameEn?, notes?, allocations: [{ projectId, amount }] }` |
| `POST /api/payroll-batches/{id}/close` | custody Approve. `{ note?, rowVersion }` |
| `GET /api/vehicles`, `GET /{id}` | fleet View (figures with See money or for its driver) |
| `POST /api/vehicles`, `PUT /{id}`, `POST /{id}/odometer` | fleet Create / Edit / Edit |
| `GET/POST /api/vehicles/{id}/drivers`, `POST …/drivers/{aid}/end` | fleet View / Edit / Edit |
| `GET /api/vehicles/{id}/custody` | fleet or fuel See money, or the current driver |
| `GET /api/vehicles/{id}/location`, `/location-history`, `/trips`, `GET /api/fleet/gps-status` | fleet View |
| `POST /api/fleet/attachments` (multipart `file`, `purpose`, `idempotencyKey`, `capturedAt?`) | fuel or fleet Create |
| `GET /api/fleet/attachments/{id}/content` | fuel or fleet View (the uploader, a reviewer, or anyone who sees vehicle records once bound) |
| `GET /api/vehicle-expenses`, `GET /{id}` | fuel View (own, or all with See money / Approve) |
| `POST /api/vehicle-expenses` | fuel Create (the driver of the day; fleet Edit on their behalf). Form in §3 |
| `POST /api/vehicle-expenses/{id}/resubmit` | the submitter |
| `POST /api/vehicle-expenses/{id}/approve`, `/reject`, `/request-evidence`, `/escalate`, `/reverse` | fuel Approve (never the submitter or driver). `{ note \| reason, rowVersion }` |
| `POST /api/vehicle-expenses/{id}/refer-to-hr` | fuel or fleet Approve. `{ subject?, details, rowVersion }` |
| `GET/POST /api/vehicle-violations`, `GET /{id}` | fleet View / Create |
| `POST /api/vehicle-violations/{id}/review`, `/refer-to-hr` | fleet Approve, not the recorder. `{ outcome, note, resolution?, rowVersion }`, `{ details, rowVersion }` |
| `GET/POST /api/hr/employees/{id}/assignments`, `POST …/assignments/{aid}/end`, `GET /api/hr/assignments?projectId&on` | hr View / Edit / Edit / View |
| `GET /api/hr/referrals`, `GET /{id}`, `POST /{id}/start-investigation`, `POST /{id}/close` | hr View / View / Edit / Approve. Close: `{ outcome, note, deduction: { month, amount, reason }?, rowVersion }` |

Reports (`GET /api/reports/{code}`): `FIN-TREASURY`, `FIN-CUSTODY`, `FIN-PAYROLL-BATCH`, `FIN-PARTY-PAYMENTS`,
`FIN-CUSTODY-RECON` (from, to); `FIN-CASH` and `FIN-LEDGER` gained the internal-transfer columns;
`FLT-VEHICLE-CUSTODY`, `FLT-VEHICLE-EXPENSES`, `FLT-CLAIMS`. Approved vehicle expenses of a vehicle on a project
reach the project's `Equipment` cost group through `EquipmentCostSource` (no double counting with equipment costs:
a vehicle claim is never recorded as an equipment cost).

## 5. Contracts between modules

- `Finance.Contracts`: `ICustodyLedgerSource` (Projects: `ProjectCustodyLedgerSource`, kind `EngineerProject`;
  Fleet: `VehicleCustodyLedgerSource`, kind `Vehicle`), `IPayrollEntitlementSource` (Workforce:
  `LaborEntitlementSource`; HR: `SalaryEntitlementSource`), `IPaymentRequests.CheckCustodySourceAsync`,
  `PayeeTypes.CustodyAccount`, `PaymentPurposes.CustodyReturn`, `PaymentSourceTypes.CustodyTransfer`.
- `HR.Contracts` (new project): `IEmployeeDirectory` (Fleet's drivers), `IHrReferrals` (Fleet's referrals).
- `Projects.Contracts`: `PriceExceptionSources.VehicleExpense`, `WorkInboxRecordTypes.VehicleExpense`,
  `VehicleViolation`; `CustodyEntryKind.AdjustmentIn/Out` on the project custody.

## 6. Settings

| Key | Default | Meaning |
|---|---|---|
| `Finance:CustodyPeriodCarryForward` | `CarryForward` | or `RequireZeroBalance` |
| `Projects:CustodyBalanceCheck` | `Warn` | reused by the treasury, the custody accounts and the vehicle custody |
| `Fleet:ClaimExecutiveApprovalAbove` | `0` | a claim above it needs an Executive |
| `Fleet:ImplausibleDistanceKmPerDay` | `1000` | the odometer flag |
| `Fleet:MaxClaimAgeDays` | `60` | the oldest expense date a claim may carry |
| `Fleet:ImageMaxMegabytes`, `Fleet:DocumentMaxMegabytes` | `15`, `20` | upload limits |
| `Fleet:Gps:Provider`, `Fleet:Gps:RetentionDays` | `None`, `90` | no provider is shipped |
| `Fleet:Storage:RootPath` | `Archive:Storage:RootPath/fleet-module` | evidence files; when it follows the Archive folder, the Archive module's startup check covers it |

## 7. Tests (see the delivery report for the run)

Finance: `Unit/CustodyRulesTests`, `Unit/DatabaseSchemaTests`, `Api/FinanceApiSecurityTests` (every endpoint),
`Database/TreasuryCustodyTests` (A, B, C, R, S, T). Integration: `TreasuryChainTests` (D, E, F, G, the payroll
batch, Q). Fleet: `Unit/VehicleRulesTests`, `Unit/DatabaseSchemaTests`, `Api/VehicleApiSecurityTests`,
`Database/VehicleCustodyTests` (H to P, U, the fuel price path). HR: `Database/AssignmentTests`,
`Api/HrApiSecurityTests`, `Unit/DatabaseSchemaTests`. Platform: `NotificationRulesTests`, `LiveScopesTests`.
Projects: schema checks for the widened constraints.

## 8. Flutter screens this needs (not built)

Treasury (balances, opening balance once), custody accounts (list, statement, counts, adjustments, period
closes), transfer request and the four steps with the notification deep links, payroll batches (create with
allocations, reconciliation), vehicles (register, drivers, odometer), the driver's claim form (camera for the
odometer and the receipt, offline queue by `idempotencyKey`), the reviewer's claim queue with flags and evidence,
violations, HR assignments and referrals, the GPS status page (truthful "no provider").

## 9. Known gaps (accepted for this delivery)

- No GPS provider is integrated: the abstraction and the truthful empty answer only.
- The Office module keeps its own funding requests (a Finance payment request) next to the registry's transfers:
  both reach the same office ledger; the central page shows the ledger, so nothing is counted twice.
- Claims of a vehicle on no project are not in the work inbox (project-scoped); the claims queue and the
  notifications carry them.
- Evidence files are stored by the Fleet file store, not the Archive module's documents (T8).
- The opening balance is set once; correcting it needs a database change by an administrator.
- Period checks apply to what Finance writes (its own movements, payments out of its accounts, receipts and
  disbursements); a project's or vehicle's own entries are governed by their modules.
- The payroll batch of kind `Salaries` reads the sheet's entitlement from HR but its distribution is one transfer;
  the sheet itself is still paid through the existing salary payment request.

## 10. Sections 12 to 18 of the second brief (engineer, accountant, approvals, central page, audit)

Decisions T10 to T16 in `07-decisions.md`. What exists and what was added:

**Engineer account (12).** `GET /api/me/overview` (any signed-in user; Projects composes it): the projects the
caller is on (`memberships` responsibility), their cash custody on each project (received, returned, spent approved,
spent pending, balance, pending count), their purchases and expenses waiting for review, the latest custody
movements, and each module's section through `IPersonalOverviewSource`: `Funding` (Finance: custody funding requests
and transfers, pending amounts: a request is never a transfer), `Labor` (Workforce: daily sheets by status, weekly
settlements pending/confirmed), `Hr` (HR: own requests by status, cases about them). The daily labor cost record is
the Workforce `DailySheet` (T12); the weekly settlement is the Workforce `PayrollSettlement`, confirmed by the
Accountant, which alone marks wages paid; a settlement above the custody balance is refused under policy Block
(409 `custody_insufficient`) or recorded with a negative balance under Warn.

**HR requests (12.D).** `hr.HrRequest` (`HRQ-`; types AttendanceCorrection, Leave, Permission,
OvertimeClarification, PayrollQuestion, CaseResponse, Other; statuses Submitted → UnderReview → NeedsInfo →
Submitted, and Approved / Rejected / Closed with a note), `HrRequestMessage` (the thread), `HrRequestAttachment`
(evidence in the shared private store under `Hr:Storage:RootPath`, typed from the bytes), `HrRequestAlert` (who was
told). Self-service: `GET/POST /api/hr/my/requests`, `GET /api/hr/my/requests/{id}`, `POST …/messages`,
`POST /api/hr/my/attachments`, `GET /api/hr/my/referrals` (the subject, status and outcome of cases about them;
never the referrer's notes). HR: `GET /api/hr/requests` (hr View), `/start-review`, `/request-info`, `/messages`
(hr Edit), `/approve`, `/reject`, `/close` (hr Approve, never one's own). `GET /api/hr/attachments/{id}/content`
for the uploader or HR. A login not linked to an employee gets 404 `not_an_employee`. An approved request changes no
payroll by itself.

**Office (15).** After the merge of `feature/office-management`, the office cash custody (`office.OfficeCustody`,
`OfficeLedgerEntry`, expenses with receipts, funding requests, cash counts) is one account of the hierarchy: kind
`Office`, reference = the office custody id, holder = its custodian, ledgered by the Office module through
`OfficeCustodyLedgerSource` (a transfer received or returned, its reversal and a reviewer's adjustment are written
to the office ledger with source types `CustodyTransfer`, `CustodyTransferReversal`, `CustodyAdjustment`; migration
`FinanceCustodyLinks`). The central custody page lists the office custodian from that ledger; an office expense is
unsupported only when its receipt is missing. Office is registered with `POST /api/custody-accounts
{ kind: "Office", referenceId }` or opened on the way by a transfer naming it.

**Accountant supervision (13).** `FIN-ACTIVITY` report (finance See money): every payment request, custody transfer,
adjustment and cash count of a period with who initiated it, who decided, the evidence reference and the link. The
Accountant initiates (requests, transfers, cash counts) and never decides: payments Approve and custody Approve are
the Executive's by default and every decision endpoint re-checks the database. Transfers between custody holders
post both sides only on receipt (section 2). The `Office` kind joins the registry when the Office module is merged.

**Price proposals (14).** Already the Price Control design: a range is insert-only, proposed with a reason, approved
or rejected by a database-checked Executive, never one's own; a pending range changes no calculation; the previous
range stays. Added: a new item's first range is a proposal unless an Executive creates it (T16); pending proposals
are in the Executive's inbox (`PriceRange`).

**Central custody page (16).** `GET /api/custody-monitor?from&to&kind&status&holderUserId&projectId&attention`
(custody See money or finance See money): one row per account with holder and role, posted, pending, reserved, in
transit, available, owed (a negative balance), the period's received / returned / transferred out / spent, the
unsupported spending (approved custody expenses with no Archive document, T14), open transfers, last entry, last
period end; totals over the filtered set. `GET /api/custody-monitor/{accountId}` opens the unified statement (paged,
newest first, `hasEvidence` per line), open transfers, adjustments, cash counts and period closes. Figures come from
`ICustodyLedgerSource.ActivityAsync` / `StatementAsync` (Projects, Fleet) or Finance's movements.
Adjustments (16.A): posted against the holder (`holderUserId`), with a `shortfall` when a debit went beyond the
balance under Warn, told to the holder; `POST …/adjustments/{id}/dispute` (the holder, a note) and
`POST …/adjustments/{id}/resolve` (custody Approve, never the holder: keep, or undo with an opposite adjustment
pointing at the original). Never a payroll deduction (T15).

**Audit (17).** Every module context but Platform's own tables goes through `AuditTrailInterceptor`; permission and
role removals now go through the change tracker, so the removed grants are in the trail. `GET /api/audit-entries`
(Executive) filters by module, entityType, entityKey, projectId, userId, from, to, and now `action` and
`statusAfter`. The table is append-only at the database (`ProtectAuditTrail` trigger refuses UPDATE and DELETE).

**Approval queues (18).** The work inbox carries company-wide items: custody transfers to decide (payments
Approve), cash to confirm receiving (the holder), rejected transfers (the requester), payment requests to decide,
price proposals (catalog Approve), HR requests (hr Edit) and questions back to the employee. Status vocabularies stay
each flow's own (`StatusFlow`), with Requested/Submitted, Approved, Rejected, Disbursed/Received or Paid, Reversed.

Settings added: `Hr:Storage:RootPath` (default `Archive:Storage:RootPath/hr-module`), `Hr:Storage:MaxMegabytes` (15).
Migrations added: HR `HrRequests`, Finance `CustodyAdjustmentDisputes`, Platform `ProtectAuditTrail`.
