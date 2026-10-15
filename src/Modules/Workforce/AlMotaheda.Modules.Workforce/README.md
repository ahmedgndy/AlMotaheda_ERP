# Workforce module

Daily-paid workers, their assignment to projects, the daily labor sheets engineers record, the allocation of each
approved worker-day to project items, and worker payments. The design is `docs/project-workspace/03-workforce.md`
(decisions W1 to W3 in `07-decisions.md`).

## What it does

- **Trades:** the 16 trades of the prototype are seeded with fixed ids, each with a default daily rate and work type.
  More can be added; trades are deactivated, never deleted.
- **Workers:** one company-wide row per person (national id unique when given), reused on every project. Workers
  have no login. An engineer is never a worker row: the engineer is the `RecordedBy` of a sheet.
- **Assignments:** a worker is put on a project (optionally with a rate for that project only) and later ended.
  Ended assignments are kept, so history and old sheets still resolve.
- **Daily labor sheets:** one sheet per engineer, project, day and shift (W1). `Draft → Submitted → Approved`, or
  `Returned` (back to the recorder, with a note) or `Rejected` (final; its lines become void and the worker-days are
  free again). A worker is recorded once per day and shift, whatever the project or the engineer.
- **Pay of a line** (`LaborPay`): the rate is the day's trade default when the worker worked another trade; else the
  project rate, else the worker's rate, else the trade default. `base = rate × days`, `overtime = hours × rate ÷ 8 × 1.5`,
  `net = base + overtime + allowances − deductions` (never below zero), all to the piastre. The server computes every
  amount; the client never sends a rate. Before approval every read shows a preview with today's rates. **At approval
  the rate and every amount are copied onto the line and never change again.**
