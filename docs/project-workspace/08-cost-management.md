# Project cost management: work items, price management, breakdown and alerts

Schema `projects` (Projects module), plus small additions to the Workforce and Subcontractors contracts and to the
Platform notification rules. Read `00-overview.md` first: its money rules (3.1 to 3.8) all still hold.

## 1. Main items and sub-items

A project item ("بند") is either a **main item** (`ParentItemId` empty, for example "الأعمال الخرسانية") or a
**sub-item** of a main item (for example "أسمنت", "حديد تسليح", "مقاول الخرسانة"). Two levels only.

| Rule | Where |
|---|---|
| A parent is an item of the same project | Composite foreign key `(ProjectId, ParentItemId)` → `(ProjectId, Id)`; the database refuses a cross-project parent |
| An item is never its own parent | `CK_ProjectItem_Parent` |
| A sub-item can't have sub-items (409 `item_parent_is_sub_item`) | `ItemRules.ParentProblemAsync`, under the project lock |
| A sub-item is added or moved only under an active main item (409 `item_parent_inactive`) | same |
| A main item with sub-items can't become a sub-item or be deleted (409 `item_has_sub_items`) | `MoveProjectItem`, `DeleteProjectItem` |
| A main item is deactivated after its sub-items (409 `item_has_active_sub_items`); a sub-item is reactivated only under an active main item | `ProjectItemStatusChange` |
| Names are unique among siblings: two main items may each have a "Cement" sub-item | `UX_ProjectItem_Parent_NameAr/NameEn` on `(ProjectId, ParentItemId, Name)` with no filter (SQL Server treats NULL parents as equal) |

A record (supply, site expense, labor allocation, contract item, manpower sheet) is linked to **exactly one item**, a
main item or a sub-item. Labor may stay on the main item when the work is not more specific; nothing forces a
sub-item.

**No double counting.** `ItemsLedger` computes each item's **own** figures, as before. `ItemTree.WithSubItems`
adds a main item's sub-items to its own figures for display (`withSubItems` on the row). The project totals are the
sum of the own figures, so a sub-item's record counts once.

