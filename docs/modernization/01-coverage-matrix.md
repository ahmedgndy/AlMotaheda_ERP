# Phase 1 — Frontend ↔ Backend Coverage Matrix

Date: 2026-10-16.

## Sources, and their limits

The React prototype (`alsaleh-prototype66.html`) is not available. As the owner agreed, this audit uses:

1. **The Flutter app** (`~/StudioProjects/al_motaheda_erp_flutter`, 117 Dart files): every screen, form, KPI
   and HTTP call it makes. Its calls were extracted from `lib/features/*/data/*_api.dart` and matched one by one to the
   156 backend routes.
2. **The app's own prototype gap list** (`docs/screens-and-endpoints.md` in the Flutter repo, written when the
   prototype was still available). It names prototype screens that have no backend.
3. **The brief itself** for the new modules (HR, Procurement, Finance, Reports, notifications).

Consequence: rows for prototype screens come from that gap list and the brief, not from reading the prototype.
Field-level details of those screens (exact columns, filters) are **Needs Confirmation** until the prototype or
a screen spec is provided.

Status values: **Covered** · **Partial** (gap explained) · **Missing** · **Unused** (exists in backend, the
app never calls it).

Permission notation: `module:Action` (V View, C Create, E Edit, A Approve, F SeeMoney). "Exec" = Executive-only,
re-checked in the database. Every project route also runs the project-scope check (404 outside scope).

## A. Checks that apply to every row

| Rule from the brief | Result |
|---|---|
| Every number is calculated from records, never typed or stored | **Holds** for every KPI the app shows. Totals are computed in pure classes (`ItemsLedger`, `LaborItemLedger`, `ExpenseSettlement`, `PaymentFifo`, `EquipmentSummary`) over source rows. No total columns exist. The app never adds money itself. |
| Every action button maps to an authorized endpoint | **Holds** for all app buttons (table below). A test checks every `/api` route needs a token and the signed-in policy. |
| Every status shown exists in the backend with allowed transitions | **Holds.** App status lists match backend enums by name (checked: project, expense, sheet, report request, equipment, budget, price). Transitions are in `*Rules` / `*Flow` classes (see refactor plan item M5). |
| Field names and enums match | **Holds.** The app uses the backend's JSON names and the 65 error codes. |

## B. Existing screens (Flutter) — all Covered

