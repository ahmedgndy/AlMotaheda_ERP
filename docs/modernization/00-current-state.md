# Phase 0 — Current State Report

Date: 2026-10-16. Read-only review of the whole backend at commit `6a84c69`. No code was changed.

## 1. The stack, as the code actually is

The brief expected "Clean Architecture, CQRS/MediatR, EF Core, SQL Server". The code differs in two places,
on purpose and documented in `docs/architecture.md`:

| Expected | Actual | Comment |
|---|---|---|
| ASP.NET Core | ASP.NET Core **Minimal APIs** on .NET 10 | No controllers. |
| Clean Architecture (Domain / Application / Infrastructure projects) | **Modular monolith with vertical slices.** One project per module; inside it, one file per use case (endpoint + request + validation + logic + data access). | Deliberate (architecture.md §3, §6). Boundaries are enforced by architecture tests. |
| CQRS with MediatR | **No MediatR.** Endpoint calls a static `HandleAsync`. Reads and writes are separate slices, so CQRS exists in practice, without a library. | Deliberate ("No mediator framework", §6). |
| FluentValidation | **Hand-written validation per slice** (`ValidationErrors`, `Input.*`), returning every failing field at once. | Deliberate. Consistent across modules. |
| EF Core + SQL Server | **EF Core 10 + SQL Server.** One `DbContext`, one schema and one migration history per module. | As expected. |
| Global ProblemDetails | Yes. `GlobalExceptionHandler` + `ApiProblem` with a machine `code`. | Good. |

**Recommendation:** follow the code. Keep vertical slices and no MediatR. Moving to Clean Architecture layers +
MediatR would rewrite ~150 endpoints and gain nothing the project needs. Business rules that today sit in
static `*Rules` classes can move into entity methods without changing the slice structure (see problem 7).

Other facts:

- Auth: JWT access token (15 min, HS256) + rotating refresh token in an HttpOnly cookie. Permissions travel in
  the token as `perm` claims like `projects:VCEF` (View, Create, Edit, Approve, See money) over **17 permission
  modules** and **6 fixed roles**: executive, manager, engineer, officeAdmin, accountant, staff.
- Project-level access (IDOR guard): `IProjectAccess.FindVisibleAsync`, membership read from the DB on every
  request; a project you can't see answers 404.
- Concurrency: `ROWVERSION` on every editable table; financial writes run in a transaction with a SQL
  application lock (`sp_getapplock`) per project or per worker.
- Money: `decimal(18,2)` everywhere, quantities `decimal(18,3)`. No `float`/`double` found.
- Enums stored by name with `CHECK` constraints.
- Background work: only three startup `IHostedService`s (migrations + bootstrap). No scheduled jobs.
- File storage: Archive module, local disk folder from `Archive:Storage:RootPath` (outside wwwroot), type
  sniffing, size limits per kind, `nosniff`, sandboxed preview.
- Logging: built-in `ILogger`, JSON in Production, `QueueFullMode: DropWrite`.
- Config: Options pattern + `ValidateOnStart`; secrets only in user-secrets / env vars. Committed appsettings
  contain no secrets.
- Size: ~58,000 lines of C# in `src` (including migrations), 156 endpoints (57 GET, 70 POST, 26 PUT, 3 DELETE),
  104 test files, ~1,700 tests (QA report of 2026-10-15: 0 failing).

## 2. Modules that exist

| Module | Schema | What it owns |
|---|---|---|
| Access | `access` | Users, roles, permissions, per-user overrides, sign-in, refresh tokens |
| Projects | `projects` | Projects, team membership, suppliers (company list), project items ("بنود"), expenses & supplies, supplier payments, budgets, catalog & price changes (with approval), daily site reports, report requests, overview, cost reports, work inbox |
| Workforce | `workforce` | Trades, workers (daily labor), project assignment, daily labor sheets (Draft → Submitted → Approved/Returned), allocation to items, worker payments (FIFO, per worker) |
| Subcontractors | `sub` | Companies, contracts, contract items, measured work, manpower sheets, subcontractor payments |
| Fleet | `fleet` | Equipment register, rentals, project assignment, equipment costs (fuel, rental, maintenance) and their payment |
| Archive | `docs` | Project documents, upload/download/preview, links to records in other modules |

Cross-module talk goes only through `Access.Contracts` and `Projects.Contracts` (interfaces + records).

## 3. Modules that are missing (compared with the brief)

| Brief asks for | Today |
|---|---|
| **Finance**: payment requests, Executive approve / approve & pay, bank confirmation, ledger, reconciliation | **Not built** (decision G3). Payments are recorded as already-happened facts in four places: `SupplierPayment`, `WorkerPayment`, `SubPayment`, and payment columns on `EquipmentCost`. |
| **HR**: employees, salaries, salary sheet | **Not built.** There is no employee or salary data anywhere. Office staff and engineers exist only as `access.User` sign-in accounts (name, job title, role). So there is nothing to "move out of the Accountant"; HR is new. |
| **Weekly payroll batch** for workers | **Not built.** Workers are paid one payment per worker, FIFO over approved days (decision W2, open question 4). There is no "Create Team" / weekly batch entity. |
| **Procurement** (PR, RFQ, PO, GRN, invoice, 3-way match) | **Not built.** Supplier deliveries are recorded directly as `ProjectExpense` rows (kind Supply). |
| **Reports module** (catalog, Excel/PDF export, background export) | Only two per-project reports (`cost-summary`, `obligations`) and report requests. No export library. |
| **Notifications** (persisted) and **SignalR** | Not built. `GET /api/inbox` computes "waiting for me" on every call; no stored notifications, no real-time. |
| **Caching** | Not built (rules are pre-written in architecture.md: `HybridCache`). |
| **Audit log table** | Not built. Audit events go only to structured logs. |
| **Database backup service** | Not built. |
| Office expenses, fuel (vehicles), custody funds, income / client certificates, GPS | Not built. Permission modules exist for them (`office`, `fuel`, `custody`, `income`, `monitoring`) but no endpoints. |

