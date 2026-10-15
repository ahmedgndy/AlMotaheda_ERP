# SignalR event map and cache invalidation map

Date: 2026-10-16. Design for Phases 5 and 6. Nothing here is built yet.

## 1. Pipeline (one path for audit, notifications, live data and cache)

```
slice: change entity ──► add event to module's outbox ──► SaveChanges + COMMIT (one transaction)
                                                               │
                         Platform OutboxDispatcher (hosted service, polls every ~1 s, at-least-once)
                                                               │
            ┌──────────────────────┬───────────────────────────┼──────────────────────────┐
            ▼                      ▼                           ▼                          ▼
   write AuditEntry (M4)   write Notification rows     DependencyMap: event → affected   HybridCache.RemoveByTag
   (if not written           (recipients resolved       aggregates (section 3)            for the same aggregates
    in-transaction)           server-side)                        │
                                   │                              ▼
                                   └──────────► SignalR: "notification" to user:{id};  "changed" to scope groups
```

- Nothing is published before commit, and nothing is lost if the process stops: the outbox row stays until
  dispatched. Each consumer is idempotent by outbox message id.
- **The cache and SignalR use the same event and the same dependency map**, so they can't disagree.

## 2. Hub contract (for React and Flutter)

| Item | Value |
|---|---|
| URL | `/hubs/erp` (same site as `/api`; access token in the `access_token` query string for WebSockets only, as SignalR requires; never logged) |
| Auth | Same JWT. Connection refused without a valid token or while a password change is required |
| Server → client | `notification(NotificationSignal)` · `changed(ChangeSignal)` · `resync()` |
| Client → server | `Join(scope)` · `Leave(scope)` (returns `true`/`false`; `false` = not authorized, nothing joined) |
| On connect | Server adds the connection to `user:{id}` automatically |
| On reconnect | Client re-joins its scopes, then **refetches** what it shows (signals missed while offline are not replayed). Unread notifications come from `GET /api/notifications` |

```jsonc
// ChangeSignal: no amounts, no names: only "something changed, refetch it"
{ "entityType": "ProjectCost", "entityId": 42, "version": 18233, "changedFields": ["labor"], "scope": "project:42:money" }
// NotificationSignal: the stored notification's id and type; text comes from GET /api/notifications
{ "id": 9001, "type": "PaymentRequestAwaitingDecision", "entityType": "PaymentRequest", "entityId": 77 }
```

`version` is the outbox message id (always increasing). A client ignores a signal older than the data it
already has.

### Groups and who may join (checked on the server on every `Join`)

| Scope | Receives | Join allowed when |
|---|---|---|
| `user:{id}` | Own notifications, own records returned/approved | Automatic, own id only |
| `project:{id}` | Non-money changes of a project (status, records, reports, documents) | `IProjectAccess.FindVisibleAsync` finds the project |
| `project:{id}:money` | Money aggregates of that project (cost, paid, budget, items) | Project visible **and** `projects:F` |
| `entity:{type}:{id}` | One open record (expense, sheet, PR, PO, payment request…) | The same check as `GET` of that record |
| `company:finance` | Company-wide money (executive and accountant dashboards, ledger, payment requests) | `finance:V` or `payments:A` |
| `company:hr` | Employees and salary sheets | `hr:V` (amount fields changed are named only with `hr:F`) |
| `company:procurement` | Company-wide procurement lists | `procurement:E` or Executive |

An Engineer can never join `company:*` or `project:{id}:money` without the permissions above [Company].
Joins are re-checked when the token is refreshed; on permission loss the server removes the connection from the
group (and sends `resync`).

## 3. Event map and dependency map (the one place derived numbers are listed)

Columns: event → aggregates to refresh (each aggregate = one `ChangeSignal` to its scope + one cache tag).