| Frontend element | Required data / action | Backend endpoint | Handler | Entity / table | Permission | Status |
|---|---|---|---|---|---|---|
| Login, refresh, logout, change password | Sign-in session | `POST /auth/login`, `/auth/refresh`, `/auth/logout`, `/auth/change-password` | `Login`, `Refresh`, `Logout`, `ChangePassword` | `access.User`, `RefreshToken` | anonymous / signed-in | Covered |
| My account | Profile, my projects | `GET /me`, `GET /projects/memberships` | `GetMe`, `ListUserProjects` | `User`, `ProjectMember` | signed-in, `projects:V` | Covered |
| Users list (search, role, status, sort, paging) | Users | `GET /users` | `ListUsers` | `User` | `accounts:V` | Covered |
| User details, create, edit, disable/enable, reset password | User CRUD | `GET/PUT /users/{id}`, `POST /users`, `/disable`, `/enable`, `/reset-password` | `GetUser`, `CreateUser`, `UpdateUser`, `SetUserStatus`, `ResetPassword` | `User` | Exec (writes) | Covered |
| Permission grid 17×5, reset | Per-user overrides | `GET/PUT /users/{id}/permissions[/{module}]`, `POST …/reset` | `GetUserPermissions`, `SetUserModulePermissions`, `ResetUserPermissions` | `UserPermissionOverride` | Exec | Covered |
| Roles page | Role defaults | `GET /roles`, `PUT /roles/{code}/permissions` | `ListRoles`, `UpdateRolePermissions` | `Role`, `RolePermission` | `accounts:V` / Exec | Covered |
| Projects list + status counts KPI | Projects, `countsByStatus` | `GET /projects` | `ListProjects` | `projects.Project` | `projects:V` | Covered |
| Project create / edit / team | Project + members | `POST/PUT /projects[/{id}]`, `/members`, `/manager`, `/members/{id}/end`, `GET /assignable-users` | `CreateProject`, `UpdateProject`, `AddProjectMember`, `SetProjectManager`, `EndProjectMember`, `ListAssignableUsers` | `Project`, `ProjectMember` | Exec / `projects:E` | Covered |
| Overview tab: financial position per cost group, counts | Calculated figures | `GET /projects/{id}/overview` | `GetProjectOverview` (+ `IProjectCostSource` ×3) | expenses, labor, sub, equipment | `projects:F` | Covered |
| Items (بنود) table: totals, not-linked, reconciliation | Calculated per item | `GET …/items`, `…/items/{id}`, `…/items/unallocated` | `ListProjectItems`, `GetProjectItem` | `ProjectItem` + sources | `projects:F` | Covered |
| Item picker (no money) | Names only | `GET …/items/options` | `ListProjectItemOptions` | `ProjectItem` | `projects:V` | Covered |
| Item create / edit / deactivate / reactivate / delete | Item CRUD | `POST/PUT/DELETE …/items…` | `CreateProjectItem`, `UpdateProjectItem`, `SetProjectItemStatus`, `DeleteProjectItem` | `ProjectItem` | `projects:E` + manager/Exec | Covered |
| Link records to an item | Set link on record | `PUT …/expenses/{id}/item`, `…/subcontracts/{c}/items/{l}/project-item`, `…/subcontract-manpower/{id}/project-item`, `…/labor/lines/{l}/allocations` | `LinkExpenseItem`, `LinkContractItem`, `ReviewManpower`, `SetLineAllocations` | link columns | `projects:C`/`subcontractors:E`/`workers:C` | Covered |
| Expenses & supplies list (kind, status, channel, payment state, unlinked, search, sort, paging) | Records | `GET …/expenses` | `ListExpenses` | `ProjectExpense` | `projects:V` (money hidden without F) | Covered |
| Expense create, fix & resubmit, approve / return / reject | Workflow | `POST …/expenses`, `PUT …/expenses/{id}`, `POST …/{id}/approve\|return\|reject` | `CreateExpense`, `UpdateExpense`, `ReviewExpense` | `ProjectExpense` | `projects:C` / `projects:A` | Covered |
| Suppliers picker + create | Company list | `GET/POST /suppliers` | `ListSuppliers`, `CreateSupplier` | `projects.Supplier` | `projects:V/C` | Covered |
| Supplier balances + payments + void | Calculated balances, payments | `GET …/supplier-balances`, `GET/POST …/supplier-payments`, `POST …/{id}/void` | `GetSupplierBalances`, `ListSupplierPayments`, `RecordSupplierPayment`, `VoidSupplierPayment` | `SupplierPayment`, `…Allocation` | `projects:F`, `payments:C`, `payments:A` | **Partial** — no approval chain (see section D) |
| Budget list / detail / create / edit / approve, budget comparison | Versions, variance | `…/budgets…`, `…/budget-comparison` | `ListBudgets`, `GetBudget`, `CreateBudget`, `UpdateBudget`, `ApproveBudget`, `GetBudgetComparison` | `Budget`, `BudgetLine` | `projects:F/E/A` | Covered |
| Cost summary / obligations cards | Calculated | `GET …/reports/cost-summary`, `…/reports/obligations` | `GetCostSummary`, `GetObligations` | sources | `projects:F` | Covered |
| Catalog + price changes (request, approve, reject) | Items, prices | `…/catalog-items…`, `…/price-changes…` | `ListCatalogItems`, `GetCatalogItem`, `CreateCatalogItem`, `UpdateCatalogItem`, `RequestPriceChange`, `ListPriceChanges`, `DecidePriceChange` | `CatalogItem`, `ItemPrice` | `catalog:V/C/E/A` | Covered |
| Subcontractors tab, subcontract page (contract, lines, measurements, manpower, payments, void) | Contracts, work, payments | `/subcontractors`, `…/subcontracts…`, `…/subcontract-measurements…`, `…/subcontract-manpower…`, `…/subcontract-payments/{id}/void` | `ListSubcontractors`, `CreateSubcontractor`, `ListSubcontracts`, `CreateSubcontract`, `UpdateSubcontract`, `CreateContractItem`, `UpdateContractItem`, `RecordMeasurement`, `ReviewMeasurement`, `RecordManpower`, `ReviewManpower`, `RecordSubPayment` | `sub.*` | `subcontractors:*`, `payments:C/A` | **Partial** — payments have no approval chain |
| Workers / labor tab: labor summary, project workers, sheets | Labor | `…/labor/summary`, `…/workers`, `…/labor/sheets…` | `GetLaborSummary`, `ListProjectWorkers`, `AssignWorker`, `EndAssignment`, `ListSheets`, `GetSheet`, `CreateSheet`, `UpdateSheet`, `SubmitSheet`, `ReviewSheet`, `SetSheetAllocations` | `DailySheet`, `AttendanceLine`, `LaborAllocation` | `workers:*` | Covered |
| Worker picker + create, trades picker | Company lists | `GET/POST /workers`, `GET /trades` | `ListWorkers`, `CreateWorker`, `ListTrades` | `Worker`, `Trade` | `workers:V/E` | Covered |
| Worker account + payments + void | FIFO balance | `GET /workers/{id}/account`, `GET/POST /workers/{id}/payments`, `POST /worker-payments/{id}/void` | `GetWorkerAccount`, `ListWorkerPayments`, `RecordWorkerPayment`, `VoidWorkerPayment` | `WorkerPayment`, `…Allocation` | `workers:F`, `payments:C/A` | **Partial** — per worker, no weekly batch, no approval chain |
| Daily reports tab / page | Site reports | `…/daily-reports…` (`review`, `return`) | `ListDailyReports`, `GetDailyReport`, `CreateDailyReport`, `UpdateDailyReport`, `ReviewDailyReport` | `DailyReport` + lines | `site:V/C/A` | Covered |
| Equipment tab: assignment, summary, costs, cost page, cost payment + void | Equipment | `/equipment`, `…/equipment…`, `…/equipment-summary`, `…/equipment-costs…` | `ListEquipment`, `CreateEquipment`, `AssignEquipment`, `EndEquipmentAssignment`, `GetEquipmentSummary`, `ListEquipmentCosts`, `CreateEquipmentCost`, `UpdateEquipmentCost`, `ReviewEquipmentCost`, `EquipmentCostPayments` | `fleet.*` | `equipment:*`, `payments:C/A` | **Partial** — payment has no approval chain and no idempotency key |
| Archive tab, upload, document page, links, attachments on records | Documents | `…/documents…` (`/content`, `/links`) | `ListDocuments`, `UploadDocument`, `GetDocument`, `GetDocumentContent`, `UpdateDocument`, `ReplaceDocumentLinks`, `DeleteDocument` (soft) | `docs.Document`, `DocumentLink` | `projects:V` + C/E in handler | Covered |
| Report requests tab / page | Operational reporting | `…/report-requests…` (`start`, `submit`, `return`, `complete`, `cancel`) | `ListReportRequests`, `GetReportRequest`, `CreateReportRequest`, `MoveReportRequest` | `ReportRequest` | `reports:V/A` | Covered |
| Bell + "My work" | Work waiting for me | `GET /inbox` | `GetWorkInbox` (+ `IWorkInboxSource` ×4) | computed | signed-in | **Partial** — computed, not persisted; no read/unread; no real-time (Phase 5) |
| CSV export on Items and Expenses | Export | none (client-side) | — | — | — | **Partial** — brief requires server-side Excel/PDF of the full dataset (Phase 4) |
| Role-based navigation | Which tabs a user sees | `GET /me` (permissions) | `GetMe` | `RolePermission` + overrides | signed-in | Covered (contract changes when HR/Purchasing roles are added) |

