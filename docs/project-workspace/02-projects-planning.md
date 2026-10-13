# Projects module, part 2: budget, items and prices, daily site reports, report requests, overview and reports

Schema `projects`. Read `00-overview.md` first.

## A. Cost estimate and budget (permission module `projects`)

The prototype has one typed budget number per project. The ERD has versioned budgets. This follows the ERD and
adds quantities and unit prices per line, as the requirement asks.

### Tables

`Budget`: `ProjectId`, `Version` (1, 2, 3…), `Status` (`Draft`, `Approved`, `Superseded`), `Title` nvarchar(200) null,
`Notes` nvarchar(1000) null, `ApprovedAt`, `ApprovedBy`, `RowVersion`.
Unique `(ProjectId, Version)`. Filtered unique: one `Draft` per project, one `Approved` per project.

`BudgetLine`: `BudgetId`, `LineNo`, `CostGroup` (one of `CostGroups`: `Labor`, `Subcontractors`, `Materials`,
`Equipment`, `SiteExpenses`, `Other`), `ProjectItemId` FK null (same project), `Description` nvarchar(300),
`Unit` varchar(20) null, `Quantity` decimal(18,3) null, `UnitPrice` decimal(18,2) null, `Amount` decimal(18,2) > 0.
Check: quantity and unit price are both empty (a lump sum) or both set; then the server sets
`Amount = round(Quantity × UnitPrice, 2)`.

### Rules

- A budget is never overwritten. A revision is a new version. Approving version N makes the previous approved
  version `Superseded`, in one transaction under the project lock.
- Only a `Draft` can be edited. An approved or superseded budget never changes, so old comparisons stay true.
- Changing a budget never touches any transaction.
- Create and edit a draft: `projects` Edit, and Executive or the project's manager, project not completed.
- Approve: `projects` Approve **and** the Executive role (decision B1 in `07-decisions.md`).
- Read: `projects` SeeMoney.

### Endpoints

| Endpoint | Notes |
|---|---|
| `GET /projects/{pid}/budgets` | All versions, newest first, with totals per cost group |
| `GET /projects/{pid}/budgets/{id}` | One version with its lines |
| `POST /projects/{pid}/budgets` | New draft: `{ title, notes, copyFromBudgetId?, lines? }`. 409 `budget_draft_exists` |
| `PUT /projects/{pid}/budgets/{id}` | Replace title, notes and all lines of a draft; `rowVersion`. 409 `budget_not_draft` |
| `POST /projects/{pid}/budgets/{id}/approve` | `rowVersion`. A budget with no lines can't be approved |
| `GET /projects/{pid}/budget-comparison` | The approved budget against recognized cost (below). `budgetId` to compare another version |

### Budget against actual (pure class `BudgetComparison`)

Per cost group, and in total:

```
budgeted        = Σ lines of the group
recognized      = approved cost of the group      (never requests, never rejected records)
variance        = budgeted − recognized            (negative = over budget)
variancePercent = budgeted = 0 ? null : variance ÷ budgeted × 100
paid, outstanding = recognized − paid
pendingReview   = recorded or submitted, not approved yet (shown apart, not in recognized)
committed       = signed subcontract value not executed yet (Subcontractors only)
```

Where "recognized" comes from: `Materials` = approved `Supply` records. `SiteExpenses` = approved `Expense`
records except categories `Other` and `PettyCash`. `Other` = approved `Expense` records in those two categories.
`Labor`, `Subcontractors`, `Equipment` = `IProjectCostSource` of the module with that `Group`.

Per project item: lines that have `ProjectItemId` are summed and compared with the item's `totalCost` from
`ItemsLedger`. The six numbers are returned separately and are never added into one "total".

## B. Items and prices: the project price catalog (permission module `catalog`)

### Tables (created by the foundation)

`CatalogItem`: `ProjectId`, `NameAr`, `NameEn` (unique per project), `Category` (`CementAggregates`, `ReadyMix`,
`Steel`, `Masonry`, `Finishing`, `Waterproofing`, `Piling`, `Formwork`, `Consumables`, `Services`, `Other`),
`Unit`, `SupplierId` FK null, `EstimatedQuantity` decimal(18,3) ≥ 0, `Status` (`Active`, `Inactive`), `RowVersion`.

