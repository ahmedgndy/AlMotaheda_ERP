# Architecture audit: module inventory, dependency map and the Projects module

Date 2026-10-21. Branch `feature/treasury-custody-fleet` at `1f9f4e2` plus the sibling session's uncommitted
treasury work (Finance, HR, Access, Platform, Projects `PriceItemEndpoints.cs`), which this audit reads but does not
touch. Read `00-overview.md` and `docs/architecture.md` first; the decisions that bind every module are in
`07-decisions.md`. This note is the audit's own record: what the solution is made of, how the modules depend on
each other, how to build and test it, and then one module at a time, starting with Projects. Nothing was rebuilt.
Every finding carries its evidence as `file:line`; every fix carries its test.

The method is the owner's brief: inventory first, then module by module, fixing only what is clearly a defect and
clearly understood, and writing down (never deciding alone) everything that rests on a business rule.

## 1. The solution in one page

A .NET 10 modular monolith: one host (`src/Host/AlMotaheda.Api`), eleven modules under `src/Modules/<Name>/`, each a
class library with its own `DbContext`, SQL Server schema and migration history, registered through the module class
(`ProjectsModule`, `FinanceModule`, …) listed in `src/Host/AlMotaheda.Api/ModuleCatalog.cs`. The catalog order is the
migration order: Platform, Access, Projects, HR, Workforce, Subcontractors, Fleet, Archive, Procurement, Reports,
Finance. Modules talk only through `*.Contracts` projects (interfaces and records, no EF types), and the test
`tests/AlMotaheda.Api.Tests/Architecture/ModuleBoundaryTests.cs` fails the build of a module that exposes anything
but its module class. Shared code lives in `src/BuildingBlocks` (validation, problems, permissions, `StatusFlow`,
`SqlAppLock`, `SharedTransaction`, `StorageConventions`, the audit interceptor, `CompanyClock`).

| Module | Schema | Source lines | Files | Migrations | Endpoints | Test lines | Tests |
|---|---|---:|---:|---:|---:|---:|---:|
| Platform | `platform` | 2,873 | 30 | 4 | 11 | 1,621 | 68 |
| Access | `access` | 4,079 | 48 | 3 | 17 | 2,296 | 100 |
| Projects | `projects` | 25,039 | 188 | 10 | 108 | 8,706 | 286 |
| HR | `hr` | 5,307 | 35 | 3 | 47 | 763 | 20 |
| Workforce | `workforce` | 8,246 | 68 | 2 | 32 | 2,532 | 96 |
| Subcontractors | `sub` | 6,623 | 41 | 3 | 25 | 2,513 | 78 |
| Fleet | `fleet` | 10,183 | 62 | 4 | 52 | 2,791 | 94 |
| Archive | `docs` | 2,868 | 31 | 2 | 7 | 2,164 | 91 |
| Procurement | `proc` | 4,072 | 27 | 2 | 28 | 642 | 21 |
| Reports | `rpt` | 1,097 | 11 | 1 | 6 | 362 | 13 |
| Finance | `finance` | 8,115 | 34 | 6 | 39 | 1,266 | 37 |

Source lines exclude `Data/Migrations`; endpoints are `Map{Get,Post,Put,Delete,Patch}` calls; tests count `[Fact]`,
`[Theory]` and `[DatabaseFact]` attributes (a theory runs several cases). Cross-cutting test projects:
`AlMotaheda.Api.Tests` (28, architecture and authorization) and `AlMotaheda.Integration.Tests` (13, cross-module
chains such as the treasury chain).

Three numbers deserve attention before any code does: Projects is a third of the solution and has ten migrations;
HR (763 test lines for 5,307), Procurement (642 for 4,072) and Finance (1,266 for 8,115) carry money paths with the
thinnest tests.

## 2. How to build and test (the commands that actually work here)

Toolchain found on this Mac: .NET SDK 10.0.402 (10.0.102 also installed, no `global.json`), Docker 29.1.3 with
4 GB for Docker Desktop, SQL Server image `mcr.microsoft.com/mssql/server:2022-latest` present. The machine has
8 GB of RAM, so the database test projects run one at a time (the owner's rule, and a practical one: each project
starts its own SQL Server container through Testcontainers).

```bash
dotnet build AlMotahedaERP.slnx
```

```bash
dotnet test tests/Modules/AlMotaheda.Modules.Projects.Tests --no-build --nologo --logger "console;verbosity=normal"
```

Repeat the second command per test project, never two at once (`tests/Modules/AlMotaheda.Modules.<Name>.Tests`,
`tests/AlMotaheda.Api.Tests`, `tests/AlMotaheda.Integration.Tests`). Before starting one, `docker ps` shows whether
another session's `testcontainers` SQL Server is still up; wait for it. `SKIP_DATABASE_TESTS=1` skips every
`[DatabaseFact]` (the attribute lives in
`tests/Modules/AlMotaheda.Modules.Access.Tests/Database/SqlServerFixture.cs`, shared by every module's tests through
`[Collection(SqlServerCollection.Name)]`); without Docker the same tests skip themselves. CI
(`.github/workflows/ci.yml`) builds once in Release and then runs one test project per matrix job, which is the same
one-at-a-time discipline.

Other commands, from `docs/architecture.md:379-384` and `README.md:29-81`:

```bash
dotnet tool restore
```