## C. Backend endpoints the app never calls (Unused)

None of these is a removal candidate: they are master-data maintenance with no screen yet. Recommendation:
**keep** them and add the screens later (frontend work, not in this task).

| Endpoint | Handler | Why keep |
|---|---|---|
| `GET /workers/{id}`, `PUT /workers/{id}` | `GetWorker`, `UpdateWorker` | Fix a worker's name, national id, rate |
| `POST /trades`, `PUT /trades/{id}` | `CreateTrade`, `UpdateTrade` | Trades and default rates |
| `PUT /suppliers/{id}` | `UpdateSupplier` | Supplier data; Procurement will use it |
| `GET /subcontractors/{id}`, `PUT /subcontractors/{id}` | `ListSubcontractors`, `CreateSubcontractor` (update) | Subcontractor data |
| `GET /equipment/{id}`, `PUT /equipment/{id}`, `POST /equipment/{id}/rentals` | `GetEquipment`, `UpdateEquipment`, `AddEquipmentRental` | Equipment register and rentals |
| `GET …/equipment-costs/{id}/voided-payments` | `ListVoidedEquipmentPayments` | Payment history (audit) |

## D. Prototype and brief features with no backend (Missing)

Field-level detail for prototype rows is **Needs Confirmation** (prototype not available).