`ItemPrice`: `CatalogItemId`, `Price` > 0, `PreviousPrice` null, `EffectiveFrom` date, `Status` (`Pending`,
`Approved`, `Rejected`), `Reason` nvarchar(500), `RequestedAt`, `RequestedBy`, `DecidedAt`, `DecidedBy`,
`DecisionNote` nvarchar(500). Insert-only: a row changes once, when it is decided.
Filtered unique: one `Pending` per item; one `Approved` per `(CatalogItemId, EffectiveFrom)`.

### Rules

- Current price on a date = the approved price with the latest `EffectiveFrom` on or before that date
  (`CatalogPrices.CurrentAsync`, in the foundation). A price approved for a future date becomes current on that
  date by itself. Nothing is overwritten.
- A supply copies its own unit price and `CatalogPriceAtRecording`. A later price change never changes a supply.
- Add an item: `catalog` Create. Its first price is approved at once (as in the prototype).
- Edit an item (names, category, unit, estimated quantity, supplier, status): `catalog` Create. Not the price.
- Request a price change: `catalog` Edit; new price > 0 and different from the current price; reason required;
  no other pending request for the item (409 `price_change_pending`).
- Approve or reject: `catalog` Approve **and** the Executive role, never your own request. Reject needs a note.
- Prices are shown to everyone with `catalog` View. Estimated value and amounts bought need `catalog` SeeMoney.

### Endpoints

| Endpoint | Notes |
|---|---|
| `GET /projects/{pid}/catalog-items` | `category`, `status`, `priceStatus` (`Active`, `PendingChange`, `Scheduled`), `search`, paging. Each row: current price, previous price, change %, pending request, scheduled price, estimated quantity and value, quantity and amount bought (approved supplies linked to the item). Summary: count, estimated value, bought, pending count |
| `GET /projects/{pid}/catalog-items/{id}` | Item, full price history (newest first), last 10 supplies |
| `POST /projects/{pid}/catalog-items` | `{ nameAr, nameEn, category, unit, price, effectiveFrom?, estimatedQuantity?, supplierId? }` |
| `PUT /projects/{pid}/catalog-items/{id}` | `rowVersion` |
| `POST /projects/{pid}/catalog-items/{id}/price-changes` | `{ price, effectiveFrom, reason }` |
| `POST /projects/{pid}/price-changes/{priceId}/approve` | Executive |
| `POST /projects/{pid}/price-changes/{priceId}/reject` | Executive; `{ note }` |
| `GET /projects/{pid}/price-changes` | `status`; the approval queue of the project |

## C. Daily site reports (permission module `site`)

A daily report says what was done on site on one day. It holds **no money** and it is not the labor sheet:
paid attendance lives in Workforce, measured subcontract work lives in Subcontractors.

### Tables

`DailyReport`: `ProjectId`, `ReportDate`, `AuthorUserId`, `Status` (`Submitted`, `Reviewed`, `Returned`),
`WorkersPresent` int null, `Weather` nvarchar(100) null, `Issues` nvarchar(2000) null, `Notes` nvarchar(2000) null,
`SubmittedAt`, `ReviewedAt`, `ReviewedBy`, `ReviewNote` nvarchar(500), `ReportRequestId` FK null, `RowVersion`.
Unique `(ProjectId, ReportDate, AuthorUserId)`: one report per author, project and day (ERD rule).

`DailyReportLine` (work done): `ReportId`, `LineNo`, `WorkType`, `Description` nvarchar(500), `Unit`,
`Quantity` decimal(18,3) > 0, `PerformedBy` (`Internal`, `Subcontractor`), `ProjectItemId` FK null (same project),
`SubcontractorName` nvarchar(200) null, `SubContractId` int null (a reference by id; the row belongs to the
Subcontractors module, so there is no foreign key), `WorkersCount` int null.

`DailyReportMaterial` (materials used): `ReportId`, `MaterialName` nvarchar(200), `Unit`, `Quantity` > 0.