| Event (raised after commit) | Source | Aggregates affected → scope |
|---|---|---|
| `ExpenseChanged` (recorded, edited, approved, returned, rejected, linked) | Projects | Expense list `project:{p}`; if money changed: ProjectItems, ProjectOverview, CostSummary, BudgetComparison, SupplierBalances → `project:{p}:money`; CompanyDashboard → `company:finance` |
| `LaborSheetChanged` (submitted, approved, returned, allocation) | Workforce | Sheets `project:{p}`; if approved/allocated: LaborSummary, ProjectItems, ProjectOverview, CostSummary, BudgetComparison → `project:{p}:money`; WorkerAccount(worker) → `entity:Worker:{w}`; CompanyDashboard |
| `PayrollBatchChanged` | Workforce | PayrollBatch `entity`; WorkerAccount ×N; `company:finance` |
| `SubcontractWorkChanged` (measurement / manpower approved…) | Subcontractors | Contract `entity`; ProjectItems, ProjectOverview, CostSummary, Obligations → `project:{p}:money`; CompanyDashboard |
| `EquipmentCostChanged` | Fleet | EquipmentCosts `project:{p}`; EquipmentSummary, ProjectOverview, CostSummary → `project:{p}:money`; CompanyDashboard |
| `BudgetChanged` (approved) | Projects | Budgets, BudgetComparison, ProjectOverview → `project:{p}:money`; CompanyDashboard |
| `PriceChangeDecided` (approved) | Projects | Catalog `project:{p}`; PriceCatalog cache; PR estimates that read it → `company:procurement` |
| `PaymentRequestChanged` (submitted, decided, sent, paid, failed) | Finance | PaymentRequest `entity`; PaymentRequests list `company:finance`; on Paid: the source's balances (SupplierBalances / WorkerAccount / Contract / EquipmentCost / SalarySheet) and ProjectOverview, CostSummary → `project:{p}:money` |
| `LedgerEntryCreated` | Finance | Ledger, CashMovement, Reconciliation, CompanyDashboard → `company:finance` |
| `SalarySheetChanged` | Hr | SalarySheet `entity`; SalarySheets `company:hr`; on Paid: `company:finance` |
| `EmployeeChanged` | Hr | Employees `company:hr` |
| `RequisitionChanged`, `QuotationSetChanged`, `PurchaseOrderChanged`, `GoodsReceiptChanged` | Procurement | Document `entity`; lists `company:procurement` and `project:{p}`; PO/GRN also CommittedCost → `project:{p}:money` |
| `SupplierInvoiceMatched` | Procurement | Invoice `entity`; recognized supplier cost: ProjectItems, ProjectOverview, CostSummary, CommittedCost, SupplierBalances → `project:{p}:money`; CompanyDashboard |
| `ProjectChanged` / `ProjectMemberChanged` | Projects | Project `project:{p}`; on membership change the affected user gets `resync` (their scopes change) |
| `ReportExportReady` | Reporting | Notification to the requester (`user:{id}`) |
| `BackupFailed` / `BackupCompleted` | Platform | Notification to Executives; BackupHistory `entity` |
| `ReferenceDataChanged` (trade, unit, category, role permissions) | any | Lookup cache; `resync` to affected roles for permissions |

Derived chain example (from the brief): approved labor line → `LaborSheetChanged` → LaborSummary →
ProjectItems (labor column) → ProjectOverview (project total) → CompanyDashboard (executive + accountant). All
four signals and cache removals come from that one row.

**Concurrency:** live signals tell an editor "this changed". The save itself is still protected by
`rowVersion` → 409 `concurrency_conflict` [Company].

**Scale-out:** V1 is one server, in-memory groups. With two or more servers: add the Redis backplane or Azure
SignalR (host-only change, architecture rule 7), and run one outbox dispatcher (SQL app lock as leader lock).

## 4. Cache (Phase 6)

**Choice: `HybridCache`** (already decided in `architecture.md`). Reasons: one API with stampede protection
(many users opening the same dashboard run the query once), tag-based removal (matches the dependency map),
and a Redis second level can be added later without code changes. `IMemoryCache` has no tags or stampede
protection; `IDistributedCache` alone needs serialization everywhere and gives nothing on one server.

Key convention, defined once in `CacheKeys` (BuildingBlocks): `{module}:{entity}:{id|scope}:v{n}`, plus tags
named like the aggregates in section 3 (`agg:ProjectOverview:{p}`). Only the owning module reads and writes its
keys [Company: architecture.md].

| Cached item | Key | TTL (local / shared) | Invalidated by | Who can read |
|---|---|---|---|---|
| Trades | `workforce:trades:all:v1` | 10 min / 30 min | `ReferenceDataChanged(trade)` | `workers:V` (checked per request) |
| Units, work types, cost groups (code lists) | in code, no cache | — | — | — |
| Expense / catalog categories | enums in code, no cache | — | — | — |
| Roles and role permissions | `access:roles:all:v1` | 1 min / 5 min | `UpdateRolePermissions`, `ReferenceDataChanged(role)` | `accounts:V` |
| Effective permissions (DB re-check, M8) | **not cached** | — | — | — |
| Project item price catalog (approved prices) | `projects:catalog:{p}:v1` | 5 min / 15 min | `PriceChangeDecided`, catalog item create/edit | `catalog:V` + project scope |
| Project overview / cost summary | `projects:overview:{p}:v1` | 30 s / 60 s | every event that lists `ProjectOverview` / `CostSummary` | `projects:F` + project scope (checked before reading the cache) |
| Company dashboards (executive, accountant) | `reports:dashboard:company:{from}:{to}:v1` | 30 s / 60 s | any event listing `CompanyDashboard` | `finance:V` |
| Report catalog metadata | `reports:catalog:all:v1` | 1 h / 1 h | deploy (it is code) | everyone; list filtered by role after reading |

**Never from cache** [Company]: balances or outstanding amounts used to approve or pay, approval checks,
idempotency checks, payment request status during processing, permission re-checks, the project-scope check.
Write slices read those straight from the database inside their transaction, as today.

**No leak between roles:** cached values hold data, not decisions. The permission and project-scope checks run
on every request before the cache is read. Keys for project data contain the project id. Nothing is cached per
role with money in it for a role without F: the money filter is applied after reading, by the same code as
today.

**Failure:** without the cache the app works, only slower (`HybridCache` falls back to the factory).
