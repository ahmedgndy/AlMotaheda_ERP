# Cost-flow integrity: completion report

Date: 2026-10-17. Design and inventory: `01-inventory-and-design.md`.

## 1. Branch

`feature/backend-cost-flow-integrity`, made from `main` 96ceae9 (the same commit as `integration/e2e-qa`). Local only:
nothing pushed, pulled, fetched or merged. The main checkout and every other branch are untouched.

## 2. Commits

On `feature/backend-cost-flow-integrity` (on top of `main` 96ceae9):

| Commit | Contents |
|---|---|
| COMMIT1 | Cost-flow integrity: code, migrations, tests and this report |
| (the commit that fills in this table) | Report: commit hash |

## 3. Modules inspected

Access, Projects (catalog, items, expenses, suppliers, payments, overview, reports, inbox, site location), Workforce
(sheets, allocations, payments), Subcontractors, Fleet, Archive, HR, Procurement (invoice posting), Finance (payment
requests, ledger), Platform (audit, outbox, notifications, SignalR, cache, push), Reports. Docs: `architecture.md`,
`project-workspace/00…07`, `modernization/00…21`, module READMEs, migrations and tests.

Not found on this machine: the project-workspace prototype (`alsaleh-prototype66.html`) and the ERD file. The only
prototype found covers office, fuel and vehicles. The workspace design docs (written from prototype66 and the ERD)
were used as the workflow reference.

## 4. Existing functionality reused (not rebuilt)

| Need in the brief | Already there, reused |
|---|---|
| One source record per cost, link to an item, never a copy | `ProjectExpense`, labor lines, subcontract work, `ItemsLedger` |
| Approved = cost; pending/returned/rejected = not cost; paid only from allocations; voids kept | Settlement rules in every module |
| Price history, effective dates, transaction keeps its price | `ItemPrice`, `CatalogPriceAtRecording` |
| Purchase → receipt → invoice → cost → payment stages | Procurement (PR, RFQ, PO, GRN, 3-way match) + Finance |
| Daily labor (workers, days, overtime, allowances, deductions with reasons, split over items, review, payments) | Workforce |
| Subcontract commitment / measured work / approved / paid / retention / advances | Subcontractors |
| Equipment register, rentals, assignments with a responsible engineer, costs, payments, voids, corrections | Fleet |
| Salaries, salary sheet, payroll payment | HR + Finance |
| Payment approval, bank mock, ledger, idempotency | Finance |
| Audit with old/new values, outbox, stored notifications, SignalR after commit, cache invalidation, push | Platform |
| Cross-project protection (`IProjectAccess`, `IProjectItems`, `IProjectRecordOwnership`) | Projects contracts |

## 5. New APIs and slices

| Area | Endpoints |
|---|---|
| Master catalog (management area) | `GET/POST /api/catalog/items`, `GET/PUT /api/catalog/items/{id}`, `POST /api/catalog/items/{id}/price-changes`, `GET /api/catalog/price-changes`, `POST /api/catalog/price-changes/{id}/approve|reject` |
| Project selection | `GET/POST /api/projects/{p}/catalog-selection` |
| Price variance | `GET/PUT /api/catalog/pricing-policy`, `GET /api/catalog/price-variances`, `GET /api/projects/{p}/price-variances`, `POST /api/projects/{p}/price-variances/{id}/accept|reject` |
| Composite items | `PUT /api/projects/{p}/items/{id}/components`; item detail gains `components`, `equipmentLines`, `staffLines` |
| Supplier profile | `GET /api/suppliers/{id}`, `GET /api/suppliers/{id}/history`, `GET /api/suppliers/{id}/documents`; optional profile fields on create/edit |
| Equipment | `POST /api/projects/{p}/equipment-costs/{id}/item`; `projectItemId` on create; `SpareParts` cost type; `POST/GET /api/projects/{p}/repair-requests`, `GET …/{id}`, `POST …/{id}/approve|reject|complete|cancel` |
| Salary allocation | `GET/PUT /api/hr/cost-allocation-policy`, `GET/POST /api/hr/project-days`, `PUT /api/hr/project-days/{id}`, `POST /api/hr/project-days/{id}/approve|return|reject`, `POST /api/hr/salary-allocations/run`, `GET /api/hr/salary-allocations`, `GET /api/hr/project-options[/{p}/items]` |
| Notifications | "Waits for your review" (`ReviewRequested`) to permission holders who see the project |

