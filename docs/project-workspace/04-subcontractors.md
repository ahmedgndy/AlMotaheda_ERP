# Subcontractors module

Module `AlMotaheda.Modules.Subcontractors`, schema `sub`, permission module `subcontractors` (and `payments`).
Read `00-overview.md` first.

A subcontractor is a company that does work under a contract. A supplier sells materials or rents equipment.
They are different tables with different workflows and are never merged.

## 1. Tables

`Subcontractor` (company list): `NameAr`, `NameEn` nvarchar(200) (unique `UX_Subcontractor_NameEn`), `Specialty`
(`Civil`, `Masonry`, `Plumbing`, `Electrical`, `Mep`, `Finishing`, `Earthworks`, `Steel`, `Facade`, `Landscape`,
`Other`), `ContactName` nvarchar(200), `Phone` varchar(30), `Email` varchar(200), `Address` nvarchar(400),
`Notes` nvarchar(1000), `Status` (`Active`, `Inactive`), `RowVersion`.

`SubContract` (one company on one project): `SubcontractorId` FK, `ProjectId` (FK to `projects.Project`),
`ContractNo` nvarchar(50) null (filtered unique), `Model` (`UnitRate`, `LumpSum`, `DailyRate`), `Value` > 0
(for `DailyRate` it is the ceiling), `DailyRate` null (required for `DailyRate`), `RetentionPercent` decimal(5,2)
0–100, `StartDate`, `EndDate` null (≥ start), `Status` (`Pending` = not signed yet, `Active`, `Completed`),
`Scope` nvarchar(1000), `RowVersion`. Unique `(ProjectId, SubcontractorId)`: one contract per company per project.
The same company can have contracts on many projects; each keeps its own value, scope and records.

`ContractItem` (a line of contracted work): `ContractId` FK, `ProjectItemId` (FK to `projects.ProjectItem`) null,
`Name` nvarchar(200), `WorkType`, `Unit`, `Quantity` decimal(18,3) > 0, `UnitPrice` decimal(18,2) > 0,
`Description` nvarchar(500), `Notes` nvarchar(1000), `RowVersion`.

`ProgressMeasurement` (measured completed work): `ContractItemId` FK, `MeasuredOn` date, `Quantity` > 0,
`UnitPrice` (copied from the item when recorded), `Amount` = round(`Quantity` × `UnitPrice`), `Status`
(`Recorded`, `Approved`, `Rejected`), `Note` nvarchar(500), `ReviewedAt`, `ReviewedBy`, `ReviewNote`, `RowVersion`.

`ManpowerSheet` (daily-rate crews): `ContractId` FK, `ProjectItemId` (FK to `projects.ProjectItem`) null,
`FromDate`, `ToDate`, `Workers` int > 0, `Days` decimal(18,3) > 0, `DailyRate` > 0,
`Amount` = round(`Workers` × `Days` × `DailyRate`), `WorkType`, `Note`, `Status`, review columns, `RowVersion`.

`SubPayment`: `ContractId` FK, `PaymentDate`, `Amount` > 0, `Method` (`BankTransfer`, `Cheque`, `Cash`),
`Purpose` (`Advance`, `Progress`, `Final`, `Retention`), `Reference`, `Notes`, `IdempotencyKey` unique,
`VoidedAt`, `VoidedBy`, `VoidReason`, `RowVersion`.

`SubPaymentAllocation`: `PaymentId` FK, `MeasurementId` FK null, `ManpowerSheetId` FK null, `Amount` > 0.
CHECK exactly one of the two is set. Filtered unique `(PaymentId, MeasurementId)` and `(PaymentId, ManpowerSheetId)`.

## 2. Rules

### Contract value, executed work, payments: three different numbers

| Number | Meaning |
|---|---|
| Contract value | What was signed. A commitment, not a cost |
| Executed (recognized) | Σ approved measurements + Σ approved manpower sheets. **This is the cost** |
| Paid | Σ payments that are not voided |

A subcontractor amount is recorded once, as measured work or a manpower sheet. It is never entered again as a
project expense.

### Measured work

- Record: `subcontractors` Create or Edit, scope, project not completed, contract not `Completed`.
  For a `Job` or `Lot` unit the request may send `percentOfLine` instead of a quantity.
- The unit price is copied at recording. Changing the item price later only affects new measurements.
- Over-quantity is blocked: measured quantity that is not rejected + the new quantity ≤ the item quantity
  (tolerance 0.0005), at recording and again at approval. Else 409 `exceeds_contract_quantity`.
- Approve or reject: `subcontractors` Approve, never the recorder, only from `Recorded`. Reject needs a note.

### Manpower sheets (contracts of model `DailyRate` only)

- `Amount` is computed by the server. `DailyRate` defaults to the contract's rate.
- Ceiling: amounts not rejected + the new amount ≤ contract `Value`. Else 409 `exceeds_contract_ceiling`.
- Same approval rule as measurements.

### Link to project items

- A `ContractItem` points to one project item (or none). All its approved measurements count in that item.
- A `ManpowerSheet` points to one project item (or none).
- Approved work with no link shows in the project's "not linked to an item yet" group.
- Setting or changing a link: `subcontractors` Edit, scope. The project item must be active and in the same
  project. A link never copies an amount.

### Payments (pure class `SubSettlement`)

