# Project Workspace: design overview

This folder is the design of the Project Workspace backend. Read this file first, then the file of the module
you work on.

| File | Covers |
|---|---|
| `00-overview.md` | Scope, screen-to-API map, module ownership, shared rules, contracts, testing |
| `01-projects-core.md` | Suppliers, project items ("بنود"), expenses and supplies, supplier payments |
| `02-projects-planning.md` | Budget, items and prices (catalog), daily site reports, report requests, overview and reports |
| `03-workforce.md` | Trades, workers, project assignment, daily labor sheets, allocation to items, worker payments |
| `04-subcontractors.md` | Subcontractor companies, contracts, contract items, measured work, manpower, payments |
| `05-fleet.md` | Equipment register, rentals, project assignment, equipment costs |
| `06-archive.md` | Project documents: upload, storage, search, preview, download |
| `07-decisions.md` | Decisions taken, open business questions, what is not built |

Sources used: the React prototype (`alsaleh-prototype66.html`, the behaviour reference), the ERD
(`Al-Saleh Database ERD.md`, the data baseline), and the existing code. Where they disagree, `07-decisions.md`
says which one was followed and why.

## 1. Scope

One workspace per project, with these sections. Every section works on one project and never mixes projects.

| # | Section (prototype tab) | Owning module | Permission module |
|---|---|---|---|
| 1 | Overview (`overview`) | Projects | `projects` |
| 2 | Expenses and supplies (`expenses`) | Projects | `projects` |
| 3 | Cost estimate and budget (new; prototype has one budget number) | Projects | `projects` |
| 4a | Project items "بنود" (`items`) | Projects, with figures from Workforce and Subcontractors | `projects` |
| 4b | Items and prices (`catalog`) | Projects | `catalog` |
| 5 | Subcontractors (`subcontractors`) | Subcontractors | `subcontractors` |
| 6 | Workers and labor (`workers`) | Workforce | `workers` |
| 7 | Engineers and project team (`attendance`, team part) | Projects (existing membership API) | `projects` |
| 8 | Daily site reports (`daily`) | Projects | `site` |
| 9 | Site equipment (`equipment`) | Fleet | `equipment` |
| 10 | Project archive (`documents`) | Archive | `projects` |
| 11 | Project reports (`costs`, `requests`) | Projects | `projects`, `reports` |

## 2. Module ownership and dependencies

```
                         AlMotaheda.Api (host)
   ┌──────────┬───────────┬──────────────┬───────────┬──────────┐
 Access    Projects    Workforce   Subcontractors   Fleet     Archive
   │          │            │              │           │          │
   │          └── implements Projects.Contracts ◄─────┴──────────┘  (all four reference it)
   └── implements Access.Contracts ◄── every module
```

- New project `AlMotaheda.Modules.Projects.Contracts`: public interfaces and records only. Projects implements
  the "ask Projects" interfaces. Workforce and Subcontractors implement the "give Projects your cost figures"
  interfaces. So no module references another module's implementation, and Projects never reads their tables.
