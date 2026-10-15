# Phase 2 — Changes log (S1, S2 and the first review pass)

Date: 2026-10-16. No API behaviour changes in this step: same routes, same responses, same error codes, same
lock names, same database model (no migration).

## 1. Removed, and why

| Removed | Where | Why it was not needed |
|---|---|---|
| `ValidationErrors` + `Input` (6 copies) | `Common/Validation.cs` in Projects, Workforce, Subcontractors, Fleet, Archive; `ValidationErrors` in Access | Identical copies. Now one public copy in `AlMotaheda.BuildingBlocks.Validation`. Access keeps its own user-field rules (`InputRules`). |
| `RowVersions`, `Paging`, `NumberInput`, `SqlViolations` (5–6 copies) | `Common/DataHelpers.cs` (Workforce, Subcontractors, Fleet, Archive: file removed), Projects `WorkspaceHelpers.cs` and `DataHelpers.cs`, Access `DataHelpers.cs` | Identical copies. Now in BuildingBlocks (`RowVersions`, `Paging`, `NumberInput`) and BuildingBlocks.Data (`SqlViolations`). The module-specific "which index failed" helpers (`UniqueViolations` in Access and Projects) stay and use the shared check. |
| `SqlList` (5 copies) | each module's `DbContext` file, Projects `ProjectConfigurations.cs` | Identical. Now `AlMotaheda.BuildingBlocks.Data.SqlList`. |
| `sp_getapplock` code (6 copies) | `AppLock` (Workforce, Subcontractors, Fleet, Archive), `ProjectLock`, `UserManagementGate` | Identical SQL. Now `SqlAppLock`. Each module keeps its own lock **names**, unchanged. |
| Startup migration code (6 copies) | `*Startup.cs` | Identical apart from the required tables. Now `ModuleMigrations.ApplyAsync`. Each startup class is a few lines. Access still creates the first Executive. |
| Date/text/money column conventions + UTC converter (6 copies) | each `DbContext.ConfigureConventions` | Identical. Now `StorageConventions.Apply`. Access had no decimal rule, but has no decimal column, so its model is unchanged. |
| Executive-only policy body (2 copies) | Access `ExecutiveOnly`, Projects `ProjectPolicies` | Now `RequireRole` in BuildingBlocks. The two `RequireExecutive` helpers remain as one-liners. Finance, HR and Procurement will need the same check. |
| `ValidationErrors.Count` | Workforce | Same as `FieldCount` in the other modules; one name kept (`FieldCount`). |
| `WorkspaceAccess.CallerManagesProject` | Projects | Never used. The rule it described lives in `ItemRules.ManageProblem`. |
| `WorkerAssignment.Covers(date)` | Workforce | Never used. The same check runs in SQL in the sheet queries. |

Kept on purpose (looked unused but are not): the EF configuration classes (found by
`ApplyConfigurationsFromAssembly`), the foreign-key name constants (used in the hand-written migrations),
`ArchiveOptions.PostConfigure` (options framework), extension classes like `HealthCheckEndpoints`.

The 12 endpoints the Flutter app doesn't call (coverage matrix, section C) are **kept**: they are master-data
maintenance the app will need.

## 2. Added

| Added | Why |
|---|---|
| Project `src/BuildingBlocks/AlMotaheda.BuildingBlocks.Data` | Home for the SQL Server helpers above, so `AlMotaheda.BuildingBlocks` stays free of EF Core. |
| `BuildingBlocks/Validation/*` | Shared input rules (see section 1). |
| `PermissionAuthorization.RequireRole` | One role-check policy for every module. |
| Architecture tests | Both BuildingBlocks projects must not reference modules or the host; the core BuildingBlocks must not reference EF Core. |
| Global `Using` items in each module `.csproj` | The shared namespaces are used by most slices; one line per module instead of one per file. |

## 3. Security fixes (S2)

Same content as the existing local branch `deps/security-advisories` (f1cfe27), taken file for file, so the two
can't disagree when merged.

| Package | Was | Now | Advisory | Reaches |
|---|---|---|---|---|
| `System.Security.Cryptography.Pkcs` | 9.0.4 (through `Microsoft.Data.SqlClient` 6.1.1) | **9.0.21** (central transitive pin) | GHSA-846f-f764-76mx (high), GHSA-2fmv-rv2c-59x7 (moderate), .NET denial of service | NuGet graph of the API and tests |
| `SSH.NET` | 2024.2.0 (through `Testcontainers.MsSql` 4.6.0) | **2026.0.0** (Testcontainers 4.16.0) | GHSA-q939-rpr3-3284, GHSA-mggc-4xg6-vcxf (high) | Tests only |

**Action for the owner:** the pin fixes what NuGet resolves, not what the API runs. A web app loads this assembly
from the ASP.NET Core shared framework, and the advisories also cover 10.0.0 to 10.0.12. This Mac runs
ASP.NET Core **10.0.2**; every machine that runs the API needs the .NET 10 runtime **10.0.13 or later**.

## 4. Review findings that need no change

- Every read query uses `AsNoTracking()` and projects only the columns it returns.
- No query runs inside a loop (checked every `for`/`foreach` body in `src`).
- Every list endpoint is paged (`Paging`, max 100).
- No `TODO`, `FIXME` or commented-out code in `src`.
- No `float`/`double` anywhere; money is `decimal(18,2)`.
- Hard deletes (`DELETE …/items/{id}`, contract items) are refused when the row has any linked cost record;
  documents are soft-deleted.

## 5. Next (from the refactor plan, in order)

Platform (audit log, outbox, notifications) → Finance (payment requests, bank mock, ledger, backfill; the four
payment endpoints switch to requests, as approved) → M5/M6/M8/M9 → HR → Procurement → Reporting → SignalR →
caching → backup.