- Record: `payments` Create, scope. Not voided payments + the new one ≤ contract `Value`,
  else 409 `exceeds_contract_value`. `idempotencyKey` as in the overview.
- A payment settles the contract's oldest approved, unpaid work first (date, then id). What is left over is an
  **unapplied advance**: it is paid, but it is not yet "paid" against any work.
- When work is approved later, unapplied balances are applied to it, oldest payment first, in the same
  transaction. The same step runs after a payment is voided.
- An allocation can never exceed what is left on the work it settles.
- Void: `payments` Approve, reason required.

### Contract account (pure class `ContractAccount`)

```
executed            = approved measurements + approved manpower
pendingReview       = recorded (not approved) measurements + manpower
remainingToExecute  = max(0, Value − executed)                       (the commitment)
paidTotal           = Σ payments not voided
appliedToWork       = Σ allocations of payments not voided
unappliedAdvance    = paidTotal − appliedToWork
outstandingWork     = executed − appliedToWork
retentionHeld       = Status = Completed ? 0 : round(executed × RetentionPercent ÷ 100)     (shown, see decision S2)
dueNow              = max(0, executed − retentionHeld − paidTotal)
completedPercent    = Value = 0 ? 0 : min(100, executed ÷ Value × 100)
```

For the project items table a work line has `paid = Σ its allocations` and `outstanding = amount − paid`.

## 3. Contracts implemented for Projects

`ISubcontractItemCosts` and `IProjectCostSource` (`Group = Subcontractors`; `Committed` = Σ `remainingToExecute`
of contracts that are not `Completed`). Only approved work counts.

## 4. Endpoints

| Endpoint | Needs | Notes |
|---|---|---|
| `GET /subcontractors` | `subcontractors` View | `search`, `specialty`, `status`, paging |
| `GET /subcontractors/{id}` | same | |
| `POST /subcontractors` | `subcontractors` Create | 409 `subcontractor_exists` |
| `PUT /subcontractors/{id}` | `subcontractors` Edit | `rowVersion` |
| `GET /projects/{pid}/subcontracts` | `subcontractors` View, scope | Each with its account; money `null` without SeeMoney |
| `GET /projects/{pid}/subcontracts/{cid}` | same | Company, contract, items (with measured and remaining quantity), measurements, manpower sheets, payments, account |
| `POST /projects/{pid}/subcontracts` | `subcontractors` Create, scope, not completed | Assigns an existing company. 409 `already_contracted`, `contract_no_taken` |
| `PUT /projects/{pid}/subcontracts/{cid}` | `subcontractors` Edit | `Value` can't go below executed or paid; `Model` can't change once work exists |
| `POST …/subcontracts/{cid}/items` | `subcontractors` Create or Edit | |
| `PUT …/subcontracts/{cid}/items/{itemId}` | same | Quantity can't go below measured |
| `DELETE …/subcontracts/{cid}/items/{itemId}` | `subcontractors` Edit | Only with no measurement. 409 `item_has_measurements` |
| `PUT …/subcontracts/{cid}/items/{itemId}/project-item` | `subcontractors` Edit | `{ projectItemId, rowVersion }` |
| `POST …/subcontracts/{cid}/measurements` | `subcontractors` Create or Edit | `{ contractItemId, measuredOn, quantity or percentOfLine, note }` |
| `POST /projects/{pid}/subcontract-measurements/{id}/approve`, `/reject` | `subcontractors` Approve | `rowVersion`; note for reject |
| `POST …/subcontracts/{cid}/manpower` | `subcontractors` Create | `{ fromDate, toDate, workers, days, dailyRate?, workType, projectItemId?, note }` |
| `POST /projects/{pid}/subcontract-manpower/{id}/approve`, `/reject` | `subcontractors` Approve | |
| `PUT /projects/{pid}/subcontract-manpower/{id}/project-item` | `subcontractors` Edit | |
| `POST …/subcontracts/{cid}/payments` | `payments` Create, scope | `{ paymentDate, amount, method, purpose, reference, notes, idempotencyKey }` |
| `POST /projects/{pid}/subcontract-payments/{id}/void` | `payments` Approve, scope | `{ reason, rowVersion }` |

Every endpoint with an id of a child record checks that the record belongs to `{pid}` and answers 404 if not.

## 5. Error codes

`subcontractor_exists`, `subcontractor_inactive`, `already_contracted`, `contract_no_taken`,
`contract_completed`, `wrong_contract_model`, `exceeds_contract_quantity`, `exceeds_contract_ceiling`,
`exceeds_contract_value`, `item_has_measurements`, `item_inactive`, `own_record`, `invalid_status`,
`idempotency_key_reused`, `payment_already_voided`, `project_completed`, `busy`.

## 6. Tests that must exist

- `ContractAccount`: the three seed checks of the prototype (unit rate, 5 % retention, daily rate).
- `SubSettlement`: oldest work first; an advance stays unapplied and is applied when work is approved; a voided
  payment frees its work and other unapplied money is applied to it; nothing is over-allocated.
- Contract value is never in recognized cost; recorded and rejected work is never in recognized cost.
- Linking a contract item to a project item moves its approved work into that item's figures without a new row.
- Records of contract A on project 1 never appear on project 2, even for the same company.
- Every endpoint: 401, 403, and 404 for an invisible project or a child of another project.