- Schemas: `projects`, `workforce`, `sub`, `fleet`, `docs`. One `DbContext` and one migration history per module.
- Foreign keys across schemas only point to `projects` and `access` (the ERD's shared modules). They are written
  by hand in the migration, like `ProjectMember.UserId`. Migration order: Access, Projects, then the others.
- Module order in `ModuleCatalog`: Access, Projects, Workforce, Subcontractors, Fleet, Archive.

## 3. Shared business rules

These rules hold in every module.

### 3.1 Four different money facts, never mixed

| Fact | Meaning | Where it lives |
|---|---|---|
| Estimate / budget | What we plan to spend | `Budget`, `BudgetLine`, catalog estimated quantity |
| Commitment | Signed but not executed (subcontract value minus executed work) | Calculated |
| Recognized cost | An **approved** source record: supply, expense, labor line, measured work, manpower sheet, equipment cost | The source record itself |
| Payment | Money that left: a payment row with allocations to the records it settles | `*Payment`, `*PaymentAllocation` |

- A record that is recorded, submitted, returned or rejected is **not** a cost. Only approved records count.
- Approved is not paid. Paid is only what payment allocations say (or the custody rule below).
- Outstanding is always calculated: recognized amount minus allocated payments. It is never stored.
- Totals are never typed and never stored as separate columns. They are sums of source records.
- One source record is one cost. Linking a record to a project item sets a link on that record. It never
  creates a second record.

### 3.2 Approval

- The person who recorded a record can't approve, return or reject it (409 `own_record`).
- Return and reject need a note. Returned records go back to the recorder to fix and resubmit. Rejected is final.
- After approval the money fields of a record are frozen. Only its link to a project item can change.
- There is no "undo approval" yet (see `07-decisions.md`).

### 3.3 Payment channel of a cost record

| Channel | Meaning | Paid when |
|---|---|---|
| `Custody` | Paid on site, in cash, from a custody (petty cash) fund | The record is approved: paid = amount |
| `Credit` | Supplier invoice, to be paid later by the company | Payment allocations are recorded |

Custody fund balances are not tracked in this version.

### 3.4 Payments

- Recording any payment needs `payments` Create. Voiding a payment needs `payments` Approve.
- Every payment request carries a client-made `idempotencyKey` (a GUID). Sending the same key twice returns the
  first payment and creates nothing (unique index).
- Payments are never edited or deleted. A wrong payment is voided (who, when, why). Voided payments and their
  allocations stay in the tables and are ignored by every calculation.
- An allocation can never exceed what is left on the record it settles. This is checked inside a transaction
  that holds a lock (see 3.8).

### 3.5 Who may see and do what

- Every project endpoint first asks `IProjectAccess.FindVisibleAsync`. A project the caller may not see answers
  404 `not_found`, the same as a project that does not exist.
- Then the permission of the owning permission module is checked (`RequirePermission`).
- A responsibility on a project never adds a permission.
- A **completed** project is read-only: writes answer 409 `project_completed`, except for an Executive.
- Money: endpoints that are only about money need `SeeMoney` on their permission module. Mixed endpoints return
  money fields as `null` when the caller has no `SeeMoney`. The server never relies on the client to hide money.

### 3.6 Company lists and project records

Suppliers, workers, trades, subcontractor companies and equipment are company-wide lists: one record each, used
by many projects. What is project-specific is the assignment, contract or cost record. Ending an assignment
never deletes history.

### 3.7 Data types (same as the ERD)

| Kind | SQL | C# |
|---|---|---|
| Key | `INT IDENTITY` | `int` |
| Money | `DECIMAL(18,2)` | `decimal` |
| Quantity, days, litres | `DECIMAL(18,3)` | `decimal` |
| Percent | `DECIMAL(5,2)` | `decimal` |
| Enum | `VARCHAR(30)` + CHECK, stored by name | `enum` |
| Business date | `DATE` | `DateOnly` |
| Moment | `DATETIME2(3)` UTC | `DateTime` |
| Free text | `NVARCHAR(n)` | `string` |
| Concurrency | `ROWVERSION` | `byte[]` |

Money is rounded to 2 decimals with `Math.Round(value, 2, MidpointRounding.AwayFromZero)`.
Currency is EGP everywhere; no currency column.

### 3.8 Concurrency

- Edits send the `rowVersion` of the last read; a stale one answers 409 `concurrency_conflict`.
- Writes that check a sum before saving (approve, allocate, pay) run in a transaction that first takes an
  application lock (`sp_getapplock`, the pattern of `ProjectLock`): one lock per project for project records,
  one per worker for worker payments. Lock timeout answers 503 `busy`.
- Unique indexes are the last guard. A lost race becomes a 409, never a 500.

## 4. `AlMotaheda.Modules.Projects.Contracts`

```csharp
// Asked by every module. Implemented by Projects.
public interface IProjectAccess
{
    // null = the project doesn't exist or the caller may not see it (answer 404).
    Task<ProjectAccessInfo?> FindVisibleAsync(ClaimsPrincipal caller, int projectId, CancellationToken ct);
}
public sealed record ProjectAccessInfo(
    int ProjectId, string Code, string NameAr, string NameEn,
    bool IsCompleted,              // status Completed: read-only except for an Executive
    string? CallerResponsibility,  // ProjectResponsibilities.* or null when the caller isn't on the team
    bool CallerIsExecutive)
{
    public bool CallerIsManager => CallerResponsibility == ProjectResponsibilities.ProjectManager;
    public bool WritesBlocked => IsCompleted && !CallerIsExecutive;
}

public interface IProjectItems     // Implemented by Projects.
{
    // Items of this project among the ids; ids of other projects are left out.
    Task<IReadOnlyDictionary<int, ProjectItemRef>> FindAsync(int projectId, IReadOnlyCollection<int> itemIds, CancellationToken ct);
}
public sealed record ProjectItemRef(int Id, int ProjectId, string NameAr, string NameEn, bool IsActive);

public interface ISupplierDirectory   // Implemented by Projects.
{
    Task<IReadOnlyDictionary<int, SupplierRef>> FindAsync(IReadOnlyCollection<int> supplierIds, CancellationToken ct);
}
public sealed record SupplierRef(int Id, string NameAr, string NameEn, bool IsActive);

// Implemented by Workforce. Used by the Projects items screens.
public interface ILaborItemCosts
{
    Task<IReadOnlyList<ItemCostTotal>> TotalsByItemAsync(int projectId, CancellationToken ct);
    Task<IReadOnlyList<LaborItemLine>> LinesAsync(int projectId, int? itemId, CancellationToken ct);  // null = not linked
    Task<bool> HasLinksAsync(int projectId, int itemId, CancellationToken ct);
}

// Implemented by Subcontractors. Used by the Projects items screens.
public interface ISubcontractItemCosts
{
    Task<IReadOnlyList<ItemCostTotal>> TotalsByItemAsync(int projectId, CancellationToken ct);
    Task<IReadOnlyList<SubcontractItemLine>> LinesAsync(int projectId, int? itemId, CancellationToken ct);
    Task<bool> HasLinksAsync(int projectId, int itemId, CancellationToken ct);
}

// Recognized cost of one item from one source. ItemId null = approved records not linked to an item yet.
public sealed record ItemCostTotal(int? ItemId, decimal Cost, decimal Paid, int RecordCount, int PartyCount);

// Implemented by Workforce, Subcontractors and Fleet (one each). Used by the overview and the reports.
public interface IProjectCostSource
{
    string Group { get; }   // CostGroups.Labor, CostGroups.Subcontractors or CostGroups.Equipment
    Task<ProjectCostFigures> SummarizeAsync(int projectId, DateOnly? from, DateOnly? to, CancellationToken ct);
}
public sealed record ProjectCostFigures(
    decimal Recognized, decimal Paid, int RecognizedRecords,
    decimal PendingReview, int PendingRecords,
    decimal Committed);   // signed but not executed; 0 when the source has no such idea
```

The contracts project also holds the shared code lists `MeasurementUnits`, `WorkTypes`, `CostGroups`,
`ProjectResponsibilities` and `PaymentStates` (string constants with an `All` list and `IsKnown`), and the two
line records `LaborItemLine` and `SubcontractItemLine`. Read the source for their fields.

`ItemCostTotal`, `LaborItemLine` and `SubcontractItemLine` only ever contain **approved** records.

## 5. Screen-to-API map

Routes are under `/api`. `{pid}` is the project id. "New" means added by this work.

| Section | Reads | Writes |
|---|---|---|
| Project list, project page header | `GET /projects`, `GET /projects/{pid}` (existing) | `POST /projects`, `PUT /projects/{pid}` (existing) |
| Overview | New `GET /projects/{pid}/overview` | none |
| Expenses and supplies | New `GET /projects/{pid}/expenses`, `GET …/expenses/{id}`, `GET /suppliers` | New `POST …/expenses`, `PUT …/expenses/{id}`, `POST …/expenses/{id}/approve\|return\|reject`, `PUT …/expenses/{id}/item`, `POST /suppliers`, `PUT /suppliers/{id}`, `POST …/supplier-payments`, `POST …/supplier-payments/{id}/void` |
| Budget | New `GET …/budgets`, `GET …/budgets/{id}`, `GET …/budget-comparison` | New `POST …/budgets`, `PUT …/budgets/{id}`, `POST …/budgets/{id}/approve` |
| Project items | New `GET …/items`, `GET …/items/{id}`, `GET …/items/unallocated` | New `POST …/items`, `PUT …/items/{id}`, `POST …/items/{id}/deactivate\|reactivate`, `DELETE …/items/{id}` |
| Items and prices | New `GET …/catalog-items`, `GET …/catalog-items/{id}` | New `POST …/catalog-items`, `PUT …/catalog-items/{id}`, `POST …/catalog-items/{id}/price-changes`, `POST …/price-changes/{id}/approve\|reject` |
| Subcontractors | New `GET /subcontractors`, `GET …/subcontracts`, `GET …/subcontracts/{cid}` | New company, contract, contract item, measurement, manpower, payment and item-link endpoints (`04-subcontractors.md`) |
| Workers and labor | New `GET /workers`, `GET /trades`, `GET …/workers`, `GET …/labor/sheets`, `GET …/labor/summary` | New worker, assignment, sheet, allocation and worker payment endpoints (`03-workforce.md`) |
| Engineers and team | `GET …/members`, `GET /projects/assignable-users` (existing) | `POST …/members`, `PUT …/manager`, `POST …/members/{id}/end` (existing, Executive only) |
| Daily site reports | New `GET …/daily-reports`, `GET …/daily-reports/{id}` | New `POST …/daily-reports`, `PUT …/daily-reports/{id}`, `POST …/daily-reports/{id}/review\|return` |
| Site equipment | New `GET /equipment`, `GET …/equipment`, `GET …/equipment-costs`, `GET …/equipment-summary` | New equipment, assignment and equipment cost endpoints (`05-fleet.md`) |
| Archive | New `GET …/documents`, `GET …/documents/{id}`, `GET …/documents/{id}/content` | New `POST …/documents` (multipart), `PUT …/documents/{id}`, `DELETE …/documents/{id}` |
| Reports | New `GET …/reports/cost-summary`, `GET …/reports/obligations`, `GET …/report-requests` | New report request endpoints (`02-projects-planning.md`) |

## 6. Code conventions (follow the existing modules exactly)

Read these files before writing code. They are the pattern:

- `src/Modules/Projects/AlMotaheda.Modules.Projects/Features/Projects/UpdateProject.cs` (a write slice)
- `…/Features/Projects/ListProjects.cs` (a list with filters and paging)
- `…/Features/Members/AddProjectMember.cs` (create with lock, unique index race, logging)
- `…/Common/*.cs`, `…/Security/*.cs`, `…/Data/**`, `…/ProjectsModule.cs`, `…/Startup/ProjectsStartup.cs`
- `docs/architecture.md`

Rules:

1. One use case = one `internal static class` in `Features/<Area>/<UseCase>.cs` with `Map(...)` and
   `HandleAsync(...)`. Request and response records are nested or sit next to it. Everything is `internal`.
2. Validate first and return every failing field at once (`ValidationErrors`, `Input.*`). Then scope (404), then
   rules (403/409), then save. Dates arrive as `yyyy-MM-dd` strings. Enums arrive and leave by name; any letter
   case is accepted on input.
3. Errors go through `ApiProblem` with a machine `code`. Each module has `Common/<Module>Problems.cs`.
4. Lists: `page` (from 1), `pageSize` (default 25, at most 100), and a response `{ items, page, pageSize, totalCount }`.
   Filtering, sorting and paging run in SQL.
5. Reads use `AsNoTracking()` and project only the columns they return. No lazy loading. No query inside a loop:
   load related names with one query per list (`IUserDirectory.FindAsync`).
6. Every handler takes a `CancellationToken` and passes it on.
7. Structured log entries for every change, by id only: no names, no notes, no amounts.
   Event id ranges: Projects 2401–2999, Workforce 3001–3999, Subcontractors 4001–4999, Fleet 5001–5999,
   Archive 6001–6999.
8. Every endpoint has `.WithName(...)` (unique in the API) and `.WithSummary(...)`. That is the OpenAPI
   documentation.
9. No MediatR, no generic repository, no AutoMapper, no new NuGet package.
10. Comments explain why, in plain English, like the existing code.

### Calculations

Money arithmetic that matters (allocation, pro-rating, totals, reconciliation, FIFO) lives in **pure static
classes** with no database access. A handler loads flat rows with a simple query, calls the pure class, and
returns the result. The summary table and the detail view call the same pure class, so they can't disagree.

### Queries

Every read query that is more than a lookup by key is built by an `internal static` method that returns an
`IQueryable<T>` (in a `…Queries` class next to the feature). Handlers execute it. Tests call
`ToQueryString()` on it to prove EF can translate it to SQL Server SQL.

## 7. Testing

SQL Server is not available in every environment, so tests come in four kinds:

| Kind | Needs | What it proves |
|---|---|---|
| Unit (`Unit/`) | nothing | Pure rules and arithmetic, input rules, the table design (`GenerateCreateScript` contains the constraints), "migrations match the model" |
| Query translation (`Unit/` or `Api/`) | nothing | Every `…Queries` method translates (`ToQueryString()`) |
| API without a database (`Api/`) | nothing | 401 / 403 / 404 / 400 for every endpoint, using `TestApplication` + `StandInDatabase` + `TestTokens` |
| SQL Server (`Database/`, `[DatabaseFact]`) | Docker | The real flows end to end: persistence, constraints, races, project scoping |

- A new endpoint is added to the module's "every endpoint needs a token" theory.
- Arithmetic tests use real numbers and check every total, not only that a call succeeds.
- Database tests are written even where they can't be run. They are reported as "skipped: Docker not available",
  never as passed.

## 8. Check constraints: one trap

A SQL `CHECK` passes when its result is unknown (`NULL`). So `[Quantity] > 0` alone lets a `NULL` quantity through.
Every nullable column in a check is tested with `IS NULL` / `IS NOT NULL` first. See `CK_Project_Site` and
`CK_ProjectExpense_KindColumns`.
