# Projects module, part 1: suppliers, project items, expenses and supplies, supplier payments

Schema `projects`. Permission module `projects` (and `payments` for payments). Read `00-overview.md` first.

## 1. Tables

Every table also has `CreatedAt`, `CreatedBy`, and where it can be edited `UpdatedAt`, `UpdatedBy`, `RowVersion`.

### `Supplier` (company list; already created by the foundation)

| Column | Type | Rule |
|---|---|---|
| `NameAr`, `NameEn` | nvarchar(200) | required; unique index on `NameEn` (`UX_Supplier_NameEn`) |
| `Kind` | enum | `Materials`, `Equipment`, `Services`, `Fuel`, `Other` |
| `Phone` | varchar(30) | optional |
| `TaxNumber` | varchar(30) | optional |
| `Notes` | nvarchar(1000) | optional |
| `Status` | enum | `Active`, `Inactive`. Inactive suppliers can't be chosen for new records |

### `ProjectItem` ("بند"; already created by the foundation)

| Column | Type | Rule |
|---|---|---|
| `ProjectId` | FK Project | |
| `NameAr`, `NameEn` | nvarchar(200) | unique per project (`UX_ProjectItem_NameAr`, `UX_ProjectItem_NameEn`) |
| `Category` | varchar(30) | one of `WorkTypes` |
| `Unit` | varchar(20) | one of `MeasurementUnits` |
| `Description`, `Notes` | nvarchar(1000) | optional |
| `Status` | enum | `Active`, `Inactive` |
| `DeactivatedAt`, `DeactivatedBy` | | set together, only while `Inactive` |

An item with any linked record is never deleted, only deactivated. Its records stay linked and stay in totals.
New records can't be linked to an inactive item.

### `ProjectExpense` (supplies and site expenses; replaces the ERD's `SiteExpense`)

| Column | Type | Rule |
|---|---|---|
| `ProjectId` | FK Project | |
| `Kind` | enum | `Supply` (materials delivered by a supplier) or `Expense` (site expense) |
| `ExpenseDate` | date | not in the future |
| `SupplierId` | FK Supplier, null | required for `Supply` and for `Credit` |
| `ItemName` | nvarchar(200), null | `Supply` only, required |
| `Unit` | varchar(20), null | `Supply` only, required, one of `MeasurementUnits` |
| `Quantity` | decimal(18,3), null | `Supply` only, > 0 |
| `UnitPrice` | decimal(18,2), null | `Supply` only, > 0. Copied here; never looked up later |
| `Category` | enum, null | `Expense` only: `FeesTransport`, `Lab`, `Survey`, `Consumables`, `PieceWork`, `Guards`, `Accommodation`, `PettyCash`, `Permits`, `Other` |
| `Description` | nvarchar(500), null | required for `Expense` |
| `Amount` | decimal(18,2) | > 0. For `Supply` = round(`Quantity` × `UnitPrice`, 2), set by the server |
| `Channel` | enum | `Custody` or `Credit` (see overview 3.3) |
| `InvoiceNo`, `DeliveryNoteNo` | nvarchar(50), null | `InvoiceNo` required for a `Supply` on `Credit` |
| `CatalogItemId` | FK CatalogItem, null | optional, same project |
| `CatalogPriceAtRecording` | decimal(18,2), null | the approved catalog price on `ExpenseDate`, copied by the server |
| `ProjectItemId` | FK ProjectItem, null | the link to a project item; same project |
| `ItemLinkedAt`, `ItemLinkedBy` | | who set the current link |
| `Status` | enum | `Recorded`, `Approved`, `Returned`, `Rejected` |
| `ReviewedAt`, `ReviewedBy`, `ReviewNote` nvarchar(500) | | set by approve, return, reject |
| `Notes` | nvarchar(1000), null | |

Check constraints: `Amount > 0`; kind-specific required columns; `Channel = 'Custody' OR SupplierId IS NOT NULL`;
status list; reviewed columns set together.
Indexes: `(ProjectId, ExpenseDate)`, `(ProjectId, Status)`, `(ProjectItemId)`, `(SupplierId)`.

### `SupplierPayment`

| Column | Type | Rule |
|---|---|---|
| `ProjectId` | FK Project | every allocation is on a record of this project |
| `SupplierId` | FK Supplier | |
| `PaymentDate` | date | not in the future |
| `Amount` | decimal(18,2) | > 0, and equal to the sum of its allocations |
| `Method` | enum | `BankTransfer`, `Cheque`, `Cash` |
| `Reference` | nvarchar(100), null | bank or cheque reference |
| `Notes` | nvarchar(500), null | |
| `IdempotencyKey` | uniqueidentifier | unique (`UX_SupplierPayment_IdempotencyKey`) |
| `VoidedAt`, `VoidedBy`, `VoidReason` nvarchar(300) | | set together |

