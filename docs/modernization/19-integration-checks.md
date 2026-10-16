# Integration and consistency checks (brief Phase 8)

Date: 2026-10-16. Tests: `tests/AlMotaheda.Integration.Tests` (new) and `tests/AlMotaheda.Api.Tests/Architecture/EndpointAuthorizationTests.cs` (new).

## 1. Results per check

| # | Brief check | Result | Where it is proved |
|---|---|---|---|
| 1 | HR salary sheet → Accountant review → payment request → Executive → ledger → reports → dashboards | ✅ through reports: the sheet is Paid; `HR-SALARIES` net = ledger (Employees) = `FIN-CASH` money out = 10,000; `FIN-RECON` difference 0; HR sees its report but not the ledger. **Dashboards: none exist yet** | `ChainTests.Salary_sheet_to_...` |
| 2 | Daily team → Accountant → weekly payroll batch → Executive → ledger → project labor cost → cost item | ✅ with the existing flow (sheet approved by the project manager, worker paid through a payment request): overview Labor 400 / paid 400 for manager, accountant and Executive; `PRJ-COST`, `PRJ-ITEMS` (item labor column), `WRK-PAYROLL` and `FIN-LEDGER` agree. **The weekly payroll batch is not built** (open since Phase 0: workers are paid per worker, oldest day first) | `ChainTests.Daily_team_to_...` |
| 3 | Supplier invoice → payment → supplier balance → cost item → project total | ✅ PR → PO → GRN → matched invoice posted → half paid: supplier balance, `SUP-ACCOUNT`, the cost item's supplier cost, the Materials group (recognized, paid, committed 0) for all three roles, the project ledger and the invoice's payment state agree | `ChainTests.Supplier_invoice_to_...` |
| 4 | Fuel / equipment / maintenance → project cost | ✅ after a fix (below): Equipment group 3,500 for all three roles = `EQP-COST` total = `PRJ-COST`; `EQP-FUEL` litres and cost | `ChainTests.Fuel_and_maintenance_...` |
| 5 | Price change → Executive approval → price catalog → cache invalidation → live update | ✅ the catalog shows the new price at once (the catalog is not cached, by design); Purchasing receives the `ItemPrice` change live on `company:procurement`; the next requisition is estimated at the new price | `ChainTests.A_price_change_...` |
| 6 | Any financial change → SignalR → dashboards refresh → cache invalidated | ✅ one dependency map drives both (Phases 5–6); proved for an approval and a payment | `LiveUpdateTests`, `CacheTests` (Platform) |
| 7 | Every report total = ledger total for the same filter | ✅ ledger, cash movement, reconciliation, worker, supplier and salary chains above; `ReportFlowTests` (Reports) | above |
| 8 | The same number identical in Project, Accountant, Executive, Reports | ✅ every chain reads the overview as manager, accountant and Executive and compares it with the reports | above |
| 9 | Every endpoint has an authorization policy | ✅ all `/api` endpoints carry a policy; only sign-in, refresh and sign-out are anonymous, and a new anonymous endpoint fails the test until it is listed on purpose | `EndpointAuthorizationTests` |
| 10 | Every financial action audit-logged (who, what, when, old value, new value) | ✅ every module's saves write `platform.AuditEntry` in the same transaction (user, time, entity, and `[old, new]` per changed field); proved for payment requests (Added, then `Status: [Submitted, Approved/Paid]`). Backup settings changes are written explicitly (Platform's own context isn't audited automatically) | `ChainTests.Daily_team_to_...`, Platform tests |

## 2. Fixed by these checks

| Problem | Fix |
|---|---|
| `EQP-COST` / `EQP-FUEL` answered 500 as soon as there was an equipment cost: the report filtered on a projected row (corrections included) that SQL can't translate | Filter after loading the project's rows (`FleetReports`) |

## 3. Not covered (open)

1. **Company dashboards** (executive, accountant) don't exist yet; the overview and the reports share one calculation
   source, which any dashboard must reuse.
2. **Weekly payroll batch** for workers: not built; needs the owner's decision on how a week is closed and paid.
3. Bank confirmation by the bank itself (callback): a person confirms today (Finance open question 3).

## 4. Results

2,105 of 2,105 tests pass, the 13 projects run one after another: Api 34, Integration 5, Access 180, Archive 216,
Finance 57, Fleet 193, HR 99, Platform 79, Procurement 92, Projects 638, Reports 24, Subcontractors 234,
Workforce 254.
