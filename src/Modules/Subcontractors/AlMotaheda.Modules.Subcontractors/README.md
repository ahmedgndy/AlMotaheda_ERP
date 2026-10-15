# Subcontractors module

Subcontractor companies, their contract on each project, contract items, measured work, manpower sheets and
payments. The design is in `docs/project-workspace/04-subcontractors.md` (rules shared by every module in
`00-overview.md`, decisions in `07-decisions.md`, especially S1, S2, S3, G3, G4 and G5).

## What it does

- **Companies:** one company list for the whole company (overview 3.6). A subcontractor is not a supplier: suppliers
  sell materials (Projects module), subcontractors do work under a contract. An inactive company keeps its history
  but gets no new contract.
- **Contracts:** one contract per company per project (`UnitRate`, `LumpSum` or `DailyRate`). The same company can
  work on many projects; each contract keeps its own value, scope, items, work and payments.
- **Contract items:** lines of contracted work (name, work type, unit, quantity, unit price). A line can point to
  one project item of the same project.
- **Measured work** (`UnitRate`, `LumpSum`): `Recorded`, then `Approved` or `Rejected` by someone else (decision
  S1). The unit price is copied from the item when recorded. A `Job` or `Lot` line can be measured as
  `percentOfLine`. Measured quantity that is not rejected never passes the item quantity (tolerance 0.0005),
  checked at recording and again at approval.
- **Manpower sheets** (`DailyRate` only): workers × days × daily rate, computed by the server; the rate defaults to
  the contract's. Sheets that are not rejected never pass the contract value (the ceiling). Same review as
  measurements. A sheet can point to a project item.
- **Payments:** a fact, never edited or deleted; a wrong one is voided. A payment settles the contract's oldest
  approved, unpaid work first (date, then measurements before manpower, then id). What is left is an **unapplied
  advance** (decision S3): paid, but not against any work. When work is approved later, or a payment is voided,
  the unapplied money is applied again, oldest payment first, in the same transaction (`SubSettlement`).

### Three numbers, never mixed

| Number | Meaning |
|---|---|
| Contract value | What was signed. A **commitment**, never a cost |
| Executed (recognized) | Σ approved measurements + Σ approved manpower sheets. **The cost** |
| Paid | Σ payments that are not voided. An advance is paid but is never cost |

A subcontract amount is recorded once, as a measurement or a manpower sheet. Linking it to a project item only sets
a link on that record: no amount is copied and no row is created.

The contract account (`ContractAccount`, one pure class used by the list, the detail and the cost figures):

```
executed            = approved measurements + approved manpower
pendingReview       = recorded (not approved) measurements + manpower
remainingToExecute  = max(0, Value − executed)                    (the commitment)
paidTotal           = Σ payments not voided
appliedToWork       = Σ allocations of payments not voided
unappliedAdvance    = paidTotal − appliedToWork
outstandingWork     = executed − appliedToWork
retentionHeld       = Completed ? 0 : round(executed × RetentionPercent ÷ 100)   (shown only, decision S2)
dueNow              = max(0, executed − retentionHeld − paidTotal)
completedPercent    = Value = 0 ? 0 : min(100, executed ÷ Value × 100)
```

### What Projects gets (through `AlMotaheda.Modules.Projects.Contracts`)

- `ISubcontractItemCosts`: approved work per project item (a measurement counts on its contract item's project
  item, a manpower sheet on its own), plus one total with `ItemId = null` for approved work not linked yet. Lines
  carry the company, its specialty (trade), the contract number, quantity, unit, unit price, the recognized amount,
  paid (allocations only, never an advance), outstanding, payment state and the source record id.
  `HasLinksAsync` is true when any contract item or manpower sheet (any status) points to the item.
- `IProjectCostSource` (`Group = Subcontractors`): recognized = approved work in the date range; pending = recorded
  work; committed = Σ `remainingToExecute` of contracts that are not `Completed`, always as of now.

Recorded and rejected work is never in any of these figures, and neither is the contract value.

## Who may do what

| Action | Needs |
|---|---|
| See companies | subcontractors View |
| Add / edit a company | subcontractors Create / Edit |
| See a project's contracts and their records | subcontractors View, project in scope. Money is `null` without subcontractors See money |
| Add a contract | subcontractors Create, project in scope |
| Edit a contract, delete an item, set a link | subcontractors Edit, project in scope |
| Add or edit a contract item, record measured work | subcontractors Create **or** Edit, project in scope |
| Record a manpower sheet | subcontractors Create, project in scope |
| Approve or reject work | subcontractors Approve, project in scope, not the person who recorded it (409 `own_record`) |
| Record a payment | payments Create, project in scope (decision G4) |
| Void a payment | payments Approve, project in scope |

A project the caller may not see answers 404, the same as one that doesn't exist. A child record (contract, item,
measurement, sheet, payment) is always looked up together with the project of the route; one of another project
answers 404. A completed project takes no change except from an Executive (409 `project_completed`).

## Endpoints