| Frontend element | Required data / action | Proposed endpoint(s) | Module | Permission | Status |
|---|---|---|---|---|---|
| Payment request → Executive reject / approve / approve & pay → bank → paid | Maker-checker on every payment | `POST /payment-requests`, `POST …/{id}/approve\|reject\|approve-and-pay`, `POST …/{id}/bank-confirmation`, `…/bank-failure` | Finance (new) | `payments:C` (create), `payments:A` (Exec decision), `finance:E` (bank confirm) | Missing |
| Transactions / ledger, reconciliation | One book of record | `GET /ledger`, `GET /reconciliation` | Finance (new) | `finance:V/F` | Missing |
| Weekly payroll batch for workers | Batch of approved labor days → payment request | `POST /payroll-batches`, `…/{id}/submit` | Workforce | `workers:A`, `payments:C` | Missing (Needs Confirmation: week boundaries; replaces per-worker payments?) |
| HR: employees, documents, salary structure, bonuses/deductions/advances, monthly salary sheet | Staff master data and payroll | `/hr/employees…`, `/hr/salary-sheets…` | HR (new) | `hr:*` | Missing |
| Procurement: PR, RFQ/quotations, PO, GRN, supplier invoice, 3-way match | Purchasing cycle | `/procurement/requisitions…`, `/quotations…`, `/purchase-orders…`, `/goods-receipts…`, `/supplier-invoices…` | Procurement (new) | `procurement:*`, `finance:*` | Missing |
| Reports centre + Excel/PDF exports | Report catalog, exports | `GET /reports/catalog`, `GET /reports/{code}`, `POST /reports/{code}/exports` | Reports (new) | per report | Missing |
| Notifications (persisted, unread count) | Stored notifications + live delivery | `GET /notifications`, `POST /notifications/{id}/read`, hub `/hubs/erp` | Notifications (new) | signed-in (own only) | Missing |
| Office expenses vs budget | Office costs | Needs Confirmation | Office (new) | `office:*` | Missing — out of brief's phase list except as a report; **Needs Confirmation** |
| Vehicles, fuel requests, fuel by vehicle/driver | Fuel | Needs Confirmation | Fleet | `fuel:*` | Missing — the fuel report in Phase 4 needs a vehicle/driver model; **Needs Confirmation** |
| Client contract value, certificates, money in | Income | Needs Confirmation | Finance | `income:*` | Missing — "money in" report depends on it; **Needs Confirmation** |
| Custody funds (balance, top-up) | Petty cash | Needs Confirmation | Finance | `custody:*` | Missing (open question 2) |
| GPS attendance, geofence alert | Engineer location | Needs Confirmation | — | `monitoring:*` | Missing (privacy decisions pending; brief lists "engineer left site geofence" as a notification example) |
| Backup settings + history | Executive settings | `GET/PUT /settings/backup`, `POST /settings/backup/run`, `GET /settings/backup/history` | Host / Platform | Exec | Missing |
| Procurement auto-approve policy setting | Executive setting | `GET/PUT /settings/procurement` | Procurement | Exec | Missing |
| Forgot password | Self-service reset | — | Access | — | Missing (needs email/SMS channel; not requested; **Needs Confirmation**) |
| Project progress % on cards | Progress | — | Projects | — | Missing (no rule for how progress is calculated; **Needs Confirmation**) |

## E. What "fix every Missing / Partial" means for the plan

Section D is Phases 3–7 of the brief. The Partial rows in section B come down to two pieces of work:

1. **Payments go through Finance** (payment request → Executive → bank → paid → ledger). It covers supplier,
   worker/payroll, subcontractor, equipment and salary payments. This changes the behaviour of 4 existing
   endpoints (see refactor plan M1).
2. **Inbox → persisted notifications + SignalR** (Phase 5) and **server-side exports** (Phase 4).

The rows marked Needs Confirmation in section D (office, fuel, income, custody, GPS, forgot password,
progress) are **not** built until the owner confirms them. The brief says not to invent business rules.
