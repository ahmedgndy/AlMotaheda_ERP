# Workforce module: workers, daily labor, allocation to project items, worker payments

Module `AlMotaheda.Modules.Workforce`, schema `workforce`, permission module `workers` (and `payments`).
Read `00-overview.md` first.

## 1. Who is who

| Thing | Where | Login? |
|---|---|---|
| User account | `access.User` | yes |
| Engineer, manager | a user with a role, on a project through `projects.ProjectMember` | yes |
| Worker (daily-paid) | `workforce.Worker` | **no** |
| Worker on a project | `workforce.WorkerAssignment` | no |

A worker is created once (unique national id) and reused on every project. An engineer records the workers;
the engineer is the `RecordedBy` of the sheet, never a worker row.

## 2. Tables

`Trade` (seeded): `Code` varchar(30) unique, `NameAr`, `NameEn`, `DefaultDailyRate` decimal(18,2) > 0,
`DefaultWorkType` (one of `WorkTypes`), `IsActive`. Seed the 16 trades of the prototype with fixed ids.

`Worker`: `NameAr`, `NameEn` nvarchar(200), `NationalId` varchar(14) null (14 digits; filtered unique
`UX_Worker_NationalId`), `Phone` varchar(30) null, `TradeId` FK, `DailyRate` decimal(18,2) null (> 0),
`Status` (`Active`, `Inactive`), `Notes` nvarchar(1000) null, `RowVersion`.

`WorkerAssignment`: `WorkerId` FK, `ProjectId` (FK to `projects.Project`), `DailyRate` decimal(18,2) null (a rate for
this project only), `StartDate`, `EndDate` null, `AssignedAt`, `AssignedBy`, `EndedAt`, `EndedBy`, `RowVersion`.
Filtered unique `(WorkerId, ProjectId) WHERE EndDate IS NULL`. Ended rows are kept.

`DailySheet`: `ProjectId` (FK to `projects.Project`), `WorkDate`, `Shift` (`Day`, `Night`), `RecordedBy` (user),
`Status` (`Draft`, `Submitted`, `Returned`, `Approved`, `Rejected`), `SubmittedAt`, `ReviewedAt`, `ReviewedBy`,
`ReviewNote` nvarchar(500), `Notes` nvarchar(1000), `RowVersion`.
Filtered unique `(ProjectId, WorkDate, Shift, RecordedBy) WHERE Status <> 'Rejected'`.

`AttendanceLine`: `SheetId` FK, `WorkerId` FK, `TradeId` FK (the trade worked that day), `WorkDate`, `Shift`
(copied from the sheet), `Days` decimal(2,1) CHECK IN (0.5, 1), `OvertimeHours` decimal(4,1) CHECK 0–12,
`WorkType`, `Notes` nvarchar(300), `IsVoid` bit, and the money columns, all `NULL` until approval:
`AppliedDailyRate`, `BaseAmount`, `OvertimeAmount`, `AllowancesAmount`, `DeductionsAmount`, `NetAmount` (CHECK ≥ 0).
Filtered unique `(WorkerId, WorkDate, Shift) WHERE IsVoid = 0`: a worker is recorded once per shift per day,
whatever the project or the engineer. Night guard plus day work is allowed.

`LineAdjustment`: `LineId` FK, `Kind` (`Allowance`, `Deduction`), `Type` (`Food`, `Transport`, `Night`,
`Accommodation`, `Tip`, `Other`), `Amount` > 0, `Reason` nvarchar(300) (required for a deduction).

`LaborAllocation`: `LineId` FK, `ProjectItemId` (FK to `projects.ProjectItem`), `Portion` decimal(5,4)
CHECK (> 0 AND ≤ 1), `Amount` decimal(18,2) null (set when the sheet is approved), `CreatedAt`, `CreatedBy`.
Unique `(LineId, ProjectItemId)`. Index `(ProjectItemId)`.