- **Allocation to project items** (`LaborAllocator`): a line has zero, one or several allocations ("this part of
  the day went to this item"). The portions of a line add up to **at most one day**, so nobody's day is ever counted
  twice. At approval each allocation gets `round(net × portion)`; the last one takes the rounding rest, so a fully
  allocated line adds up to its net exactly. What is not allocated is "not linked yet".
- **Worker payments** (`PaymentFifo`): a payment settles the worker's oldest approved unpaid days first, across all
  projects (work date, then line id), and the allocations are stored. A payment above what the worker is owed is
  refused (no advances, W2). A wrong payment is voided, never edited or deleted.
- **Worker screens show only visible projects:** a worker is company-wide, but `GET /api/workers/{id}`, its
  `/account` and its `/payments` show only the assignments, rates, pay and payment allocations of projects the caller
  may see (`IProjectAccess`, asked once per distinct project). The account's worker totals are summed from those
  projects only (the safer choice: a company total would reveal pay on hidden projects); the FIFO still runs over
  every day first, so a visible day shows what is really paid on it. A payment that also settled days on a hidden
  project is listed with only its visible allocations, and its amount is their sum; a payment with no visible
  allocation is left out. A caller who sees every project, such as an Executive, gets the company-wide figures.
- **Labor cost for the Projects module:** `ILaborItemCosts` and `IProjectCostSource` (group `Labor`) are implemented
  here (`Integration/ProjectContracts.cs`) by `LaborItemLedger`, from two flat queries. **Only approved, non-void
  lines count.** Each line is split into its parts per item plus the not-linked rest; the parts add up to the line's
  net, and its paid amount is split over the same parts, so the items, the not-linked group and the project total
  always reconcile. Lines of submitted sheets (waiting for a reviewer) are reported apart as `PendingReview` and counted one record per line in `PendingRecords` (a preview, never cost). A returned sheet is back with its recorder, so it is not pending review, the same as in the other cost groups.

## Who may do what

| Action | Needs |
|---|---|
| See trades, workers, a project's workers, sheets, the labor summary | workers View (and the project in scope) |
| Add or edit trades and workers; assign and end assignments | workers Edit |
| Record a sheet | workers Create, project in scope, not completed |
| Edit or submit a sheet | the recorder only, only `Draft` or `Returned` |
| Approve, return, reject | workers Approve, **never the recorder** (409 `own_record`), only `Submitted` |
| Change item allocations | the recorder (workers Create) while the sheet is `Draft` or `Returned`, or workers Edit or Approve until rejected |
| Money fields (rates, pay, paid, outstanding) | workers See money; otherwise `null`. The recorder always sees the allowances and deductions they typed |
| Worker account and payments list | workers See money |
| Record a worker payment | payments Create |
| Void a worker payment | payments Approve |
| Full national ids | workers Edit; others see only the last 4 digits |

A completed project takes no change (409 `project_completed`) except from an Executive. A project outside the
caller's scope answers 404, the same as a project that doesn't exist.

## Endpoints

| Endpoint | Needs |
|---|---|
| `GET /api/trades` | workers View |
| `POST /api/trades`, `PUT /api/trades/{id}` | workers Edit |
| `GET /api/workers` (`search`, `tradeId`, `status`, `projectId`, paging) | workers View |
| `GET /api/workers/{id}` | workers View |
| `POST /api/workers`, `PUT /api/workers/{id}` | workers Edit |
| `GET /api/workers/{id}/account` | workers See money |
| `GET /api/workers/{id}/payments` | workers See money |
| `POST /api/workers/{id}/payments` | payments Create |
| `POST /api/worker-payments/{id}/void` | payments Approve |
| `GET /api/projects/{pid}/workers` (`includeEnded`) | workers View |
| `POST /api/projects/{pid}/workers` | workers Edit |
| `POST /api/projects/{pid}/workers/{assignmentId}/end` | workers Edit |
| `GET /api/projects/{pid}/labor/sheets` (`status`, `from`, `to`, `shift`, `mine`, paging) | workers View |
| `GET /api/projects/{pid}/labor/sheets/{id}` | workers View |
| `POST /api/projects/{pid}/labor/sheets` | workers Create |
| `PUT /api/projects/{pid}/labor/sheets/{id}` | workers Create, the recorder |
| `POST /api/projects/{pid}/labor/sheets/{id}/submit` | workers Create, the recorder |
| `POST /api/projects/{pid}/labor/sheets/{id}/approve`, `/return`, `/reject` | workers Approve, not the recorder |
| `PUT /api/projects/{pid}/labor/sheets/{id}/allocations` | the recorder while `Draft` or `Returned`, or workers Edit or Approve |
| `PUT /api/projects/{pid}/labor/lines/{lineId}/allocations` | workers Edit or Approve |
| `GET /api/projects/{pid}/labor/summary` | workers View (money with See money) |

Dates are `yyyy-MM-dd`. A sheet's work date is today (company time zone) or one of the 7 days before. Enums are sent
and returned by name, any letter case accepted. Edits and reviews take the `rowVersion` of the last read. Payments
carry an `idempotencyKey` (a GUID): the same key again returns the first payment and creates nothing.

## Errors

| Status | Code | When |
|---|---|---|
| 400 | `validation_failed` | Field errors, all together (lines and allocations by path, e.g. `lines[2].allocations[0].portion`) |
| 403 | `forbidden` | Missing permission, or not the recorder of the sheet |
| 404 | `not_found` | Project not in scope or missing; sheet, line, worker, trade, assignment or payment not found |
| 409 | `trade_code_taken`, `national_id_taken`, `worker_inactive`, `not_assigned`, `already_assigned`, `sheet_exists`, `worker_already_recorded`, `own_record`, `invalid_status`, `allocation_exceeds_day`, `item_inactive`, `item_removed`, `negative_net`, `over_payment`, `idempotency_key_reused`, `payment_already_voided`, `project_completed`, `concurrency_conflict` | Rule conflicts (`item_removed`: a linked project item was deleted while saving) |
| 503 | `busy` | The project lock (approve, allocate) or the worker lock (pay, void) was held for more than 10 seconds; retry |

## Tables (schema `workforce`)

- `Trade` (seeded, ids 1–16), `Worker`, `WorkerAssignment` (filtered unique: one open assignment per worker and
  project), `DailySheet` (filtered unique: one live sheet per project, day, shift and recorder), `AttendanceLine`
  (filtered unique: one live line per worker, day and shift; the pay columns are all empty until approval, then all
  set, with `NetAmount = Base + Overtime + Allowances − Deductions ≥ 0` checked in SQL), `LineAdjustment`,
  `LaborAllocation` (`Portion` in (0, 1], unique per line and item), `WorkerPayment` (unique idempotency key),
  `WorkerPaymentAllocation` (unique per payment and line).
- Foreign keys to `projects.Project` (`WorkerAssignment.ProjectId`, `DailySheet.ProjectId`) and
  `projects.ProjectItem` (`LaborAllocation.ProjectItemId`) are written by hand in the migration. **Apply the Access
  and Projects migrations before the Workforce migrations.** No foreign key cascades.

## Configuration

| Setting | Default | Notes |
|---|---|---|
| `Workforce:RegularHours` | `8` | Overtime pay per hour is the daily rate divided by this |
| `Workforce:OvertimeMultiplier` | `1.5` | Checked at startup (1 to 5) |
| `Projects:TimeZone` | `Africa/Cairo` | "Today" for the 7-day sheet window and payment dates |
| `Database:ApplyMigrationsOnStartup` | `false` | Development only; runs after the Projects migrations |

## Known limits (accepted)

- No correction of an approved sheet and no undo of an approval (open question 3). Only the item links of an approved
  line can change.
- No worker advances, hourly or weekly wages, or weekly payroll runs (W2, W3, open question 4).
- The labor figures are computed per request from flat rows of one project (decision G5). A very large project would
  move the fully paid and unpaid rows to SQL aggregation, never to a stored total.
- Until the platform AuditLog table exists, the audit trail is the structured log (`WorkforceLog`, event ids 3001 to
  3091), by id only.

## Code layout

- `Features/Trades`, `Features/Workers`, `Features/Assignments`: the company lists and project assignment.
- `Features/Sheets`: the sheet use cases, the form rules (`SheetForm`, `SheetReferences`), the workflow
  (`SheetRules`), the pay (`LaborPay`) and the allocation (`LaborAllocator`, `AllocationChanges`).
- `Features/Payments`: worker payments and the worker account (`PaymentFifo`, `WorkerAccounts`).
- `Features/Ledger`: `LaborItemLedger` and its queries, shared by the labor summary and the Projects contracts.
- `Integration`: the implementations of the `Projects.Contracts` interfaces.
- `Data`: entities, `WorkforceDbContext`, configuration and migrations. `Startup`: applies migrations when enabled.
