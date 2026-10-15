# Projects module: workspace part 2 (budgets, items and prices, daily site reports, report requests, overview and reports)

Text to merge into `README.md`. Design: `docs/project-workspace/02-projects-planning.md`.

## What it does

- **Budget (`Budget` + `BudgetLine`):** the project's cost estimate, in versions. A line is in one cost group
  (`Labor`, `Subcontractors`, `Materials`, `Equipment`, `SiteExpenses`, `Other`), may name a project item, and is a
  lump sum (`amount`) or priced (`quantity` × `unitPrice`; the server computes the amount to the piastre and
  ignores the client's). Only a `Draft` can change. Approving a draft makes the previous approved version
  `Superseded` in the same transaction, so a project has at most one draft and one approved budget, and an old
  comparison stays true. A budget is a plan: it never touches any cost record.
- **Budget against actual (`BudgetComparison`):** per cost group and in total: budgeted, recognized, variance
  (budgeted − recognized; negative = over budget), variance percent (null when nothing is budgeted), paid,
  outstanding, pending review and committed. Per project item: the item's budget lines against its recognized cost
  from the items ledger. These are never added into one "total".
- **Items and prices (`CatalogItem` + `ItemPrice`):** the project's price list. A price is never overwritten: a
  change is a new `ItemPrice` row (`Pending` → `Approved` or `Rejected`). The current price on a date is the
  approved price with the latest effective date on or before it, so a price approved for a later date becomes
  current by itself. A supply copies its own price; a later price change never changes a supply.
- **Daily site reports (`DailyReport` + lines, materials used, equipment used):** what was done on site on one
  day, by one author. One report per author, project and day. It holds **no money** and creates no cost:
  reviewing a report never creates labor cost or subcontract measurements (decision D2). The detail lists the
  project's supplies dated that day, without prices.
- **Report requests (`ReportRequest`):** one person asks another for one piece of information about one project
  (`Requested` → `InProgress` → `Submitted` → `Completed`, or `Returned` with a note and submitted again, or
  `Cancelled` by the requester). An engineer can file a report nobody asked for (`isProactive`): it is created
  `Submitted`, the author is its recipient and the person it is sent to completes or returns it.
- **Overview and reports:** read-only money pictures of a project. The four money facts stay apart everywhere:
  estimate (budget), commitment (signed, not executed), recognized cost (approved records only) and payments.
  `Materials`, `SiteExpenses` and `Other` come from this module's expense records; `Labor`, `Subcontractors` and
  `Equipment` come **only** from the `IProjectCostSource` of the module that owns them. A module that is not
  installed gives zero and `hasSource: false`. The overview, the budget comparison, the cost summary and the
  obligations all use the same pure classes (`ProjectCosts`, `BudgetComparison`, `ProjectOverview`,
  `Obligations`, plus the first part's `ExpenseSettlement` and `SupplierBalances`), loaded by one
  `ProjectCostLoader`, so they can't disagree.

## Who may do what

| Action | Who | Checked how |
|---|---|---|
| See budgets and the comparison | projects See money, in scope | `ListBudgets`, `GetBudget`, `GetBudgetComparison` |
| Create or edit a draft budget | projects Edit, and an Executive or the project's manager; project not completed | `BudgetRules.PrepareProblem` |
| Approve a budget | projects Approve **and** the Executive role (decision B1) | `BudgetRules.ApproveProblem` |
| See the catalog and prices | catalog View, in scope. Estimated value and amounts bought need catalog See money | `ListCatalogItems`, `GetCatalogItem` |
| Add or edit a catalog item (not its price) | catalog Create; project not completed | `CreateCatalogItem`, `UpdateCatalogItem` |
| Ask for a price change | catalog Edit; new price > 0 and not the current one; reason required; one pending per item | `RequestPriceChange` |
| Approve or reject a price change | catalog Approve **and** the Executive role, never your own request; reject needs a note | `PriceDecision` |
| See daily reports | site View, in scope | `ListDailyReports`, `GetDailyReport` |
| Submit a daily report | site Create, in scope, project not completed; today or one of the 7 days before | `CreateDailyReport` |
| Fix and resubmit | the author only, while Submitted or Returned | `DailyReportRules.Edit` |
| Review or return | site Approve, and an Executive or the project's manager, never the author, only from Submitted | `DailyReportRules.Review` |
| See a report request | an Executive or a Manager (any request of a project they can see), the requester, the recipient. Anyone else gets 404 | `ReportRequestQueries.Visible` |
| Ask for a report | reports Edit, in scope; the recipient must be an active user who can open the project | `CreateReportRequest` |
| File a report nobody asked for | reports Create, in scope; the text is required | `CreateReportRequest` |
| Start, submit | the recipient only | `ReportRequestFlow.Decide` |
| Complete, return | reports Approve, and the requester (or an Executive) | `ReportRequestFlow.Decide` |
| Cancel | the requester only, while Requested, InProgress or Returned; a note is required | `ReportRequestFlow.Decide` |
| Overview and reports | projects See money, in scope | `GetProjectOverview`, `GetCostSummary`, `GetObligations` |

A completed project answers 409 `project_completed` to every write, except from an Executive. A project outside
the caller's scope answers 404 everywhere, the same as a project that doesn't exist.

## Endpoints

| Endpoint | Who may call |
|---|---|
| `GET /api/projects/{pid}/budgets` | projects See money. All versions, newest first, with totals per cost group. `page`, `pageSize` |
| `GET /api/projects/{pid}/budgets/{id}` | projects See money. One version with its lines |
| `POST /api/projects/{pid}/budgets` | projects Edit. `{ title, notes, copyFromBudgetId?, lines? }`. 409 `budget_draft_exists` |
| `PUT /api/projects/{pid}/budgets/{id}` | projects Edit. Replaces title, notes and all lines of a draft; `rowVersion`. 409 `budget_not_draft` |
| `POST /api/projects/{pid}/budgets/{id}/approve` | projects Approve + Executive. `rowVersion`. 409 `budget_empty` |
| `GET /api/projects/{pid}/budget-comparison` | projects See money. `budgetId` to compare another version |
| `GET /api/projects/{pid}/catalog-items` | catalog View. `category`, `status`, `priceStatus` (`Active`, `PendingChange`, `Scheduled`), `search`, paging, summary |
| `GET /api/projects/{pid}/catalog-items/{id}` | catalog View. Item, full price history (newest first), last 10 supplies |
| `POST /api/projects/{pid}/catalog-items` | catalog Create. The first price is approved at once. 409 `catalog_item_exists` |
| `PUT /api/projects/{pid}/catalog-items/{id}` | catalog Create. Names, category, unit, estimated quantity, supplier, status; `rowVersion` |
| `POST /api/projects/{pid}/catalog-items/{id}/price-changes` | catalog Edit. `{ price, effectiveFrom, reason }`. 409 `price_change_pending`, `same_price`, `price_date_taken` |
| `GET /api/projects/{pid}/price-changes` | catalog View. `status`, paging: the approval queue |
| `POST /api/projects/{pid}/price-changes/{priceId}/approve` | catalog Approve + Executive. `{ note? }` |
| `POST /api/projects/{pid}/price-changes/{priceId}/reject` | catalog Approve + Executive. `{ note }` |
| `GET /api/projects/{pid}/daily-reports` | site View. `status`, `from`, `to`, `authorUserId`, paging |
| `GET /api/projects/{pid}/daily-reports/{id}` | site View |
| `POST /api/projects/{pid}/daily-reports` | site Create. 409 `report_exists` |
| `PUT /api/projects/{pid}/daily-reports/{id}` | site Create, the author. `rowVersion`. It becomes Submitted again |
| `POST /api/projects/{pid}/daily-reports/{id}/review` | site Approve, Executive or manager. `{ note?, rowVersion }` |
| `POST /api/projects/{pid}/daily-reports/{id}/return` | site Approve, Executive or manager. `{ note, rowVersion }` |
| `GET /api/projects/{pid}/report-requests` | reports View. `status`, `mine=true`, paging |
| `GET /api/projects/{pid}/report-requests/{id}` | reports View, and a party (or Executive / Manager) |
| `POST /api/projects/{pid}/report-requests` | reports Edit (ask) or reports Create (`isProactive: true`, with `sendToUserId` and `responseText`) |
| `POST /api/projects/{pid}/report-requests/{id}/start` | reports View, the recipient. `rowVersion` |
| `POST /api/projects/{pid}/report-requests/{id}/submit` | reports View, the recipient. `{ responseText, rowVersion }` |
| `POST /api/projects/{pid}/report-requests/{id}/complete` | reports Approve, the requester or an Executive. `rowVersion` |
| `POST /api/projects/{pid}/report-requests/{id}/return` | reports Approve, the requester or an Executive. `{ note, rowVersion }` |
| `POST /api/projects/{pid}/report-requests/{id}/cancel` | reports View, the requester. `{ note, rowVersion }` |
| `GET /api/projects/{pid}/overview` | projects See money. Approved budget, per cost group budgeted / recognized / paid / outstanding / pending review / committed / remaining / percent used, totals, items totals and not-linked total, counts (active items, returned records, records waiting for review) |
| `GET /api/projects/{pid}/reports/cost-summary?from=&to=` | projects See money. Per cost group in the period (by the records' business date): recognized, paid, outstanding, pending review, record counts; totals |
| `GET /api/projects/{pid}/reports/obligations` | projects See money. Supplier balances, approved records with no supplier, labor / subcontractors / equipment recognized, paid and outstanding, total outstanding |

## Errors

Besides the shared ones (`validation_failed`, `forbidden`, `not_found`, `concurrency_conflict`,
`project_completed`, `busy`, `own_record`, `invalid_status`):

| Code | When |
|---|---|
| `budget_draft_exists` | The project already has a draft budget |
| `budget_not_draft` | An approved or superseded budget can't change |
| `budget_empty` | A budget with no lines can't be approved |
| `catalog_item_exists` | The catalog already has an item with this name |
| `price_change_pending` | The item already has a price change waiting |
| `same_price` | The new price equals the current one |
| `price_date_taken` | An approved price of the item already starts on that date |
| `report_exists` | You already filed a daily report for this project and day |

## Tables (schema `projects`)

| Table | Key rules |
|---|---|
| `Budget` | Unique `(ProjectId, Version)`; filtered unique: one `Draft`, one `Approved` per project; approval columns set exactly when not Draft |
| `BudgetLine` | Unique `(BudgetId, LineNo)`; `Amount > 0`; lump sum (no quantity, no price) or priced (both, and amount = round(quantity × price, 2)) |
| `DailyReport` | Unique `(ProjectId, ReportDate, AuthorUserId)`; review columns by status; no money column |
| `DailyReportLine` | `Quantity > 0`; internal lines name no subcontractor, subcontractor lines name one (by name or contract id) |
| `DailyReportMaterial`, `DailyReportEquipment` | Materials used and equipment used that day; `Hours` (0, 24] |
| `ReportRequest` | Requester ≠ recipient; each status carries the facts that led to it (answer, review, return note, cancel note) |

`CatalogItem` and `ItemPrice` are created by the shared foundation (see the first part).
`DailyReportLine.SubContractId` and `DailyReportEquipment.EquipmentId` are ids of other modules' rows, with no
foreign key. `DailyReport.AuthorUserId`, `ReportRequest.RequesterUserId` and `ReportRequest.RecipientUserId` get
hand-written foreign keys to `access.User` in the migration. No foreign key cascades.

## Known limits

- Export to Excel or PDF is not built: the prototype exports in the browser from the same rows.
- The cost summary filters by the records' business date; what is paid on them is counted whenever it was paid.
  Commitments are not in the cost summary (a contract has no date in a period); the overview shows them.
- "Returned records" on the overview counts this module's expenses and supplies only; other modules report no
  returned counts through `IProjectCostSource`.
- Photos and attachments of daily reports and report requests are Archive documents; this module stores none.
- No notifications are sent when a request is created, answered or returned.