`WorkerPayment`: `WorkerId` FK, `PaymentDate`, `Amount` > 0, `Method` (`Cash`, `BankTransfer`), `Reference`,
`Notes`, `IdempotencyKey` unique, `VoidedAt`, `VoidedBy`, `VoidReason`, `RowVersion`.

`WorkerPaymentAllocation`: `PaymentId` FK, `LineId` FK, `Amount` > 0. Unique `(PaymentId, LineId)`. Index `(LineId)`.

## 3. Rules

### Sheet workflow

```
Draft ─submit─► Submitted ─approve─► Approved      (labor cost recognized; lines frozen)
                    │
                    ├─return (note)─► Returned ─edit, submit─► Submitted
                    └─reject (note)─► Rejected     (lines become void; the worker-days are free again)
```

- Create and edit: `workers` Create, scope, project not completed. Only the recorder edits, only `Draft` or `Returned`.
- `WorkDate` is today or one of the 7 days before.
- Every worker on a sheet must be `Active` and assigned to this project on `WorkDate`.
- Approve, return, reject: `workers` Approve, never the recorder, only from `Submitted`.

### Pay of one line (pure class `LaborPay`)

```
rate        = line trade ≠ worker's trade ? that trade's DefaultDailyRate
              : assignment.DailyRate ?? worker.DailyRate ?? trade.DefaultDailyRate
base        = round(rate × Days)
overtime    = round(OvertimeHours × rate ÷ RegularHours × OvertimeMultiplier)
allowances  = Σ allowance adjustments
deductions  = Σ deduction adjustments
net         = base + overtime + allowances − deductions          (must be ≥ 0)
```

`RegularHours` (8) and `OvertimeMultiplier` (1.5) are settings (`Workforce:RegularHours`,
`Workforce:OvertimeMultiplier`). The rate and all amounts are **copied onto the line when the sheet is
approved**. Later changes to a worker's rate, a trade or the settings never change an approved line.
Before approval, reads show a preview computed with today's rates.

### Allocation to project items (pure class `LaborAllocator`)

The requirement: an engineer records 14 workers; 7 did plumbing; the plumbing item must show those 7 workers,
their days, rates and cost, and nobody's day may be counted twice.

- A line has zero, one or several allocations. Each says "this portion of this worker's day went to this item".
- Per line: items are distinct, belong to this project and are active; every portion is > 0; **the portions add
  up to at most 1**. So a day can never be counted in full against two items.
- A line with portions below 1 has an unallocated rest. A line with no allocation is fully "not linked".
- At approval each allocation gets `Amount = round(net × portion)`; the last one takes the rounding rest so the
  allocated amounts of a fully allocated line add up to `net` exactly.
- The recorder sets allocations with the sheet. After submit or approval, a user with `workers` Edit or
  `workers` Approve can set or change them (this is how a "not linked" labor record is linked). On an approved
  line the amounts are recomputed in the same transaction. Net, rate and payments never change.
- Paid amount of an allocation = the line's paid amount × (allocation amount ÷ net), with the rounding rest on
  the last part, so the parts of a line always add up to what was really paid.

### Worker payments (pure class `PaymentFifo`)

- `payments` Create. One transaction under a lock per worker.
- A payment settles the worker's oldest approved, unpaid days first, across all projects (`WorkDate`, then line
  id). The allocations are stored, so one payment is never counted twice.
- A payment larger than the worker's outstanding approved pay answers 409 `over_payment`. There are no worker
  advances in this version (decision W2).
- Void: `payments` Approve, with a reason. Its allocations stop counting.

## 4. Contracts implemented for Projects

- `ILaborItemCosts` and `IProjectCostSource` (`Group = Labor`) from `Projects.Contracts`, computed by the pure
  class `LaborItemLedger` from two flat queries (approved lines with their paid sum; their allocations).
- Only approved, non-void lines count. Pending sheets are reported apart (`PendingReview`).

## 5. Endpoints