## 6. Database changes and migrations

| Module | Migration | Contents |
|---|---|---|
| Access | `AddFinancialManagerRole` | Role 9 `financial_manager` + 23 defaults; Accountant `catalog` Edit (inserted only if missing) |
| Projects | `MasterCatalogVariancesComponents` | `MasterItem`, `MasterItemPrice`, `PricingPolicy` (seeded 0 % / 0.01), `PriceVariance`, `ProjectItemComponent`; `MasterItemId` on `CatalogItem` and `ProjectItem` (unique per project); supplier profile columns; cost group `Staff` in the budget-line check |
| Fleet | `ItemLinksAndRepairRequests` | `EquipmentCost.ProjectItemId` (+ linked at/by), `SpareParts`, `RepairRequest`; hand-written keys to `projects` |
| HR | `SalaryCostAllocation` | `CostAllocationPolicy` (not set), `ProjectWorkDays`, `SalaryCostAllocation`; hand-written keys to `projects` |
| Archive | `SupplierAndRepairLinks` | Link types `Supplier`, `RepairRequest` (check constraint) |

All money is `decimal(18,2)`; quantities `decimal(18,3)`; days `decimal(4,1)`. Every status has a check constraint;
every one-at-a-time rule has a filtered unique index (one waiting variance per supply, one pending/approved project-days
entry per employee/project/month, one active allocation per entry, one selection per master item per project). No
total is stored. Migration order is unchanged (Platform, Access, Projects, …, HR, Procurement, Reports, Finance).
Applied only to throw-away test databases (Testcontainers); **not** to `AlMotahedaErp`, `AlMotahedaErp_Test` or the server.

## 7. Item catalog and composite cost

- One master catalog; projects select from it. Work/composite → project item; materials/services/labor/equipment →
  priced project catalog item. Linked by `MasterItemId`; selecting twice changes nothing; the master is never copied.
- Reference price = the project's own approved price if one applies on the date, else the master's [Confirm].
- Components are a plan. The item's cost stays its records; the detail sorts them by component (material by catalog
  item, labor + staff, equipment, subcontract) and shows the rest as "other". Components + other = item cost; the
  project total is the sum of records, so nothing is counted twice (test E).

## 8. Purchase → expense → payment

Unchanged stages, kept distinct: a supply is recorded (not cost) → reviewed (approved = cost) → paid through a Finance
payment request (paid ≠ cost). Procurement's chain posts matched invoices as approved supplies once (unique source).
Fuel/equipment costs are refused as project expenses (`use_equipment_costs`). Test B: a 2,000 cement purchase is one
record, cost only after approval, paid separately, never in site expenses.

## 9. Price changes and variances

