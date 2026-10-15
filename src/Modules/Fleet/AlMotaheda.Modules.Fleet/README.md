# Fleet module

Site equipment: the company's equipment register, rental terms, the assignment of machines to projects over time,
and equipment costs (rental, fuel, maintenance, repair, transport, other) with approval and payment. The design is
in `docs/project-workspace/05-fleet.md`; the shared rules are in `00-overview.md` and the decisions in
`07-decisions.md` (G2, G4, G5, F1, F2). Vehicles, fuel requests and GPS are not built.

## What it does

- **Register:** one `Equipment` row per machine for its whole life (asset number unique, upper-case). A machine is
  `Owned` or `Rented`. It is never copied per project.
- **Rental terms:** a rented machine has rental terms from a lessor (a `projects.Supplier`) over time. New terms
  close the open ones the day before they start; nothing is overwritten. A cost copies its own rate, so changing
  terms later never changes a recorded cost.
- **Assignments:** a machine is sent to a project and later leaves it. The open assignment says where it is today;
  a filtered unique index allows one open assignment per machine, so it is on one project at a time. Stays never
  overlap. Ending a stay keeps it as history.
- **Equipment costs:** one row per cost, on the project in its own `ProjectId` (not wherever the machine is today).
  The machine must have, or have had, a stay on that project. The server sets the amount: fuel = litres × price per
  litre; day, month or hour = quantity × rate; lump = the typed amount; rounded to the piastre.
- **Workflow:** `Recorded` → `Approved`, `Returned` or `Rejected`, as for project expenses. The recorder can't
  review. Return and reject need a note. A returned cost is fixed and resubmitted by its recorder. An approved cost
  is frozen.
- **Paid:** `Custody` counts as paid when approved. `Credit` is paid when its one payment for the full amount is
  recorded (F2). A wrong payment is voided; who, when and why stay on the cost. The cost is then unpaid again, so
  the right payment can be recorded. The voided payment is never lost: before the payment columns are emptied,
  the whole payment and its void are copied to a `VoidedEquipmentCostPayment` row, so a second pay and void never
  overwrites the first one (payments are never edited or deleted).
- **No double counting:** fuel, rental, maintenance and repair of equipment are recorded only here. The Projects
  expense form has no equipment category, and the cost reports keep `Equipment` as its own cost group.
- **For the Projects module:** `EquipmentCostSource` implements `IProjectCostSource` with `Group = Equipment`:
  recognized = approved costs in the period, paid as above, pending review = `Recorded` costs, committed = 0.
  Equipment costs are not split over project items (F1); the items screen shows them in "other project costs".
- One rule for the money: `EquipmentCostSettlement` and `EquipmentSummary` (pure classes) are used by the list,
  the detail, the summary and the cost source, so they can't disagree.

## Who may do what

| Action | Needs |
|---|---|
| See the register, a machine, a project's machines and costs | equipment View (project endpoints: the project must be visible) |
| Register a machine | equipment Create |
| Edit a machine, set new rental terms, assign and end assignments | equipment Edit |
| Record a cost | equipment Create **or** Edit (checked in the handler) |
| Fix and resubmit a cost | the recorder, with equipment Create or Edit |
| Approve, return, reject | equipment Approve, not the recorder |
| Record a payment | payments Create (G4) |
| Void a payment | payments Approve (G4) |
| Equipment summary | equipment See money |

- Every project endpoint first asks `IProjectAccess.FindVisibleAsync`: a project the caller may not see answers 404.
- A completed project is read-only (409 `project_completed`) except for an Executive.
- Without equipment See money the cost list shows only the caller's own records (filtered in SQL), and rental rates
  in the register are `null`.
- A machine's detail shows only the stays on projects the caller may see; the others are counted
  (`hiddenHistoryCount`).

## Endpoints

| Endpoint | Needs |
|---|---|
| `GET /api/equipment` | equipment View. `search` (asset no., names, type, serial), `category`, `ownership`, `status`, `available`, `page`, `pageSize` |
| `GET /api/equipment/{id}` | equipment View |
| `POST /api/equipment` | equipment Create. `rental: { supplierId, rateBasis, rate, startDate }` for a rented machine |
| `PUT /api/equipment/{id}` | equipment Edit. `rowVersion` |
| `POST /api/equipment/{id}/rentals` | equipment Edit |
| `GET /api/projects/{pid}/equipment` | equipment View. `includeEnded`, paging |
| `POST /api/projects/{pid}/equipment` | equipment Edit. `{ equipmentId, responsibleUserId?, operatorName?, startDate?, notes? }` |
| `POST /api/projects/{pid}/equipment/{assignmentId}/end` | equipment Edit. `{ endDate?, rowVersion }` |
| `GET /api/projects/{pid}/equipment-costs` | equipment View. `type`, `status`, `equipmentId`, `from`, `to`, paging |
| `GET /api/projects/{pid}/equipment-costs/{id}` | equipment View |
| `POST /api/projects/{pid}/equipment-costs` | equipment Create or Edit |
| `PUT /api/projects/{pid}/equipment-costs/{id}` | the recorder. `rowVersion` |
| `POST /api/projects/{pid}/equipment-costs/{id}/approve` | equipment Approve. `{ note?, rowVersion }` |
| `POST /api/projects/{pid}/equipment-costs/{id}/return` | equipment Approve. `{ note, rowVersion }` |
| `POST /api/projects/{pid}/equipment-costs/{id}/reject` | equipment Approve. `{ note, rowVersion }` |
| `POST /api/projects/{pid}/equipment-costs/{id}/payment` | payments Create. `{ paidOn, method, reference?, rowVersion }` |
| `POST /api/projects/{pid}/equipment-costs/{id}/payment/void` | payments Approve. `{ reason, rowVersion }` |
| `GET /api/projects/{pid}/equipment-costs/{id}/voided-payments` | equipment View, and See money or the recorder (else 404). Every voided payment, newest first |
| `GET /api/projects/{pid}/equipment-summary` | equipment See money |