| Endpoint | Needs | Notes |
|---|---|---|
| `GET /trades` | `workers` View | |
| `POST /trades`, `PUT /trades/{id}` | `workers` Edit | 409 `trade_code_taken` |
| `GET /workers` | `workers` View | `search`, `tradeId`, `status`, `projectId` (assigned now), paging. National id masked except the last 4 digits unless the caller has `workers` Edit |
| `GET /workers/{id}` | `workers` View | With assignments. Rates only with `workers` SeeMoney |
| `POST /workers`, `PUT /workers/{id}` | `workers` Edit | 409 `national_id_taken` |
| `GET /workers/{id}/account` | `workers` SeeMoney | Approved pay, paid, outstanding, open days by project |
| `GET /workers/{id}/payments` | `workers` SeeMoney | |
| `POST /workers/{id}/payments` | `payments` Create | `{ paymentDate, amount, method, reference, notes, idempotencyKey }` |
| `POST /worker-payments/{id}/void` | `payments` Approve | `{ reason, rowVersion }` |
| `GET /projects/{pid}/workers` | `workers` View, scope | Workers on the project now; `includeEnded` |
| `POST /projects/{pid}/workers` | `workers` Edit, scope, not completed | `{ workerId, dailyRate?, startDate? }`; reuses the worker row. 409 `already_assigned` |
| `POST /projects/{pid}/workers/{assignmentId}/end` | `workers` Edit, scope | `{ endDate?, rowVersion }`. History is kept |
| `GET /projects/{pid}/labor/sheets` | `workers` View, scope | `status`, `from`, `to`, `shift`, `mine=true`, paging |
| `GET /projects/{pid}/labor/sheets/{id}` | same | Lines, adjustments, allocations, pay (or preview) |
| `POST /projects/{pid}/labor/sheets` | `workers` Create, scope, not completed | `{ workDate, shift, notes, submit, lines: [{ workerId, tradeId?, days, overtimeHours, workType?, notes, adjustments: [{ kind, type, amount, reason }], allocations: [{ itemId, portion }] }] }` |
| `PUT /projects/{pid}/labor/sheets/{id}` | the recorder | Replaces the lines; `rowVersion` |
| `POST …/labor/sheets/{id}/submit` | the recorder | |
| `POST …/labor/sheets/{id}/approve`, `/return`, `/reject` | `workers` Approve, not the recorder | `rowVersion`; note for return and reject |
| `PUT …/labor/sheets/{id}/allocations` | the recorder (not approved yet), or `workers` Edit / Approve | `{ rowVersion, lines: [{ lineId, allocations: [{ itemId, portion }] }] }` |
| `PUT …/labor/lines/{lineId}/allocations` | `workers` Edit or Approve, scope | `{ allocations: [{ itemId, portion }] }` |
| `GET /projects/{pid}/labor/summary` | `workers` View, scope | Workers assigned, sheets pending; with SeeMoney: approved, paid, outstanding, pending amount |

Money fields are `null` without `workers` SeeMoney. Adjustments typed by the recorder are shown to the recorder.

## 6. Error codes

`trade_code_taken`, `national_id_taken`, `worker_inactive`, `not_assigned`, `already_assigned`, `sheet_exists`,
`worker_already_recorded` (the worker has a line for that day and shift), `own_record`, `invalid_status`,
`allocation_exceeds_day`, `item_inactive`, `negative_net`, `over_payment`, `idempotency_key_reused`,
`payment_already_voided`, `project_completed`, `busy`.

## 7. Tests that must exist

- `LaborPay`: full day, half day, overtime, allowances, deductions, rate order, the trade-change rule.
- `LaborAllocator`: 14 workers, 7 to plumbing and 7 split over other items; each item shows its own workers and
  amounts; the items add up to the sheet's net; portions above 1 are refused; a 0.5 + 0.5 split of an odd amount
  keeps the cents right.
- `LaborItemLedger`: paid shares of a split, partly paid line add up to the paid amount; unapproved, returned,
  rejected and void lines are in no total; a line allocated to item A never shows in item B or in "not linked".
- `PaymentFifo`: oldest first, across projects, partial last line, over-payment refused, voided payments ignored.
- Every endpoint: 401 without a token, 403 without the permission, 404 for an invisible project.