### `SupplierPaymentAllocation`

`PaymentId` FK, `ExpenseId` FK, `Amount` decimal(18,2) > 0. Unique `(PaymentId, ExpenseId)`. Index on `ExpenseId`.

## 2. Rules

### Expense workflow

```
(create) ─► Recorded ─approve─► Approved        (cost recognized; frozen)
               │
               ├─return (note)─► Returned ─fix and resubmit by the recorder─► Recorded
               └─reject (note)─► Rejected       (final; never a cost)
```

- Create: `projects` Create, project visible, project not completed (Executive excepted).
- Fix and resubmit (`PUT`): only the recorder, only while `Recorded` or `Returned`. Sets `Recorded`, clears the review.
- Approve, return, reject: `projects` Approve, not the recorder, only from `Recorded`.
- After approval only `ProjectItemId` can change.

### What counts

- Recognized cost of an expense = `Amount` when `Status = Approved`, else 0.
- Paid: `Custody` → `Amount` when approved. `Credit` → sum of allocations of payments that are not voided.
- Outstanding = recognized − paid. Payment state: `Paid` (outstanding = 0), `PartiallyPaid` (0 < paid < amount), `Unpaid`.

### Supplier payment

Checked in one transaction under the project lock:

1. `payments` Create; project visible.
2. At least one allocation; no expense twice; all amounts > 0; sum of allocations = `Amount`.
3. Every expense: this project, this supplier, `Approved`, `Credit`.
4. For every expense: already allocated (not voided) + new amount ≤ `Amount` of the expense. Else 409 `over_allocation`.
5. Same `idempotencyKey` again → return the first payment (200), create nothing. Same key with a different project
   → 409 `idempotency_key_reused`.

Void: `payments` Approve, reason required, `rowVersion`. A voided payment frees its expenses again.

### Project items: where a supplier record lands

A supplier-side record is in the items screens when it is `Approved` **and** (it is a `Supply`, or it has a
supplier, or it is linked to an item). Site expenses with no supplier and no link are "other project costs":
they are in the project total but not in the items table and not in the "not linked" list.

- Linked (`ProjectItemId` set) → that item's row.
- Not linked → the "غير مربوط ببند بعد" (not linked to an item yet) group.

Linking (`PUT …/expenses/{id}/item`): `projects` Edit or `projects` Approve; item must be `Active` and in the same
project; `null` removes the link. It changes only the link, never an amount, and never creates a row.

### The items table (pure class `ItemsLedger`)

For every item, from three sources that never overlap:

```
supplierCost       = Σ approved supplier-side records linked to the item           (this module)
subcontractorCost  = ISubcontractItemCosts.TotalsByItemAsync                       (Subcontractors)
laborCost          = ILaborItemCosts.TotalsByItemAsync                             (Workforce)
supplierAndSubcontractorCost = supplierCost + subcontractorCost
totalCost          = supplierAndSubcontractorCost + laborCost
paid               = supplierPaid + subcontractorPaid + laborPaid
outstanding        = totalCost − paid
```

- `totals` = the same sums over **all** items of the project (active and inactive), whatever the search is.
- `unallocated` = the same figures for records with no item. It is never inside `totals`.
- Reconciliation: `projectRecognizedCost = totals.totalCost + unallocated.totalCost + otherCosts`, where
  `otherCosts` = site expenses outside the items screens + equipment costs (`IProjectCostSource` of Fleet).
  The response returns all four numbers so the screen can show where the project total comes from.

The item detail and the "not linked" detail use the same loaded rows and the same class, so the totals at the
bottom of a detail always equal the row in the table.

## 3. Endpoints

All under `/api`. "Scope" = project visible, else 404. "Not completed" = 409 `project_completed` unless Executive.

### Suppliers

| Endpoint | Needs | Notes |
|---|---|---|
| `GET /suppliers` | `projects` View | `search` (names, phone), `status`, `kind`, `page`, `pageSize` |
| `POST /suppliers` | `projects` Create | 409 `supplier_exists` on the same English name |
| `PUT /suppliers/{id}` | `projects` Edit | `rowVersion` |

### Project items