| Endpoint | Needs |
|---|---|
| `GET /api/subcontractors` (`search`, `specialty`, `status`, `page`, `pageSize`) | subcontractors View |
| `GET /api/subcontractors/{id}` | subcontractors View |
| `POST /api/subcontractors` | subcontractors Create |
| `PUT /api/subcontractors/{id}` | subcontractors Edit |
| `GET /api/projects/{pid}/subcontracts` (`status`) | subcontractors View |
| `GET /api/projects/{pid}/subcontracts/{cid}` | subcontractors View |
| `POST /api/projects/{pid}/subcontracts` | subcontractors Create |
| `PUT /api/projects/{pid}/subcontracts/{cid}` | subcontractors Edit |
| `POST /api/projects/{pid}/subcontracts/{cid}/items` | subcontractors Create or Edit |
| `PUT /api/projects/{pid}/subcontracts/{cid}/items/{itemId}` | subcontractors Create or Edit |
| `DELETE /api/projects/{pid}/subcontracts/{cid}/items/{itemId}` | subcontractors Edit |
| `PUT /api/projects/{pid}/subcontracts/{cid}/items/{itemId}/project-item` | subcontractors Edit |
| `POST /api/projects/{pid}/subcontracts/{cid}/measurements` | subcontractors Create or Edit |
| `POST /api/projects/{pid}/subcontract-measurements/{id}/approve` | subcontractors Approve |
| `POST /api/projects/{pid}/subcontract-measurements/{id}/reject` | subcontractors Approve |
| `POST /api/projects/{pid}/subcontracts/{cid}/manpower` | subcontractors Create |
| `POST /api/projects/{pid}/subcontract-manpower/{id}/approve` | subcontractors Approve |
| `POST /api/projects/{pid}/subcontract-manpower/{id}/reject` | subcontractors Approve |
| `PUT /api/projects/{pid}/subcontract-manpower/{id}/project-item` | subcontractors Edit |
| `POST /api/projects/{pid}/subcontracts/{cid}/payments` | payments Create |
| `POST /api/projects/{pid}/subcontract-payments/{id}/void` | payments Approve |

Dates are `yyyy-MM-dd`; work and payments can't be dated in the future. Enums are sent and returned by name (any
letter case). Edits, reviews, links and voids take the `rowVersion` of the last read and answer 409
`concurrency_conflict` when someone saved first. A payment carries a client-made `idempotencyKey` (GUID): the same
key again returns the first payment (200) and records nothing; the same key on another contract answers 409.

## Errors

| Status | Code | When |
|---|---|---|
| 400 | `validation_failed` | Field errors, all together (also: value below executed work or payments, model change once work exists, quantity below measured, unit change once measured, unknown company or project item) |
| 403 | `forbidden` | Missing permission |
| 404 | `not_found` | Project not visible, or a child record that is not on this project |
| 409 | `subcontractor_exists`, `subcontractor_inactive`, `already_contracted`, `contract_no_taken`, `contract_completed`, `wrong_contract_model`, `exceeds_contract_quantity`, `exceeds_contract_ceiling`, `exceeds_contract_value`, `item_has_measurements`, `item_inactive`, `item_removed`, `own_record`, `invalid_status`, `idempotency_key_reused`, `payment_already_voided`, `project_completed`, `concurrency_conflict` | Rule conflicts (`item_removed`: the linked project item was deleted while saving) |
| 503 | `busy` | Another change to the same project held its lock for more than 10 seconds; retry |

## Tables (schema `sub`)

- `Subcontractor`: names (unique English name), specialty, contact, phone, e-mail, address, notes, status.
- `SubContract`: company, project, contract number (filtered unique), model, value (> 0), daily rate (exactly on
  `DailyRate`), retention percent (0–100), dates, status, scope. Unique `(ProjectId, SubcontractorId)`.
- `ContractItem`: contract, project item (nullable), name, work type, unit, quantity (> 0), unit price (> 0).
- `ProgressMeasurement`: contract item, date, quantity, copied unit price, amount (CHECK = round(quantity × price)),
  status and review columns (CHECK: reviewed exactly when not `Recorded`; a rejection has a note).
- `ManpowerSheet`: contract, project item (nullable), period, workers, days, daily rate, amount (CHECK = round(workers
  × days × rate)), work type, status and review columns.
- `SubPayment`: contract, date, amount (> 0), method, purpose, reference, notes, idempotency key (unique), void
  columns (all set or none).
- `SubPaymentAllocation`: payment, measurement **or** manpower sheet (CHECK exactly one), amount (> 0). Filtered
  unique `(PaymentId, MeasurementId)` and `(PaymentId, ManpowerSheetId)`.

No foreign key cascades. `SubContract.ProjectId` → `projects.Project`, and `ContractItem.ProjectItemId` and
`ManpowerSheet.ProjectItemId` → `projects.ProjectItem` are written by hand in the migration. **Apply the Access and
Projects migrations before the Subcontractors migrations.**

## Known limits (accepted)

- Approvals can't be undone yet (open question 3). Retention release and advance recovery per certificate are not
  modelled (open question 5); retention is shown on the account only.
- Who approves subcontract work is subcontractors Approve (by default the Executive); see open question 6.
- Lowering a `DailyRate` contract's value below the sheets that are recorded (not yet approved) is allowed; those
  sheets are then refused at approval with `exceeds_contract_ceiling`.
- Accounts are computed in memory from flat rows per project (decision G5). A project has a handful of contracts.
- Until the platform AuditLog table exists, the audit trail is the structured log (`SubcontractorsLog`, event ids
  4001 to 4999), by id only.

## Code layout

- `Features/Companies`, `Features/Contracts`, `Features/ContractItems`, `Features/Work`, `Features/Payments`: one
  class per use case.
- `Features/Accounts`: `ContractAccount`, `ContractTotals`, `SubcontractFigures` and the flat `WorkQueries`.
- `Features/Payments/SubSettlement`: the pure settlement rule; `ContractSettlement` runs it against the database.
- `Features/Work/WorkRules`: review, over-quantity, ceiling and amount rules.
- `Integration`: `ISubcontractItemCosts` and `IProjectCostSource` for Projects.
- `Common`: validation, error responses, the audit log, the caller. `Data`: entities, context, configuration,
  migrations, the project lock. `Startup`: applies migrations when enabled.
