# Fleet module: site equipment

Module `AlMotaheda.Modules.Fleet`, schema `fleet`, permission module `equipment` (and `payments`).
Read `00-overview.md` first. Vehicles, fuel requests and GPS (the rest of the ERD's fleet sheet) are not built.

## 1. Tables

`Equipment` (the company's permanent register; one row per machine): `AssetNo` varchar(30) unique, `NameAr`,
`NameEn` nvarchar(200), `Category` (`Tools`, `Medium`, `Heavy`, `Survey`, `Safety`), `TypeName` nvarchar(100)
(for example "Excavator"), `SerialNo` nvarchar(60) null, `Ownership` (`Owned`, `Rented`), `Condition` (`New`,
`Good`, `Used`, `Damaged`), `Status` (`InService`, `UnderMaintenance`, `Lost`, `Retired`), `Notes`, `RowVersion`.

`EquipmentRental` (rental terms from a lessor, over time): `EquipmentId` FK, `SupplierId` (FK to
`projects.Supplier`), `RateBasis` (`Day`, `Month`, `Hour`), `Rate` > 0, `StartDate`, `EndDate` null, `Notes`.
Filtered unique `(EquipmentId) WHERE EndDate IS NULL`. Only for `Rented` equipment.

`EquipmentAssignment` (the machine on a project, over time): `EquipmentId` FK, `ProjectId` (FK to
`projects.Project`), `ResponsibleUserId` int null (FK to `access.User`), `OperatorName` nvarchar(200) null,
`StartDate`, `EndDate` null, `Notes`, `AssignedAt`, `AssignedBy`, `EndedAt`, `EndedBy`, `RowVersion`.
Filtered unique `(EquipmentId) WHERE EndDate IS NULL`: a machine is on one project at a time.
The same machine moves between projects over time without a second `Equipment` row.

`EquipmentCost`: `EquipmentId` FK, `ProjectId` (FK to `projects.Project`), `CostDate`, `Type` (`Rental`, `Fuel`,
`Maintenance`, `Repair`, `Transport`, `Other`), `Basis` (`Day`, `Month`, `Hour`, `Lump`) null, `Quantity` null,
`Rate` null, `Liters` null, `PricePerLiter` null, `Amount` > 0, `Description` nvarchar(500), `SupplierId` (FK to
`projects.Supplier`) null, `Channel` (`Custody`, `Credit`), `InvoiceNo` nvarchar(50) null, `Status` (`Recorded`,
`Approved`, `Returned`, `Rejected`), `ReviewedAt`, `ReviewedBy`, `ReviewNote`, and the settlement columns
`PaidOn` date null, `PaymentMethod` null, `PaymentReference` null, `PaymentRecordedAt`, `PaymentRecordedBy`,
`RowVersion`.

## 2. Rules

- A cost belongs to the project in its own `ProjectId`, not to where the machine is today. The machine must
  have (or have had) an assignment on that project.
- Amount is set by the server: `Fuel` = round(`Liters` × `PricePerLiter`); a basis other than `Lump` =
  round(`Quantity` × `Rate`); `Lump` = the typed amount. The rate is copied on the cost; rental terms changing
  later never change a recorded cost.
- `Rental` needs a supplier. `Credit` needs a supplier.
- Workflow and approval are the same as for project expenses: `Recorded` → `Approved` / `Returned` / `Rejected`;
  the recorder can't review; return and reject need a note; an approved cost is frozen.
- Paid: `Custody` → paid at approval. `Credit` → paid when its payment is recorded
  (`payments` Create; one payment for the full amount). Removing a wrongly recorded payment needs `payments` Approve.
- **No double counting:** fuel, rental, maintenance and repair of equipment are recorded only here. Project
  expenses have no equipment category, and the cost reports keep `Equipment` as its own cost group.
- A cost may name **one** work item (`projectItemId`, optional): a main item or a sub-item of the same project,
  active when newly chosen (400 when not on the project, 409 `item_inactive`; a cost being fixed may keep an item
  deactivated since). The composite key `(ProjectId, ProjectItemId)` → `projects.ProjectItem (ProjectId, Id)`
  refuses another project's item in the database. A cost is never split over items (decision F1). With no item it
  serves the whole site and the items screen shows it in "other project costs"; on an item it is that item's cost
  (column "Machinery"), and the project total is the same either way.
- Usage: the daily site report lists the equipment used that day (Projects module). Hours and meter readings are
  not in the prototype; the report line has an optional `Hours`.

## 3. Contract implemented for Projects

`IProjectCostSource` with `Group = Equipment`: recognized = approved costs in the period; paid as above;
pending review = `Recorded` costs; committed = 0.

`IEquipmentItemCosts` (`EquipmentItemCosts`): the same rows and arithmetic grouped by work item. One total per item
plus one for the costs on no item (their sum is the Equipment group), the approved lines of some items (the items
detail and the cost breakdown), and "has links" (an item with an equipment cost can't be deleted).

## 4. Endpoints

| Endpoint | Needs | Notes |
|---|---|---|
| `GET /equipment` | `equipment` View | `search` (asset no., names, type, serial), `category`, `ownership`, `status`, `available=true` (in service and not on a project), paging |
| `GET /equipment/{id}` | same | Current rental terms, current project, assignment history (only projects the caller can see) |
| `POST /equipment` | `equipment` Create | `rental: { supplierId, rateBasis, rate, startDate }` for rented equipment. 409 `asset_no_taken` |
| `PUT /equipment/{id}` | `equipment` Edit | `rowVersion` |
| `POST /equipment/{id}/rentals` | `equipment` Edit | New terms; closes the open ones the day before |
| `GET /projects/{pid}/equipment` | `equipment` View, scope | On the project now; `includeEnded` for history |
| `POST /projects/{pid}/equipment` | `equipment` Edit, scope, not completed | `{ equipmentId, responsibleUserId?, operatorName?, startDate?, notes? }`. 409 `equipment_assigned`, `equipment_unavailable` |
| `POST /projects/{pid}/equipment/{assignmentId}/end` | `equipment` Edit, scope | `{ endDate?, rowVersion }` |
| `GET /projects/{pid}/equipment-costs` | `equipment` View, scope | Without `equipment` SeeMoney only the caller's own records. `type`, `status`, `equipmentId`, `from`, `to`, paging |
| `GET /projects/{pid}/equipment-costs/{id}` | same | |
| `POST /projects/{pid}/equipment-costs` | `equipment` Create or Edit, scope, not completed | |
| `PUT /projects/{pid}/equipment-costs/{id}` | the recorder, `Recorded` or `Returned` | `rowVersion` |
| `POST …/equipment-costs/{id}/approve`, `/return`, `/reject` | `equipment` Approve, not the recorder | |
| `POST …/equipment-costs/{id}/payment` | `payments` Create | `{ paidOn, method, reference, rowVersion }`. 409 `already_paid`, `invalid_status` |
| `POST …/equipment-costs/{id}/payment/void` | `payments` Approve | `{ reason, rowVersion }` |
| `GET /projects/{pid}/equipment-summary` | `equipment` SeeMoney, scope | Per machine: rental, fuel, maintenance (with repair), other, total, this month; project totals; pending review; litres |

## 5. Error codes

`asset_no_taken`, `equipment_assigned`, `equipment_unavailable`, `never_on_project`, `supplier_required`,
`own_record`, `invalid_status`, `already_paid`, `not_paid`, `project_completed`, `busy`.

## 6. Tests that must exist

- Amount rules for fuel, per-day, per-month, per-hour and lump costs.
- A machine can't be on two projects at once; ending an assignment keeps history; costs of project A never show in B.
- Only approved costs are recognized; custody is paid at approval; credit only after its payment.
- Every endpoint: 401, 403, 404 for an invisible project.