```bash
dotnet ef migrations add <Name> --project src/Modules/<Module>/AlMotaheda.Modules.<Module> --output-dir Data/Migrations
```

Generated migrations must be made `internal` (the boundary test fails otherwise). Scripts for a server:
`dotnet ef migrations script --idempotent --project src/Modules/<Module>/AlMotaheda.Modules.<Module> -o <module>.sql`,
in catalog order. The API runs with `dotnet run --project src/Host/AlMotaheda.Api --launch-profile https`.

## 3. Module inventory

Per module: what it owns, its tables (DbSets), what it offers through contracts and what it implements for others,
what it depends on (both directions), where it duplicates another module, its risks and a first assessment. The
"depends on" column is the `*.Contracts` projects the module's `.csproj` references; "used by" is who references its
Contracts. Only Access, Projects, HR, Finance, Procurement and Reports have a Contracts project.

### 3.1 Platform (`platform`)

- **Owns:** the audit trail (`AuditEntry`, written by `AuditTrailInterceptor` inside every module's save), the
  outbox (`OutboxMessage`), notifications and device tokens, live updates over SignalR (`ILiveUpdates`,
  `LiveScopes`), push (`FcmPushSender`), cache invalidation (`IChangeObserver`), backups (`IBackupFiles`),
  the notification rules (`Notifications/NotificationRules.cs`).
- **Tables:** `AuditEntries`, `OutboxMessages`, `Notifications`, `DeviceTokens`.
- **Depends on:** Access.Contracts, Projects.Contracts (project scope of live updates). **Used by:** nobody through
  a Contracts project; the interceptor is in BuildingBlocks.
- **Duplication:** none.
- **Risks:** `NotificationRules.cs` and `LiveScopes.cs` name every module's events, so every new feature edits
  Platform (both are in the sibling's uncommitted change set). Acceptable for a monolith; it is the one place that
  knows the whole event vocabulary.
- **Assessment:** small and healthy. Audit later only for the rule table's growth.

### 3.2 Access (`access`)

- **Owns:** users, roles (`AccessRoles`: executive, manager, engineer, office_admin, accountant, staff, hr,
  purchasing), permissions per module and action (View, Create, Edit, Approve, SeeMoney), per-user overrides,
  JWT sign-in, refresh tokens, sign-in state.
- **Tables:** `Roles`, `Permissions`, `RolePermissions`, `Users`, `UserSignInStates`, `UserPermissionOverrides`,
  `RefreshTokens`.
- **Offers:** `IUserDirectory` (names; used by every module), `IExecutiveCheck` (the database re-check behind
  `ExecutiveGate`; used by Projects, Finance, Fleet, Procurement, Platform), `IPermissionCheck` (Finance).
- **Depends on:** nothing but its own Contracts. **Used by:** every module.
- **Duplication:** none.
- **Risks:** four files have uncommitted sibling edits (`UpdateRolePermissions.cs`, `ResetUserPermissions.cs`,
  `SetUserModulePermissions.cs`, `UpdateUser.cs`); audit them after that commit lands.
- **Assessment:** stable core, well tested (100 tests for 4,079 lines).

### 3.3 Projects (`projects`)

- **Owns:** projects and membership (`ProjectMember` with `Responsibility`; the scope every other module asks
  through `IProjectAccess`), the company supplier directory, project items (BoQ-like, with sub-items), the legacy
  per-project catalog (`CatalogItem`, `ItemPrice`), supplies and site expenses (`ProjectExpense`, two channels:
  custody and credit; review; `ExpenseAdjustment`), supplier payments and allocations, supply agreements, the
  project custody ledger (`CustodyEntry`), budgets, cost alerts, the company price list and Price Control
  (`PriceCategory`, `PriceUnit`, `PriceItem`, `PriceRange`, `PriceException`, `PriceExceptionAlert`), daily site
  reports, report requests, project reports, site location monitoring (`LocationSharing`, `LocationPing`,
  `LocationAlert`), the work inbox (`/api/inbox`) and the personal overview (`/api/me/overview`).
- **Tables:** 28 DbSets across five partial context files (`ProjectsDbContext.cs`, `.Core.cs`, `.Planning.cs`,
  `.Prices.cs`, `.SiteLocation.cs`).
- **Offers (Projects.Contracts, implemented here):** `IProjectAccess`, `IVisibleProjects`, `IProjectDirectory`,
  `ISupplierDirectory`, `IProjectItems`, `IProjectCatalog`, `IPriceControl`, `ICustodyLedger`; plus Finance's
  `IPaymentSource` twice (`SupplierPaymentSource`, `CustodyFundingSource`), Finance's `ICustodyLedgerSource`
  (`ProjectCustodyLedgerSource`), Procurement's `IInvoicePosting` (`InvoicePostingContract`), Reports'
  `IReportSource` twice, and its own `IWorkInboxSource`, `IProjectRecordOwnership`.
- **Hosts contracts that other modules implement (the shared-kernel part of Projects.Contracts):**
  `IProjectCostSource` (Workforce, Subcontractors, Fleet, Procurement), `ILaborItemCosts`, `ISubcontractItemCosts`,
  `IEquipmentItemCosts`, `IWorkInboxSource` (Workforce, Subcontractors, Fleet, HR, Finance), `IPersonalOverviewSource`
  (Workforce, HR, Finance), `IProjectRecordOwnership` (Workforce, Subcontractors, Fleet; consumed by Archive),
  `IRecordDocuments` (Archive), `IPriceExceptionSource` (Procurement, Fleet).
- **Depends on:** Access, Finance, Reports and Procurement Contracts. **Used by:** every other module except Access
  and Reports.
- **Duplication:** two price catalogs (legacy per-project `CatalogItem`/`ItemPrice` and company-wide
  `PriceItem`/`PriceRange`); a supplier directory that Procurement, the natural owner of purchasing, consumes rather
  than owns; the custody ledger pattern repeated by Fleet (`VehicleCustodyEntry`) and Finance (`CustodyMovement`),
  by design (decision T-series: one ledger per module, one registry in Finance).
- **Risks:** size (a third of the solution, 108 endpoints) and the mutual Contracts dependency with Procurement
  (Projects references Procurement.Contracts to implement `IInvoicePosting` and read `ISupplierOrders`; Procurement
  references Projects.Contracts). Allowed by the rule, but it means the two modules cannot be split or deployed
  apart and suggests the supplier/invoice boundary is in the wrong place.
- **Assessment:** well tested (286 tests, 8,706 lines) and consistently written; the audit of section 4 found no
  P0 or P1, one P2 defect (fixed) and a list of P3 items. Its real issue is scope, not quality.

### 3.4 HR (`hr`)

- **Owns:** departments, employees and documents, salary structures, payroll inputs, salary sheets and lines,
  employee assignments to projects, referrals, requests (messages, attachments, alerts), HR evidence files
  (`HrFileStore`).
- **Tables:** `Departments`, `Employees`, `EmployeeDocuments`, `SalaryStructures`, `PayrollInputs`, `SalarySheets`,
  `SalarySheetLines`, `EmployeeAssignments`, `Referrals`, `Requests`, `RequestMessages`, `RequestAttachments`,
  `RequestAlerts`.
- **Offers (HR.Contracts):** `IEmployeeDirectory`, `IHrReferrals` (both used by Fleet, for drivers). **Implements:**
  Finance `IPaymentSource` (`SalarySheetPaymentSource`), `IPayrollEntitlementSource`; Reports `IReportSource`;
  Projects `IPersonalOverviewSource`, `IWorkInboxSource`.
- **Depends on:** Projects, Access, Finance, Reports Contracts. **Used by:** Fleet.
- **Duplication:** a second "people who are paid" model next to Workforce workers (salaried staff vs daily labor),
  with its own payment path (salary sheet) and its own alerts and attachments store; a second private file store
  next to Fleet's and the Archive module.
- **Risks:** the thinnest tests of any business module (20 tests, 763 lines) on a payroll money path; uncommitted
  sibling edits in `HrDbContext.cs`, `HrFeatures.cs`, `HrModule.cs`, the snapshot (the HR-before-Fleet migration
  order).
- **Assessment:** audit after the sibling's commit; first investment is tests around salary sheets and the Finance
  handshake.

### 3.5 Workforce (`workforce`)

- **Owns:** trades, workers, worker assignments, daily sheets with attendance lines and line adjustments, labor
  allocations to project items, worker payments and allocations, weekly payroll settlements (lines, allocations,
  alerts) paid from the project custody.
- **Tables:** `Trades`, `Workers`, `WorkerAssignments`, `DailySheets`, `AttendanceLines`, `LineAdjustments`,
  `LaborAllocations`, `WorkerPayments`, `WorkerPaymentAllocations`, `PayrollSettlements`, `PayrollSettlementLines`,
  `PayrollSettlementAllocations`, `SettlementAlerts`.
- **Implements:** Projects `ILaborItemCosts`, `IProjectCostSource` (`LaborCostSource`), `IWorkInboxSource`,
  `IPersonalOverviewSource`, `IProjectRecordOwnership`; Finance `IPaymentSource` (`WorkerPaymentSource`),
  `IPayrollEntitlementSource` (`LaborEntitlementSource`); Reports `IReportSource`. **Consumes:** Projects
  `ICustodyLedger` (the only caller of `HoldAsync`/`PostAsync`/`ReleaseAsync`, source `PayrollSettlement`),
  `IProjectAccess`, `IProjectItems`, `IProjectDirectory`, Finance `IPaymentRequests`.
- **Depends on:** Access, Projects, Finance, Reports Contracts. **Used by:** nobody (no Contracts project).
- **Duplication:** the settlement-plus-alerts pattern (also Fleet `VehicleExpenseAlerts`, Finance
  `CustodyTransferAlerts`, HR `RequestAlerts`, Projects `PriceExceptionAlert`); the adjustment pattern
  (`LineAdjustments`, like Projects `ExpenseAdjustments` and Fleet `EquipmentCostAdjustments`).
- **Risks:** none structural. **Assessment:** healthy, well tested (96 tests).

### 3.6 Subcontractors (`sub`)

- **Owns:** subcontractors, sub-contracts and items, progress measurements, manpower sheets, payments and
  allocations, variations.
- **Tables:** `Subcontractors`, `SubContracts`, `ContractItems`, `ProgressMeasurements`, `ManpowerSheets`,
  `SubPayments`, `SubPaymentAllocations`, `ContractVariations`.
- **Implements:** Projects `ISubcontractItemCosts`, `IProjectCostSource`, `IWorkInboxSource`,
  `IProjectRecordOwnership`; Finance `IPaymentSource`; Reports `IReportSource`.
- **Depends on:** Access, Projects, Finance, Reports Contracts. **Used by:** nobody.
- **Duplication:** a second commitment model next to Procurement purchase orders (contract value vs order value);
  both feed `IProjectCostSource` as commitment, which is the right seam.
- **Assessment:** healthy, well tested (78 tests).

### 3.7 Fleet (`fleet`): cars, machines and equipment

- **Owns:** equipment (rentals, assignments to projects, costs, cost adjustments, voided payments) and vehicles
  (driver assignments, vehicle custody ledger, vehicle expenses with flags and alerts, attachments, violations), a
  private file store (`FleetFileStore`), a location provider seam (`IVehicleLocationProvider`, null today).
- **Tables:** `Equipment`, `EquipmentRentals`, `EquipmentAssignments`, `EquipmentCosts`, `EquipmentCostAdjustments`,
  `VoidedEquipmentCostPayments`, `Vehicles`, `VehicleDriverAssignments`, `VehicleCustodyEntries`, `VehicleExpenses`,
  `VehicleExpenseFlags`, `VehicleExpenseAlerts`, `Attachments`, `VehicleViolations`.
- **Implements:** Projects `IProjectCostSource` (`EquipmentCostSource`), `IEquipmentItemCosts`, `IWorkInboxSource`,
  `IProjectRecordOwnership`, `IPriceExceptionSource` (`VehicleExpensePriceExceptionSource`); Finance `IPaymentSource`
  (`EquipmentCostPaymentSource`), `ICustodyLedgerSource` (`VehicleCustodyLedgerSource`); Reports `IReportSource`
  twice. **Consumes:** HR `IEmployeeDirectory`, `IHrReferrals` (drivers are employees), Projects `IProjectAccess`,
  `ISupplierDirectory`, `IPriceControl`, Finance `IPaymentRequests`.
- **Depends on:** Access, Projects, Finance, Reports, HR Contracts. **Used by:** nobody.
- **Duplication:** `EquipmentCostAdjustments` mirrors Projects `ExpenseAdjustments`; vehicle expense with flags,
  alerts and a price exception mirrors `ProjectExpense` with `PriceException`; a third private file store.
- **The brief's question (do cars, machines and equipment need their own module?):** they already have one, and it
  should stay one module. Equipment is a cost to projects (through `IProjectCostSource`); vehicles are a custody
  (through `ICustodyLedgerSource`) with their own expenses; both share drivers from HR and the assignment history.
  Splitting would add a Contracts project with no consumer and a second migration history for one team. The
  decision to take is smaller: whether the recurring adjustment and alert patterns move to BuildingBlocks (section 6).
- **Assessment:** the second-largest module, well tested (94 tests, 2,791 lines); audit after Projects.

### 3.8 Archive (`docs`)

- **Owns:** documents and their links to records of any module (`DocumentLink` by record kind and id, checked
  against `IProjectRecordOwnership` so a link never points off its project).
- **Tables:** `Documents`, `DocumentLinks`.
- **Implements:** Projects `IRecordDocuments` (does a record have evidence; used by Projects and Finance custody
  statements). **Consumes:** `IProjectAccess`, every `IProjectRecordOwnership`.
- **Depends on:** Access, Projects Contracts. **Used by:** nobody.
- **Duplication:** HR request attachments and Fleet attachments are stored outside Archive, so evidence can live in
  three places. Whether `HasEvidence` on a custody statement line sees HR and Fleet files is a question for those
  modules' audits (section 6, Q7).
- **Assessment:** small and the best-tested module by ratio (91 tests for 2,868 lines).

### 3.9 Procurement (`proc`)

- **Owns:** policies, requisitions and lines, quotation requests and quotations, purchase orders and lines, goods
  receipts and lines, supplier invoices and lines (with the Price Control hold).
- **Tables:** `Policies`, `Requisitions`, `RequisitionLines`, `QuotationRequests`, `Quotations`, `PurchaseOrders`,
  `PurchaseOrderLines`, `GoodsReceipts`, `GoodsReceiptLines`, `SupplierInvoices`, `SupplierInvoiceLines`.
- **Offers (Procurement.Contracts):** `ISupplierOrders` (implemented here, read by Projects), `IInvoicePosting`
  (declared here, implemented by Projects `InvoicePostingContract`). **Implements:** Projects `IProjectCostSource`
  (`CommittedCostSource`, the commitment fact), `IPriceExceptionSource` (`InvoicePriceExceptionSource`). **Consumes:**
  Projects `ISupplierDirectory`, `IProjectCatalog`, `IPriceControl`, `IProjectAccess`, `IProjectItems`.
- **Depends on:** Access, Projects Contracts. **Used by:** Projects (the mutual dependency noted in 3.3).
- **Duplication:** suppliers are read from Projects; the purchase-order commitment sits beside the Subcontractors
  contract commitment (fine) and beside Projects supply agreements (`SupplyAgreements`, a lighter promise to buy;
  worth a look at whether both are needed).
- **Risks:** 21 tests for 4,072 lines on the invoice-to-expense money path; the posting of an invoice as a Projects
  expense runs per line (`src/Modules/Projects/AlMotaheda.Modules.Projects/Integration/ProcurementContracts.cs:107-167`).
- **Assessment:** audit third, after Finance; the supplier ownership decision (section 6, D3) shapes it.

### 3.10 Reports (`rpt`)

- **Owns:** report runs (`Run`) and the Excel export (ClosedXML); the `IReportSource` registry that nine sources in
  seven modules feed.
- **Depends on:** Access Contracts and its own. **Used by:** every module with reports.
- **Assessment:** a thin orchestrator; fine as is.

### 3.11 Finance (`finance`)

- **Owns:** payment requests (lines, events), ledger entries, treasury accounts, the custody registry and its
  movements, transfers (events, alerts), payroll batches and allocations, cash counts, custody adjustments, period
  closes.
- **Tables:** `PaymentRequests`, `PaymentRequestLines`, `PaymentRequestEvents`, `LedgerEntries`, `TreasuryAccounts`,
  `CustodyAccounts`, `CustodyMovements`, `CustodyTransfers`, `CustodyTransferEvents`, `CustodyTransferAlerts`,
  `PayrollBatches`, `PayrollBatchAllocations`, `CustodyCashCounts`, `CustodyAdjustments`, `CustodyPeriodCloses`.
- **Offers (Finance.Contracts):** `IPaymentRequests` (implemented here; used by Projects, Workforce, Subcontractors,
  Fleet, HR), `IPaymentSource` (six implementations: Projects ×2, HR, Workforce, Subcontractors, Fleet),
  `IPayrollEntitlementSource` (HR, Workforce), `ICustodyLedgerSource` (Projects, Fleet), `CustodyAccountKinds`.
  **Implements:** Reports `IReportSource` twice, Projects `IWorkInboxSource`, `IPersonalOverviewSource`;
  `IBankPaymentProvider` is a mock.
- **Depends on:** Reports, Access, Projects Contracts and its own. **Used by:** Projects, Workforce, Subcontractors,
  Fleet, HR.
- **Duplication:** by design keeps one registry while three ledgers stay in their modules (`12-treasury-custody.md`
  section 1).
- **Risks:** the money spine with 37 tests for 8,115 lines, and the module most changed in the uncommitted set
  (`FinanceLog.cs`, `FinanceProblems.cs`, `Custody.cs`, `CustodyConfigurations.cs`, the snapshot,
  `CustodyControlEndpoints.cs`). Auditing a moving target produces findings that are wrong by the time they are
  read.
- **Assessment:** audit second, right after the sibling's commit lands; Integration.Tests `TreasuryChainTests.cs`
  is the place to grow.

## 4. Dependency map

Arrows read "references the Contracts of". Every module references Access.Contracts; it is left out of the figure.

```
                          Projects.Contracts
      ┌──────────┬──────────┬────┴─────┬──────────┬──────────┬──────────┐
  Workforce  Subcontr.   Fleet     Archive    Platform     HR       Finance    Procurement
      │          │          │                              │          ▲            │ ▲
      └──────────┴──────────┴──────────────────────────────┴──────────┘            │ │
                       Finance.Contracts (IPaymentSource, IPaymentRequests, …)      │ │
                                                                                    │ │
  Projects ──► Finance.Contracts, Reports.Contracts, Procurement.Contracts ─────────┘ │
  Procurement ──► Projects.Contracts ─────────────────────────────────────────────────┘   (mutual)
  Fleet ──► HR.Contracts (drivers)
  Projects, Workforce, Subcontractors, Fleet, HR, Finance ──► Reports.Contracts (IReportSource)
```

| Contract (declared in) | Implemented by | Called by |
|---|---|---|
| `IProjectAccess`, `IVisibleProjects`, `IProjectDirectory`, `IProjectItems` (Projects) | Projects | Workforce, Subcontractors, Fleet, Archive, Platform, Procurement, Finance, HR |
| `ISupplierDirectory`, `IProjectCatalog`, `IPriceControl` (Projects) | Projects | Procurement, Fleet |
| `ICustodyLedger` (Projects) | Projects | Workforce (payroll settlements) |
| `IProjectCostSource`, `ILaborItemCosts`, `ISubcontractItemCosts`, `IEquipmentItemCosts` (Projects) | Workforce, Subcontractors, Fleet, Procurement | Projects (`ProjectCostLoader`, item cost breakdown) |
| `IWorkInboxSource`, `IPersonalOverviewSource` (Projects) | Projects, Workforce, Subcontractors, Fleet, HR, Finance | Projects (`/api/inbox`, `/api/me/overview`) |
| `IProjectRecordOwnership` (Projects) | Projects, Workforce, Subcontractors, Fleet | Archive (document links) |
| `IRecordDocuments` (Projects) | Archive | Projects, Finance (evidence on statements) |
| `IPriceExceptionSource` (Projects) | Procurement, Fleet | Projects (price exception decisions) |
| `IPaymentSource`, `IPayrollEntitlementSource`, `ICustodyLedgerSource` (Finance) | Projects, HR, Workforce, Subcontractors, Fleet | Finance |
| `IPaymentRequests` (Finance) | Finance | Projects, Workforce, Subcontractors, Fleet, HR |
| `IInvoicePosting` (Procurement) | Projects | Procurement |
| `ISupplierOrders` (Procurement) | Procurement | Projects |
| `IEmployeeDirectory`, `IHrReferrals` (HR) | HR | Fleet |
| `IReportSource` (Reports) | nine sources in seven modules | Reports |
| `IUserDirectory`, `IExecutiveCheck`, `IPermissionCheck` (Access) | Access | all; Projects, Finance, Fleet, Procurement, Platform; Finance |

Two observations for the decision list (section 6): Projects.Contracts is both the Projects module's API and the
solution's shared kernel (inbox, overview, evidence, record ownership, cost sources), which is why every module
depends on it; and Projects and Procurement depend on each other's Contracts.

## 5. Projects module audit (Phase 2)

Scope: `src/Modules/Projects/AlMotaheda.Modules.Projects` (every non-migration file, 188), `Projects.Contracts`, and
the module's test harness (`tests/Modules/AlMotaheda.Modules.Projects.Tests`). Each item of the brief's checklist was
read against the code: boundaries, permissions and scope, the Executive re-check, money arithmetic, concurrency,
database integrity, performance, duplication, dead code and documentation. Classification: P0 corrupts money or
data or lets a caller act outside their rights; P1 breaks a business flow; P2 wrong result or structural debt that
will cost more later; P3 cosmetic, noise or an optimisation with no visible effect today.

### 5.1 What is right, so it stays

- Every write goes through one vertical slice with its own validation, permission and scope check; the Executive is
  re-checked in the database (`Security/ExecutiveGate.cs`), never trusted from the token alone, except in the legacy
  catalog (5.2, P2-4).
- Money is four facts kept apart (budget, commitment, recognised, paid), computed by pure classes
  (`ExpenseSettlement`, `CustodyBalance`, `ItemsLedger`, `ProjectCosts`, `BudgetComparison`) from `*Queries` that
  return `IQueryable` and are proven to translate (`Unit/Core/CoreQueryTests.cs` with `ToQueryString()`). No total
  is stored.
- Concurrency is handled where it matters: `SqlAppLock` per project and per custody, `RowVersion` on every decided
  record, one open adjustment or exception per record enforced by filtered unique indexes.
- The database carries the rules (`CK_ProjectExpense_PriceCheck`, `CK_ProjectExpense_Review`,
  `CK_ProjectExpense_Source`, `CK_CustodyEntry_Pending`), not only the code.
- Cross-module writes run in the caller's transaction (`SharedTransaction`): Finance funding a project custody,
  Workforce posting a payroll settlement, Procurement posting an invoice.
- The module boundary is intact: only Contracts are referenced, and `ModuleBoundaryTests` would fail otherwise.

### 5.2 Findings

| # | Class | Finding | Evidence | Status |
|---|---|---|---|---|
| P2-1 | P2 defect | The expense list's `paymentState` filter compared supplier allocations with the recorded amount while the state shown on each row uses the corrected amount (recorded plus approved adjustments). A record of 1,000 paid 600 and corrected by −400 showed "Paid" but was missing from `paymentState=Paid` and listed under `PartiallyPaid`; a record corrected upwards showed the mirror error. | `Features/Expenses/ExpenseQueries.cs` (Paid and PartiallyPaid branches of `Filtered`, before the fix lines 239-253) against `ExpenseSettlement.cs:53-54`, `Projects.Contracts/CodeLists.cs:90-93` and the corrected amount in `Settle` (`ExpenseQueries.cs:127-128`) | **Fixed** (5.3) |
| P2-2 | P2 structure | Two price catalogs live side by side: the legacy per-project `CatalogItem`/`ItemPrice` and the company-wide `PriceItem`/`PriceRange`. `08-cost-management.md:77` says the company list replaces the project catalog, yet `ProjectExpense` still carries `CatalogItemId` and `CatalogPriceAtRecording`, the catalog endpoints are live and the first price of a new catalog item is approved by its own creator. | `Features/Catalog/CreateCatalogItem.cs:101-113`, `Features/Catalog/UpdateCatalogItem.cs` (unit may change after prices exist), `Features/Expenses/ExpenseQueries.cs` projection (`CatalogItemId`, `CatalogPriceAtRecording`) | Decision D1 |
| P2-3 | P2 structure | `Projects.Contracts` is both the module's API and the solution's shared kernel: `IWorkInboxSource`, `IPersonalOverviewSource`, `IRecordDocuments`, `IProjectRecordOwnership`, `IProjectCostSource` and `IPriceExceptionSource` are implemented by five other modules, and Projects hosts the company-wide endpoints `/api/inbox`, `/api/me/overview` and `/api/price-*`. Every module therefore depends on Projects, and Projects grows with every feature anywhere. | Section 4 table; `Features/Inbox`, `Features/Overview`, `Features/PriceControl`, `Features/Prices` | Decision D2 |
| P2-4 | P2 structure | Projects and Procurement depend on each other's Contracts: Projects implements Procurement's `IInvoicePosting` and reads `ISupplierOrders`; Procurement reads Projects' supplier directory, catalog, price control and scope. Suppliers are owned by Projects although purchasing is Procurement's job. | `AlMotaheda.Modules.Projects.csproj` (Procurement.Contracts reference), `Integration/ProcurementContracts.cs`, `AlMotaheda.Modules.Procurement.csproj` (Projects.Contracts reference) | Decision D3 |
| P2-5 | P2 security consistency | The legacy catalog's price decision trusts the token (`caller.IsExecutive`) while every other Executive-only decision re-checks the database through `ExecutiveGate` (price ranges, adjustments, budgets, exceptions). A demoted Executive keeps the power until the token expires, only here. | `Features/Catalog/PriceDecision.cs` against `Features/Prices/PriceRangeDecision.cs` and `Security/ExecutiveGate.cs` | Decision D1 (retire) or a two-line fix once D1 is taken |
| P3-1 | P3 | `SiteLocationEndpoints` converts the pinged `recordedAt` with `ToUniversalTime()`. A value without offset arrives as `DateTimeKind.Unspecified` and would be shifted by the server's zone, while the storage convention treats Unspecified as UTC. The Flutter app sends `toUtc().toIso8601String()` (a `Z` suffix), so nothing is wrong today. | `Features/SiteLocation/SiteLocationEndpoints.cs:267`; convention `src/BuildingBlocks/AlMotaheda.BuildingBlocks.Data/StorageConventions.cs:30` | Recommend `DateTime.SpecifyKind(value, Utc)` when the kind is Unspecified; not changed (no live effect, and the sibling owns the current Projects change set) |
| P3-2 | P3 | Dead and loose code in the exception list: a predicate that is always true, `status=all` handled by resetting the validation errors, grouping over `dynamic`. | `Features/PriceControl/PriceExceptionEndpoints.cs:208` (`|| true`), `:85-91`, `Group<TKey>` | Clean-up, next pass over Price Control |
| P3-3 | P3 performance | The expense row projection runs two correlated subqueries per row on `PriceException` by `(SourceType, SourceId, Status <> Withdrawn)` and the only index on that pair is filtered on `Status = Pending`, so SQL Server scans the exceptions once per listed row. Invisible at today's volumes; a problem when exceptions accumulate. | `Features/Expenses/ExpenseQueries.cs:324-335`; indexes in `Data/Configurations/PriceControlConfigurations.cs:81-87` | Needs a migration: deferred until the sibling's snapshot change is committed (D6) |
| P3-4 | P3 performance | Per-row database calls in integration code: invoice posting queries the price list per line; the supplier report loops per project; the personal overview calls each source in turn. Small today (a posting has a handful of lines). | `Integration/ProcurementContracts.cs:107-167`, `Integration/ProjectReports.cs:139-145`, `Features/Overview` | Note for the Procurement and Reports audits |
| P3-5 | P3 noise | Since the T16 change, a non-Executive creating a price item hits `ExecutiveGate.CheckAsync` as a probe, and the gate logs an `ActionRefused` warning for every one of them although nothing was refused. | `Features/Prices/PriceItemEndpoints.cs:178-181` (uncommitted, sibling's), `Security/ExecutiveGate.cs` | Report to the sibling session; not touched |
| P3-6 | P3 consistency | `sort=amount` orders by the recorded amount, not the corrected one the row shows. | `Features/Expenses/ExpenseQueries.cs:270` | Cosmetic; decide with D5 |
| P3-7 | P3 note | `CK_CustodyEntry_Pending` allows a non-Posted entry only for `Kind = 'Payroll'`. Correct today: Workforce is the only caller of `HoldAsync`/`ReleaseAsync` (source `PayrollSettlement`). Anything else that wants a pending custody outflow (a transfer awaiting confirmation) will hit the constraint. | `Data/Configurations/SupplyAndCustodyConfigurations.cs:77`; callers found only in Workforce `Features/Settlements/SettlementEndpoints.cs` | For the Finance audit |

No P0 and no P1 were found. In particular: project scope is enforced on every project route through
`IProjectAccess.FindVisibleAsync` (404 outside scope), self-review is refused (`own_record`), a completed project is
read-only except for the Executive, voided payments never count, and the custody ledger refuses an overdraft under
policy Block inside the custody's lock.

### 5.3 Fix applied (P2-1)

`src/Modules/Projects/AlMotaheda.Modules.Projects/Features/Expenses/ExpenseQueries.cs`, `Filtered`: the `Paid` and
`PartiallyPaid` branches now compare the live allocations with
`expense.Amount + Σ approved adjustments`, the same expression `Settle` and the row projection already used, so the
list filter and the state printed on each row can no longer disagree. `Unpaid` is unchanged (no live allocation),
which agrees with `PaymentStates.Of` (`paid <= 0`). The expression is written out in each branch, as the file's own
comment requires (EF translates expressions, not helper calls).

Tests:

- `tests/Modules/AlMotaheda.Modules.Projects.Tests/Unit/Core/CoreQueryTests.cs`,
  `The_expense_list_translates_with_every_filter_including_the_payment_state`: for `Paid` and `PartiallyPaid` the
  translated SQL must reference `[projects].[ExpenseAdjustment]` more often than the same query without the payment
  filter, which proves the threshold reads the adjustments (the projection alone reads them too, hence the
  comparison rather than a plain `Contains`).
- `tests/Modules/AlMotaheda.Modules.Projects.Tests/Database/Core/ExpenseAdjustmentTests.cs`,
  `The_payment_state_filter_follows_the_corrected_amount` (`[DatabaseFact]`): two credit invoices of 1,000; the first
  paid 600 and corrected by −400, the second paid 1,000 and corrected by +200. `paymentState=Paid` must list exactly
  the first (shown as Paid), `PartiallyPaid` exactly the second (shown as PartiallyPaid, outstanding 200), `Unpaid`
  nothing. Before the fix the first was listed under PartiallyPaid and the second under Paid.

### 5.4 Business-rule questions found while reading (not defects until the owner says so)

| # | Question | Evidence |
|---|---|---|
| Q1 | An approved adjustment on a **custody** record changes the posted custody outflow (the balance uses the corrected amount) but the decision runs no `Projects:CustodyBalanceCheck`; a custody can be pushed into overdraft by a correction under policy Block. Intended, or should the Executive's decision run the same check as recording? | `Features/Expenses/AdjustExpense.cs:283-287` (custody treated as paid 0, no custody lock or check); `Features/Custody/CustodyBalance.cs` |
| Q2 | Budgets: who may approve a budget the same person drafted (decision B1 and open question 11 in `07-decisions.md`)? | `Features/Budgets` |
| Q3 | `ICustodyLedger.HoldAsync` does not check that the holder is an active member; it relies on the caller asking `CanHoldAsync` first. Workforce does. Should the ledger enforce it itself (one more query under the lock)? | `Integration/CustodyContracts.cs:28-31` |
| Q4 | A transfer written by Finance into the project custody is always recorded with `Method = Cash`, even when Finance paid it by bank transfer. Is the method on the project statement meant to be the transfer's? | `Integration/CustodyContracts.cs:315` |
| Q5 | The Purchasing role is company-wide in `ProjectScopes.IsCompanyWide` and may take part in report requests on any project. Wanted? | `Security/ProjectCaller.cs:56-67`, `Features/ReportRequests/ReportRequestParties.cs` |
| Q6 | Adjustment amounts and decision notes are shown to any caller who sees the record, while paid, outstanding and payment state need SeeMoney. A caller without SeeMoney sees only their own records, so the exposure is their own corrections. Acceptable? | `Features/Expenses/ExpenseViews.cs:133-137` |
| Q7 | Evidence lives in three stores (Archive documents, HR request attachments, Fleet attachments). Does `HasEvidence` on a custody statement line need to see HR and Fleet files? | `Integration/CustodyContracts.cs` (`IRecordDocuments`), `src/Modules/HR/.../Storage/HrFileStore.cs`, `src/Modules/Fleet/.../Storage/FleetFileStore.cs` |

## 6. Decision list (for the owner; nothing here was changed)

| # | Decision | Options | Recommendation | Why |
|---|---|---|---|---|
| D1 | The legacy per-project catalog (`CatalogItem`, `ItemPrice`, `/api/projects/{id}/catalog*`) | (a) keep both; (b) freeze writes now, keep reads for old records, copy names into the company price list (`08-cost-management.md:190`, step 5), drop in a later release; (c) drop now | **(b)** | The design already says the company list replaces it; two catalogs with two approval rules (one self-approved, one Executive) is how a wrong price gets through. Old expenses keep `CatalogItemId` for history. Flutter screens must switch first. |
| D2 | `Projects.Contracts` as shared kernel | (a) leave; (b) move the cross-cutting interfaces (`IWorkInboxSource`, `IPersonalOverviewSource`, `IRecordDocuments`, `IProjectRecordOwnership`, `IProjectCostSource`, `IPriceExceptionSource`) to a new `AlMotaheda.Modules.Shared.Contracts` and the `/api/inbox` and `/api/me/overview` hosts to Platform, keeping behaviour; (c) a new Workspace module | **(b), contracts first, endpoints later, not in this branch** | Mechanical (namespace moves, no behaviour), but it touches every module and the Flutter app's routes if endpoints move; do it on a quiet branch after the treasury merge. |
| D3 | Suppliers and invoice posting between Projects and Procurement | (a) leave the mutual dependency; (b) declare `IInvoicePosting` in Projects.Contracts (Projects implements its own contract, Procurement calls it) so the dependency runs one way; (c) move suppliers to Procurement | **(b)** now, (c) only with the next big Procurement release | (b) removes the cycle with a file move and one `using` change; (c) rewires cross-schema foreign keys on `ProjectExpense`, `SupplierPayment`, `SupplyAgreement` and every report. |
| D4 | Fleet as one module for cars, machines and equipment | (a) keep one module; (b) split vehicles from equipment | **(a)** | Section 3.7. |
| D5 | Custody corrections and the overdraft policy (Q1); sort by corrected amount (P3-6) | (a) corrections bypass the policy (today); (b) the Executive's decision runs `CustodyBalanceCheck` like a new record | owner's call; the code change is small either way | Money rule, not a code rule. |
| D6 | Index `IX_PriceException_Source` on `(SourceType, SourceId)` including `Status` (P3-3) | add in the next Projects migration | add | One migration; wait until the sibling's `PriceItemEndpoints` change and snapshot are committed so two sessions do not generate migrations on the same snapshot. |
| D7 | Shared adjustment and alert patterns (Projects, Workforce, Fleet, Finance, HR each have their own) | (a) leave; (b) a BuildingBlocks `Adjustment<T>` and `Alert` pair | **(a)** for now | Five similar shapes but different owners and rules; a shared base would be an abstraction without a second real use of the same rule. Revisit if a sixth appears. |