## 4. Top 10 problems, ranked by risk (financial first)

| # | Problem | Why it is risky | Where |
|---|---|---|---|
| 1 | **No maker-checker on payments.** One user with `payments` Create (Accountant by default) records a payment and it counts as paid immediately. No Executive approval, no bank confirmation. | One person can move money on the books alone. This is the main control the brief asks for. | `RecordSupplierPayment`, `RecordWorkerPayment`, `RecordSubPayment`, `EquipmentCostPayments` |
| 2 | **No single ledger.** Paid money lives in four different tables with three different designs (allocation tables vs. columns on the cost row). | Reports can't reconcile to one book; "total paid this month" needs four queries that can drift. | Projects, Workforce, Subcontractors, Fleet |
| 3 | **Custody records count as paid the moment they are approved**, with no custody fund balance. | Cash spent on site has no balance control; overspending a fund is invisible. | `PaymentChannel.Custody`, `ExpenseSettlement` |
| 4 | **No correction path for approved records.** No reversal or adjustment record exists. | A wrong approved amount stays wrong, or someone edits the database by hand (no trace). | All `Review*` slices; open question 3 |
| 5 | **Audit trail only in logs, and logs can drop messages** (`DropWrite` in Production). No old/new values. | Under load, the record of who approved or paid can be lost. Not acceptable for money. | `*Log.cs` in every module, `appsettings.Production.json` |
| 6 | **Payment permissions come from a token up to 15 minutes old.** Only user management re-checks the database. | A demoted or disabled Accountant can still record payments for up to 15 minutes. | `RequirePermission` on payment endpoints |
| 7 | **Status has a public setter on every entity.** Allowed transitions are checked in separate static `*Rules` classes. | Correct today, but any new handler can set `Status` directly and skip the rules. | 24 places assign `.Status =` (e.g. `ReviewExpense.cs:135`) |
| 8 | **Gaps in duplicate prevention.** Worker national id is optional, so workers without it can be duplicated. Suppliers and subcontractors are unique on English name only (not Arabic name, not tax number). Equipment-cost payment has no idempotency key (protected by status + rowversion only). | Duplicate parties split balances; a duplicate worker can be paid twice for the same person. | `WorkerConfigurations.cs:66`, `WorkspaceConfigurations.cs:27`, `SubcontractorConfigurations.cs:29` |
| 9 | **Approvals without separation for budgets.** An Executive can approve a budget they drafted (every other approval forbids "own record"). | Weak control on the number every variance report uses. | `ApproveBudget.cs`; open question 11 |
| 10 | **Copy-pasted infrastructure and a vulnerable dependency.** `AppLock` ×4 (+ `ProjectLock`), `SqlList` ×4, `DataHelpers` ×6, `Validation` ×6, near-identical. `System.Security.Cryptography.Pkcs` 9.0.4 is flagged (NU1902/NU1903). | A fix in one copy is missed in another; known CVE in the dependency tree. | `*/Data/AppLock.cs`, `*/Common/*.cs` |

What is already good and should be kept: project-scope IDOR checks, money hidden without `SeeMoney`, rowversion
on every editable row, app locks on sum-checking writes, idempotency keys on three payment types, approved money
fields frozen, voids instead of deletes, CHECK constraints with the NULL trap handled, migration-matches-model
tests, endpoint-needs-a-token tests.

## 5. Things the brief assumes that don't match the code (please confirm)

1. **"Move employee management out of the Accountant."** There is no employee management today. HR will be new;
   there is no employee data to migrate. Users (sign-in accounts) stay in Access, managed by the Executive.
2. **"Daily Team → weekly payroll batch."** Today: daily labor sheet per engineer/day → reviewed → worker paid
   individually. A weekly batch is a new concept and changes open question 4 of `07-decisions.md`.
3. **"Supplier entity used in the Project Cost Breakdown."** It is `projects.Supplier`, owned by the Projects
   module. Procurement must use it through a contract (or Supplier moves to its own module). No second table.
4. **"Existing payment provider abstraction" (NBE).** There is none. A bank abstraction with a mock will be new.
5. **"Existing Report Requests / Daily Project Report flow."** Exists (Projects module) and stays as is.
6. **Frontend:** the React prototype (`alsaleh-prototype66.html`, cited in `docs/project-workspace/00-overview.md`)
   is not in this repository or on this machine. The owner chose to use the Flutter app instead
   (`~/StudioProjects/al_motaheda_erp_flutter`); see `01-coverage-matrix.md`.