`DailyReportEquipment` (equipment used): `ReportId`, `EquipmentId` int null (a reference by id; Fleet owns it),
`EquipmentName` nvarchar(200), `Hours` decimal(5,1) null.

Photos are Archive documents linked to the report (`entityType = DailyReport`).
Materials received that day are not stored again: the report detail lists the project's supplies dated that day
(name, quantity, unit, supplier; no prices).

### Rules

- Create: `site` Create, scope, project not completed. `ReportDate` is today or one of the 7 days before.
  A second report by the same author for the same project and day answers 409 `report_exists`.
- A report needs at least one work line or an issue text. Every work line needs a description and a quantity > 0.
  A subcontractor line needs a subcontractor name or a contract id.
- Edit (`PUT`): only the author, while `Submitted` or `Returned`. It becomes `Submitted` again.
- Review and return: `site` Approve, and Executive or the project's manager, never the author, only from
  `Submitted`. Return needs a note. `Reviewed` is final.
- Reviewing a report does **not** create subcontract measurements or labor cost (decision D2).

### Endpoints

`GET /projects/{pid}/daily-reports` (`status`, `from`, `to`, `authorUserId`, paging; `site` View),
`GET /projects/{pid}/daily-reports/{id}`, `POST /projects/{pid}/daily-reports`,
`PUT /projects/{pid}/daily-reports/{id}`, `POST …/daily-reports/{id}/review`, `POST …/daily-reports/{id}/return`.

## D. Report requests (permission module `reports`)

A request asks one person for one piece of information about one project. An engineer can also file a report
without being asked.

`ReportRequest`: `ProjectId`, `RequesterUserId`, `RecipientUserId`, `Type` (`DailyReport`, `Labor`, `Materials`,
`Subcontractor`, `Expense`, `Equipment`, `Other`), `Title` nvarchar(200), `Description` nvarchar(2000) null,
`DueDate` null, `Priority` (`Normal`, `High`, `Urgent`), `Status` (`Requested`, `InProgress`, `Submitted`,
`Returned`, `Completed`, `Cancelled`), `IsProactive`, `ResponseText` nvarchar(4000) null, `StartedAt`,
`SubmittedAt`, `ReviewedAt`, `ReviewedBy`, `ReturnNote`, `CancelNote` nvarchar(500), `RowVersion`.

```
Requested ─start─► InProgress ─submit─► Submitted ─complete─► Completed
     └──────────── submit ───────────────┘   │
                                             └─return (note)─► Returned ─submit─► Submitted
Requested / InProgress / Returned ─cancel (note, requester only)─► Cancelled
Proactive report: created as Submitted by its author (who is the recipient)
```

- Who sees a request: an Executive; the requester; the recipient; a Manager for projects they can see.
  Anyone else gets 404.
- Create a request: `reports` Edit, scope. The recipient must be an active user who can see the project.
- File a proactive report: `reports` Create, scope; the response text is required.
- Start and submit: the recipient only. Complete and return: `reports` Approve, and the requester (or an Executive).
- Attachments are Archive documents linked to the request (`entityType = ReportRequest`).

Endpoints: `GET /projects/{pid}/report-requests` (`status`, `mine=true`, paging),
`GET …/report-requests/{id}`, `POST …/report-requests`, `POST …/report-requests/{id}/start|submit|complete|return|cancel`.

## E. Overview and reports (permission module `projects`, SeeMoney)

Read-only. All figures come from the same pure classes as the screens above.

| Endpoint | Returns |
|---|---|
| `GET /projects/{pid}/overview` | Approved budget total, recognized cost per cost group, paid, outstanding, pending review, committed, remaining budget (budget − recognized), percent used, items totals and the not-linked total, counts (active items, open returned records) |
| `GET /projects/{pid}/reports/cost-summary?from=&to=` | Per cost group in the period: recognized, paid, outstanding, pending review, record counts; totals |
| `GET /projects/{pid}/reports/obligations` | What is owed: supplier balances; labor, subcontractors and equipment recognized, paid and outstanding |

Export (Excel, PDF) is not built: the prototype exports in the browser from the same rows.