Cost form: `equipmentId`, `costDate`, `type`, `channel`, and for fuel `liters` and `pricePerLiter`; for other types
`basis` and a `description`, with `quantity` and `rate` (day, month, hour) or `amount` (lump). `supplierId` is
required for rental and for credit. Any amount sent for a non-lump cost is ignored.

Dates are `yyyy-MM-dd`. Enums are sent and returned by name (any letter case is accepted).

## Errors

| Status | Code | When |
|---|---|---|
| 400 | `validation_failed` | Field errors, all together (also: a machine or supplier that doesn't exist, overlapping stays) |
| 403 | `forbidden` | Missing permission; fixing someone else's cost |
| 404 | `not_found` | The project doesn't exist or isn't visible; the machine, assignment or cost isn't there (or is on another project) |
| 409 | `asset_no_taken` | Another machine has this asset number |
| 409 | `equipment_assigned` | The machine is on a project; end that stay first |
| 409 | `equipment_unavailable` | The machine is under maintenance, lost or retired |
| 409 | `never_on_project` | A cost for a machine that was never on this project |
| 409 | `own_record` | Reviewing a cost you recorded |
| 409 | `invalid_status` | The action doesn't fit the status (or channel: custody takes no payment); terms on an owned machine |
| 409 | `already_paid`, `not_paid` | A second payment; voiding when there is none |
| 409 | `project_completed`, `concurrency_conflict` | Read-only project; stale `rowVersion` |
| 503 | `busy` | Another change held the lock for more than 10 seconds; retry |

## Tables (schema `fleet`)

- `Equipment`: asset no. (unique), names, category, type, serial, ownership, condition, status, notes.
- `EquipmentRental`: machine, lessor (`SupplierId`), rate basis, rate (> 0), start, end. One open row per machine.
- `EquipmentAssignment`: machine, project, responsible user, operator name, start, end, who assigned and ended it.
  One open row per machine (`UX_EquipmentAssignment_Open`).
- `EquipmentCost`: machine, project, date, type, basis, quantity, rate, litres, price per litre, amount (> 0),
  description, supplier, channel, invoice no., status, review, payment and payment-void columns. Check constraints
  hold the type/basis columns, the amount formula, the supplier rules, a whole review, a whole payment (approved
  credit only) and a whole void; every nullable column is tested with `IS NULL` / `IS NOT NULL` first.
- `VoidedEquipmentCostPayment`: one row per voided payment of a cost (cost, paid on, method, reference, who and
  when it was recorded, who voided it, when and why). Written at the void, never changed or deleted. Not money:
  a voided payment counts nowhere.
- Foreign keys never cascade. `ProjectId` → `projects.Project`, `SupplierId` → `projects.Supplier` and
  `ResponsibleUserId` → `access.User` are written by hand in the migration. **Apply the Access and Projects
  migrations before the Fleet migrations.**

## Locks

Assigning a machine runs under the machine's application lock; approve, return, reject and payment run under the
project's lock (`AppLock`). The filtered unique indexes are the last guard: a lost race answers 409, never 500.

## Known limits (accepted)

- `GET /api/equipment/{id}` asks `IProjectAccess` once per project in the machine's history (a handful). A batch
  question on the Projects contract would be better.
- Hours and meter readings are not recorded (not in the prototype). The daily site report (Projects module) lists
  the equipment used that day.
- One payment per credit cost, for the full amount (F2). No partial payments, no idempotency key: the `rowVersion`
  makes a retried request harmless (409 `already_paid` or `concurrency_conflict`).
- Custody fund balances are not tracked (overview 3.3).
- Until the platform AuditLog table exists, the audit trail is the structured log (`FleetLog`, event ids 5001 to
  5999), by id only.

## Code layout

- `Features/Register`: `ListEquipment`, `GetEquipment`, `CreateEquipment`, `UpdateEquipment`, `AddEquipmentRental`,
  the form rules (`EquipmentFields`, `RentalTimeline`) and queries.
- `Features/Assignments`: `ListProjectEquipment`, `AssignEquipment`, `EndEquipmentAssignment`, `AssignmentRules`.
- `Features/Costs`: list, detail, create, update, review, payment and summary slices; the pure rules
  (`EquipmentCostAmounts`, `EquipmentCostSettlement`, `EquipmentCostRules`, `EquipmentSummary`) and queries.
- `Integration`: `EquipmentCostSource` (`IProjectCostSource`).
- `Security`, `Common`, `Data` (entities, configuration, migrations), `Startup` (applies migrations when enabled).