Endpoints (projects Edit, Executive or the project's manager, project scope):

| Endpoint | Notes |
|---|---|
| `POST /projects/{pid}/items` | New optional `parentItemId` |
| `PUT /projects/{pid}/items/{id}/parent` | `{ parentItemId (null = make main), rowVersion }`. Only the hierarchy changes; records stay linked |
| `GET /projects/{pid}/items` | Rows in screen order (each main item, then its sub-items); new fields `parentItemId`, `level`, `subItemCount`, `withSubItems` |
| `GET /projects/{pid}/items/{id}` | New `subItems`, `withSubItems` |
| `GET /projects/{pid}/items/options` | New `parentItemId`; same screen order |

Existing items all become main items. Use `PUT …/parent` to organise them.

## 2. The work-item cost breakdown

`GET /projects/{pid}/items/{itemId}/cost-breakdown` (projects See money, project scope). Filters: `from`, `to`,
`subItemId` (the item or one of its sub-items, else 400), `supplierId`, `workerId`, `subcontractorId`,
`equipmentId`, `type` (`Supply`, `DirectPurchase`, `SiteExpense`, `Labor`, `Subcontract`, `Equipment`), `page`,
`pageSize`.

One query per source for the item and all its sub-items (`ExpenseQueries.LinesOfItems`,
`ILaborItemCosts.LinesByItemAsync`, `ISubcontractItemCosts.LinesByItemAsync` and `CommitmentsByItemAsync`,
`IEquipmentItemCosts.LinesByItemAsync`), then the pure class `ItemCostBreakdown`.

| Field | Formula |
|---|---|
| `totals` | The item's row in the items table (with its sub-items). **Never filtered**, so the two screens agree |
| `budget.budgeted` | Σ lines of the **approved** budget whose `ProjectItemId` is the item or a sub-item; 0 without an approved budget |
| `budget.actual` | `totals.totalCost` |
| `budget.remaining` | budgeted − actual; `null` when nothing is budgeted |
| `budget.percentUsed`, `overBudget` | actual ÷ budgeted × 100; remaining < 0 |
| `commitments.subcontractRemaining` | Σ per contract line on the items: max(0, quantity × unit price − approved measured amount), on contracts not completed. Never cost, never paid, never in a total |
| `pending` | Supplier records on the items that are recorded or returned: not cost yet |
| `filtered` | Σ amount, paid, outstanding of the transactions that pass the filters, and the same per type |
| `suppliers[]` | Per supplier: `invoiced` = approved **credit** records (the liability); `custodyPaid` = approved custody records (paid on site); `paid` = custody + allocations of payments not voided; `outstanding` = invoiced − allocations to those invoices; `quantities[]` per unit (physical, never added to money) |
| `workers[]` | Per worker: days on the item (days × portion), cost, paid, outstanding |
| `subcontractors[]` | Per subcontractor: approved work (recognized), paid (allocations), outstanding, committed |
| `machines[]` | Per machine (Fleet): approved equipment costs on the items, paid (custody at approval, credit once its payment is recorded), outstanding. A credit equipment cost is never in `suppliers[]`: it is paid through Fleet |
| `transactions[]` | Every approved record, newest first, with its source id so the screen opens the originating record |

Supporting documents stay in the Archive, linked to the source records (`ProjectExpense`, `LaborSheet`, …); the
breakdown links each transaction to its record, where its documents are listed.

## 3. Price management ("إدارة الأسعار")

> Extended by `10-price-control.md` (Central Price Control): the classification is now a configurable `PriceCategory`
> (same codes), the unit a configurable `PriceUnit`, a range carries a rule kind (range, fixed ± tolerance, lump
> sum), and a record above its range opens a price exception the Executive decides. The rules below still hold.

The company price list replaces the project's own "items and prices" tab in the app. The project catalog tables and
endpoints are **kept** unchanged: Procurement estimates and old supplies use them.

### Tables

`PriceItem`: `NameAr`, `NameEn` (unique in the company), `Classification` (the catalog categories), `Unit` (set
once), `Notes`, `Status` (`Active`, `Inactive`), audit columns, `RowVersion`.

`PriceRange` (insert-only, decided once): `PriceItemId`, `SupplierId` (null = general range), `MinPrice`,
`MaxPrice` (≥ min, > 0), `Currency` (`EGP` only, as everywhere in the system), `EffectiveFrom`, `EffectiveTo`
(optional expiry), `Conditions`, `Status` (`Pending`, `Approved`, `Rejected`), `Reason`, `RequestedAt/By`,
`DecidedAt/By`, `DecisionNote`. One pending request per item and supplier; one approved range per item, supplier and
start date.

### Which range applies (`PriceRanges.ApplicableAsync`)

The supplier's own approved range with the latest `EffectiveFrom` on or before the purchase date; otherwise the
general one. When that newest range has expired (`EffectiveTo` before the date), **none** applies: an expired price
is not silently replaced by an older one.

### Who may do what (permission module `catalog`, the module of the catalog it replaces)

| Action | Needs |
|---|---|
| Read the list, the history, the queue | catalog View |
| Add an item with its first general range (approved at once, like the project catalog) | catalog Create |
| Edit names, classification, notes, status (never the unit or a price) | catalog Create |
| Request a new range (general or supplier-specific), with a reason | catalog Edit |
| Approve or reject a range | catalog Approve **and** an active Executive, checked in the database (`ExecutiveGate`); never your own request (`own_record`) |
| Check a price (`GET /price-items/{id}/check`) | projects Create or catalog View |

Endpoints: `GET/POST /api/price-items`, `GET/PUT /api/price-items/{id}`, `POST /api/price-items/{id}/ranges`,
`GET /api/price-ranges?status=`, `POST /api/price-ranges/{id}/approve|reject`, `GET /api/price-items/{id}/check`.

### The price check of a purchase

A supply may name a price list item (`priceItemId`). On record and on fix-and-resubmit the **server**:

1. Refuses a unit that is not the price item's unit (400 on `unit`): a price per ton can't be compared with bags.
2. Finds the applicable range on the purchase date for the supply's supplier.
3. Compares: `AboveRange` (unit price > max), `BelowRange` (< min), `WithinRange`, or `NoReference`.
   `differencePerUnit` = price − max (above) or price − min (below); `totalDifference` = round(difference × quantity, 2).
4. Copies the range id, min and max and the result onto the record (`ProjectExpense.PriceRangeId`,
   `ReferenceMinPrice`, `ReferenceMaxPrice`, `PriceCheck`). A later range never changes this comparison.
5. Applies the policy when the price is above the range.

Example: range 1,000–1,200 EGP/ton, 2 tons at 1,400 → `AboveRange`, 200 per ton, 400 in total.

### Policy (`Projects:PriceCheckPolicy`)

| Value | Effect above the range |
|---|---|
| `Warn` (**default**) | Saved with the warning; a justification (`priceJustification`) is required; any reviewer with projects Approve (never the recorder) may approve |
| `ExecutiveApproval` | As `Warn`, and only an Executive may approve it (403 `price_needs_executive`); returning or rejecting stays open to any reviewer |
| `Block` | Refused with 409 `price_above_range`; nothing is saved |

Why `Warn` is the default: every supply already needs approval by a second person (01-projects-core §2), so a
warning with a written justification and an alert to the manager and the Executives adds a control without
inventing an approval chain the company has not confirmed. Below the range is a note only, under every policy.
Supplies posted by Procurement (from a matched supplier invoice) are not price-checked here: their price was
chosen by an Executive and matched to the order and the receipt.

## 4. Duplicate submissions

* **Supplies and site expenses:** an optional `idempotencyKey` (GUID) on `POST …/expenses`. The same key from the
  same recorder on the same project returns the first record with 200 and records nothing; from anyone else, or on
  another project, 409 `idempotency_key_reused`. A lost race on `UX_ProjectExpense_IdempotencyKey` answers the
  winner's record. The app sends one key per form.
* **Labor:** unchanged and sufficient: one sheet per engineer, project, day and shift (`UX_DailySheet_Recorder_Day`)
  and one non-void line per worker, day and shift across all projects (`UX_AttendanceLine_Worker_Shift`). A
  repeated sheet answers 409 `sheet_exists` or `worker_already_recorded`; nothing is charged twice.
* **Payments:** unchanged (idempotency keys on every payment request).

## 5. Alerts and notifications

The Platform module turns audited saves into stored notifications through the outbox (`AuditTrailInterceptor` →
`OutboxMessage` → `OutboxDispatcher`), only after the save commits, deduplicated by
`UX_Notification_Source`. New records and rules:

| Event | Record | Recipients | Notification type |
|---|---|---|---|
| A supply or site expense is recorded | `CostRecordedAlert` (one row per recipient, in the record's own save) | The project's active managers, not the recorder | `CostRecorded` |
| A supply is recorded or resubmitted above its range | `PriceAboveRangeAlert` (same) | The project's active managers and every active Executive, not the recorder | `PriceAboveRange` |
| A price range is approved or rejected | `PriceRange` status | The requester | `RecordApproved` / `RecordRejected` |
| A payment request is approved, rejected, paid or failed | `PaymentRequest` status | Whoever asked for it | `RecordApproved`, `RecordRejected`, `PaymentPaid`, `PaymentFailed` |

* One alert per record, kind and recipient (`UX_CostAlert_Expense_Kind_Recipient`): a resubmitted record does not
  notify the same people again.
* Notifications hold no amount and no supplier name; they point to the record (`entityType`, `entityKey`,
  `projectId`). A cost alert's key is the alert; the app opens the project's expenses.
* `GET /api/notifications` returns only the caller's own; someone else's is 404.
* Live updates: `ProjectItem` now routes as a money change (refetch the items table, overview, budget comparison);
  `PriceItem` and `PriceRange` signal the procurement group. Cost alerts are silent (their notification is the signal).

A work-item budget threshold alert is **not** built: the company has no threshold rule yet (final report, open
question 25). The breakdown shows `overBudget` and `percentUsed`.

## 6. Migration `CostManagement` (Projects)

Adds only; no row is changed or removed. `ProjectItem.ParentItemId`, the `(ProjectId, Id)` alternate key, the
composite parent key and the sibling-unique name indexes (replacing the per-project ones); `PriceItem`,
`PriceRange`, `CostAlert` (recipient key into `access.User` written by hand); the price-check and save-key columns
of `ProjectExpense`. Down fails if two sub-items of different main items were given the same name since.

## 7. Not built / open questions

1. A labor "crew" line (N unnamed workers × a rate). Workforce records named workers only (10 workers at 500 are
   10 lines, 5,000 in all). The Subcontractors manpower sheet is the only headcount record.
2. Correction of an approved labor sheet, and cancel / delete of a draft sheet (a draft holds its workers' days).
3. Workforce copies a worker's rate at **approval**, not at recording: a rate edited between the two changes the
   cost of that day. Confirm which the company wants.
4. Subcontract commitments count contracts with status `Pending` (not signed yet), as the existing tests require.
   The contract text says "signed but not executed". Confirm.
5. Copying the existing project catalog items into the company price list. Not done automatically: names may clash
   between projects. Re-enter them in Price management, or ask for a one-off script.
6. Budget threshold alerts (see 5).