| Endpoint | Needs | Notes |
|---|---|---|
| `GET /projects/{pid}/items` | `projects` SeeMoney, scope | `search` (names; Arabic placeholder "بحث في البنود…"), `includeInactive`. Returns `items`, `totals`, `unallocated`, `reconciliation` |
| `GET /projects/{pid}/items/{itemId}` | same | Item, `supplierLines`, `subcontractLines`, `laborLines`, `pendingSupplierLines`, `totals` |
| `GET /projects/{pid}/items/unallocated` | same | Same shape, for records with no item |
| `POST /projects/{pid}/items` | `projects` Edit, scope, Executive or the project's manager, not completed | 409 `item_name_taken` |
| `PUT /projects/{pid}/items/{itemId}` | same | `rowVersion` |
| `POST …/items/{itemId}/deactivate`, `/reactivate` | same | `rowVersion` |
| `DELETE …/items/{itemId}` | same | Only with no linked record anywhere, else 409 `item_has_records` |

Each row of `items`: `id, nameAr, nameEn, category, unit, status, deactivatedAt, supplierRecordCount,
supplierCount, subcontractRecordCount, subcontractorCount, laborRecordCount, workerCount, linkedRecordCount
(supplier + subcontract records), supplierCost, subcontractorCost, supplierAndSubcontractorCost, laborCost,
totalCost, supplierPaid, subcontractorPaid, laborPaid, paid, outstanding`.

Supplier line: `expenseId, kind, date, supplierId, supplierNameAr, supplierNameEn, supplierKind, description
(item name or description), invoiceNo, deliveryNoteNo, quantity, unit, unitPrice, amount, channel, status, paid,
outstanding, paymentState, payments [{ paymentId, date, method, reference, amount }], rowVersion`.

### Expenses and supplies

| Endpoint | Needs | Notes |
|---|---|---|
| `GET /projects/{pid}/expenses` | `projects` View, scope | Without `projects` SeeMoney the caller sees only records they recorded. Filters: `kind`, `status`, `channel`, `supplierId`, `itemId`, `unlinked=true`, `paymentState`, `from`, `to`, `search` (item name, description, invoice no., delivery note no., supplier name), `sort` (`date` newest first, `amount`, `status`), paging. Also returns `summary` for the whole project (SeeMoney only) |
| `GET /projects/{pid}/expenses/{id}` | same | With its payment allocations |
| `POST /projects/{pid}/expenses` | `projects` Create, scope, not completed | Records a supply or a site expense as `Recorded` |
| `PUT /projects/{pid}/expenses/{id}` | the recorder, `Recorded` or `Returned` | Fix and resubmit; `rowVersion` |
| `POST …/expenses/{id}/approve` | `projects` Approve, not the recorder | `rowVersion` |
| `POST …/expenses/{id}/return` | same | `note` required |
| `POST …/expenses/{id}/reject` | same | `note` required |
| `PUT …/expenses/{id}/item` | `projects` Edit or Approve, scope, not completed | `{ itemId, rowVersion }` |

`summary`: `suppliesRecognized, expensesRecognized, recognizedTotal, paid, outstanding, pendingReviewCount,
pendingReviewAmount, returnedCount`.

### Supplier payments

| Endpoint | Needs | Notes |
|---|---|---|
| `GET /projects/{pid}/supplier-payments` | `projects` SeeMoney, scope | `supplierId`, `includeVoided`, paging |
| `POST /projects/{pid}/supplier-payments` | `payments` Create, scope | See rules. 201, or 200 on an idempotent repeat |
| `POST …/supplier-payments/{id}/void` | `payments` Approve, scope | `{ reason, rowVersion }` |
| `GET /projects/{pid}/supplier-balances` | `projects` SeeMoney, scope | Per supplier: `creditRecognized, custodyRecognized, paid, outstanding, openInvoices` |

## 4. Error codes added

`supplier_exists`, `item_name_taken`, `item_has_records`, `item_inactive`, `own_record`, `invalid_status`
(the record is not in a status that allows this), `over_allocation`, `idempotency_key_reused`,
`payment_already_voided`, `project_completed`, `busy`.

## 5. Tests that must exist

- `ItemsLedger`: the nine-row example of the requirement, to the pound: supplier and subcontractor 9,147,350;
  labor 669,819; total 9,817,169; paid 7,613,252; outstanding 2,203,917; and the separate not-linked group
  114,700 + 53,100 = 167,800, paid 164,650, outstanding 3,150. The not-linked group is not inside the totals.
- Linking a not-linked record moves its amounts into the item and out of the not-linked group; the project total
  does not change; no record is added.
- Recorded, returned and rejected records are in no total.
- Custody is paid at approval; credit is paid only through allocations; a voided payment pays nothing.
- A payment can't over-allocate, can't touch another project or another supplier, and can't be applied twice
  with the same key.
- A caller who can't see the project gets 404 on every endpoint here; records of project A never show in project B.