- Accountant requests (catalog Edit) → Financial Manager or Executive decides (catalog Approve, re-checked in the
  database, never one's own request) → applies from its effective date; recorded supplies keep their price (test D).
- A supply whose unit price is outside `max(reference × tolerance, rounding)` raises a price variance; the supply
  can't be approved until accepted; rejected keeps it blocked until fixed; accepting never changes the catalog
  (test C). Thresholds are the Executive's.

## 10. Labor and salary allocation

- Daily labor: unchanged module, proven with 20 workers split over items, overtime, allowance and a deduction reason
  (test F): 8,180 counted once after approval, refused over a day or twice a day.
- Salaries: HR records days per employee/project/month, the Accountant approves, the allocation run charges
  (basic + fixed allowance) ÷ working days × days. Working days are not set until the Executive sets them. Re-runs
  charge nothing twice; corrections reverse and re-charge; payroll pays company money; the project shows the
  allocation as paid once the month's sheet is paid (test G: 5,000 → 6,000, ledger −20,000 company, 0 project).

## 11. Suppliers

Optional profile fields; one company-wide history built from the same settlement rule as each project's balances;
only the caller's projects; amounts only with See money; documents linked from project archives (one file, no copy)
(test I).

## 12. Equipment

Equipment costs can be linked to an item; `SpareParts` added; identical costs refused (`duplicate_cost`); repair
requests by the responsible engineer or equipment Create/Edit, decided with equipment Approve [Confirm], never a cost,
completed with an optional link to the repair's cost; voided payments keep their history (test H).

## 13. Notifications

New: `ReviewRequested` to every user who holds the reviewing permission now and can see the project (supplies and
expenses, equipment costs, repair requests, labor sheets, price changes, master price changes, price variances, salary
sheets, project days); decisions go back to the requester (master prices, variances, repair requests, project days).
Stored in the outbox's transaction, sent live only after commit. No amounts in titles. Push to phones uses the
existing FCM sender, which still needs the owner's Firebase credentials: **not operational**.

## 14. Tests

Run on 2026-10-17, one project at a time (`dotnet test <project> --no-build`, after one `dotnet build`), SQL Server
in Testcontainers (throw-away databases):

| Test project | Passed | Failed |
|---|---|---|
| AlMotaheda.Api.Tests | 34 | 0 |
| Access | 185 | 0 |
| Archive | 216 | 0 |
| Finance | 57 | 0 |
| Fleet | 193 | 0 |
| HR | 106 | 0 |
| Platform | 109 | 0 |
| Procurement | 92 | 0 |
| Projects | 668 | 0 |
| Reports | 24 | 0 |
| Subcontractors | 234 | 0 |
| Workforce | 254 | 0 |
| AlMotaheda.Integration.Tests | 18 | 0 |
| **Total** | **2,190** | **0** |

Subcontractors failed 14 database tests once in the full run, all with the same SQL Server connection error while
its container was starting (`pre-login handshake`, no assertion failed); run again on its own, on the same build:
234 passed. Docker has 4 GB, so test projects are run one at a time.

The end-to-end tests of this brief are in `tests/AlMotaheda.Integration.Tests` (`CostFlowCatalogTests`,
`CostFlowRecordTests`), each against SQL Server through the real API with the real roles:

| Test | What it proves |
|---|---|
| A | The project's manager selects master items (the site engineer can't); selecting again changes nothing; work items become بنود, priced items catalog items, each linked; no price is copied (the master's price applies); the master's unit is locked once used |
| B | A purchase is one record: not cost before approval, cost after, paid separately, never a site expense |
| C | A price above and below the reference raises a variance; approval waits for it; reject/accept with a reason; the catalog never changes |
| D | Accountant requests, the requester can't decide, the Financial Manager approves; effective date; old supplies keep their price |
| E | Composite item: components + other = item cost = what the project counts (no double count) |
| F | 20 workers, overtime, allowance, a deduction with a reason, split over items: 8,180 counted once after approval |
| G | Salary allocation: refused until working days are set; 5,000; re-run charges nothing; a correction reverses and re-charges 6,000; paid with payroll |
| H | Equipment: the same cost twice (or fuel again as a project expense) refused; fuel linked to an item, spare parts not yet; a repair request only by the responsible engineer, never a cost; a voided payment keeps history and the cost stays |
| I | Supplier profile and history across projects, only the caller's projects, money only with See money, documents without a copy |
| J | Records never cross projects (item links, labor, documents, components, reads); prices and policies only for their holders; money only with See money; nothing changed by the refused calls |
| K | Full reconciliation: items, not-linked, overview, supplier balances, history and `PRJ-COST` report agree (12,100 / 5,200 / 6,900 / 500 pending) |

Notifications are checked inside B, C, D, G and H (the reviewer hears, the submitter doesn't, the decision goes back
to the requester). New unit tests: price variance limits and figures, reference price layering, component breakdown, item ledger with
equipment and staff, salary allocation plan (amount, re-run, correction, skipped), review notification rules.

## 15. Reconciliation

Test K builds a project with credit and custody supplies, a payment, a site expense, labor and a worker payment,
equipment (linked and not), subcontract work and a pending supply, then checks every item row, the not-linked group,
the reconciliation, every overview group, the grand totals, the supplier balances, the supplier history and the
`PRJ-COST` report against figures computed by hand from the records: all equal (12,100 recognized, 5,200 paid,
6,900 outstanding, 500 pending).

## 16. Build

`dotnet build AlMotahedaERP.slnx`: **Build succeeded**, 0 errors. Two xUnit analyzer warnings in the new integration
tests (`Assert.False(… .Any(…))`) were fixed to `Assert.DoesNotContain`; the rebuild after that reported no warnings.
New migrations were generated with `dotnet ef` and made `internal` like the existing ones.

## 17. Security issues found and fixed

| Issue | Fix |
|---|---|
| Price decisions checked only the Executive **role** from the token | Now `catalog` Approve re-checked in the database (`PermissionGate`) |
| Supply prices could differ from the catalog without anyone reviewing | Price variance review blocks approval |
| The new `Staff` group crashed every project overview until registered as a source group | Fixed before commit; covered by the cache and K tests |
| Two queries filtered after projecting (500 at runtime): repair requests, staff item lines | Filters moved before the projection |
| A price variance could be decided on a completed (read-only) project | Refused with 409 `project_completed`, like every other write |
| A test expected the old "Executive role only" price decision | Replaced by two tests: refused without catalog Approve (no database call), and refused (`permission_revoked`) when the token's Approve is no longer in the database, with nothing written |

## 18. Remaining decisions and blockers

1. **Reference price layering** (project price first, then master) — confirm.
2. **Variance thresholds** seeded 0 % and 0.01 EGP; both directions flagged — confirm values.
3. **Procurement invoices** are controlled by the PO price and the 3-way match, not re-checked against the catalog — confirm.
4. **Financial Manager defaults** (catalog Approve; read-only money elsewhere; no payment approval) — confirm.
5. **Salary allocation**: who records days (HR) and who approves (Accountant); pay = basic + fixed allowance; working
   days per month (not set) — decide and set.
6. **Repair approval** reuses equipment Approve (Executive, Manager, Accountant) — confirm.
7. Supplier documents must belong to a project (the archive is per project); company-only supplier documents need a decision.
8. Material **consumption/stock** is not tracked (deliveries only) — needs an inventory decision.
9. Engineer's periodic **equipment report** as a separate record is not built: the period's costs and a repair request cover it.
10. Weekly payroll for workers, corrections of approved labor days and subcontract work: still open from before.
11. Push notifications: Firebase credentials pending.
12. **First price of a new master item**: approved at once when its creator may decide prices (catalog Approve,
    re-checked in the database), otherwise a pending request. Same as the project catalog's first price — confirm,
    or require a second person for master items too.
13. **Completed projects and salaries**: project days can't be recorded on a completed project, but days recorded
    before completion can still be approved and allocated (the salary was really spent there) — confirm.
14. **Project catalog items added directly** (not selected from the master) still get their first price approved
    at once by whoever adds them (catalog Create: Executive, Manager by default), as before this change. That price
    becomes the reference for price variances. Decide whether items must come from the master catalog, or whether
    a directly added item's first price should wait for catalog Approve. Unchanged here.

## 19. How to review

```bash
git log --oneline main..feature/backend-cost-flow-integrity
```

```bash
git diff --stat main...feature/backend-cost-flow-integrity
```

Run one test project at a time (Docker has 4 GB), for example:

```bash
dotnet test tests/AlMotaheda.Integration.Tests
```
