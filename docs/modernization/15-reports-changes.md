# Reports step — report catalog, Excel, print, background exports (brief Phase 4)

Date: 2026-10-16. Design: `14-reports-design.md`.

## 1. Added

| Added | Why |
|---|---|
| Module `AlMotaheda.Modules.Reports` (schema `rpt`), before Finance in `ModuleCatalog` | The catalog, running, Excel, print, background exports and their history |
| `AlMotaheda.Modules.Reports.Contracts` (`IReportSource`) | Each module defines its reports and computes the rows with its own screen queries (one calculation source) |
| Report sources in Finance, Projects, Workforce, Subcontractors, Fleet and HR | 12 reports (below) |
| `rpt.ReportRun` | Every Excel export, background export and print: who, report, filters, rows, when (audited); a background file is kept 7 days |
| `ExportWorker` (hosted service) | Builds large exports with the requester's rights as they were when asked; expired files are removed |
| ClosedXML 0.105.0 (MIT) | Server-side `.xlsx`: right-to-left sheet for Arabic, header block, typed cells, totals row, frozen header |
| Notifications `ExportReady` / `ExportFailed` | The requester hears when a background export is ready or failed |
| `CompanyClock.Zone` | Report headers show company time |

## 2. Reports

| Code | Report | Needs |
|---|---|---|
| `FIN-CASH` | Cash movement by day / week (Saturday start) / month, drill-down to the ledger | finance F |
| `FIN-LEDGER` | Transaction ledger | finance F |
| `FIN-REQUESTS` | Payment requests by status | payments F |
| `FIN-RECON` | Paid / reversed requests against their ledger entries (Matched / Mismatch) | finance F |
| `PRJ-COST` | Project cost summary by cost group (with committed) | projects F + project in scope |
| `PRJ-ITEMS` | Cost breakdown by item: suppliers / internal labor / subcontractors | projects F + project in scope |
| `SUP-ACCOUNT` | Supplier account and payments, per project | projects F |
| `WRK-PAYROLL` | Worker weekly payroll (days, pay, paid, outstanding) | workers F |
| `SUB-ACCOUNT` | Subcontractor account per contract (value, executed, paid, retention, due now) | subcontractors F |
| `EQP-COST` | Equipment cost by kind (rental, fuel, maintenance, other) | equipment F |
| `EQP-FUEL` | Fuel by equipment and project | equipment F |
| `HR-SALARIES` | Employee salaries by month | hr F (HR, Accountant, Executive) |

## 3. Endpoints

| Endpoint | Notes |
|---|---|
| `GET /api/reports` | Only the reports the caller may run, with filters and columns (AR/EN) |
| `GET /api/reports/{code}?…filters&page&pageSize` | Rows of the page, totals of every matching row, note, `totalCount`; each row may carry a drill-down `link` |
| `GET /api/reports/{code}/excel?…&lang=ar\|en` | The whole report as `.xlsx`; above `Reports:ImmediateExportRowLimit` (5,000) rows → 202 `{ exportId }` |
| `GET /api/reports/{code}/print?…&lang=ar\|en` | Print-ready HTML (A4, RTL for Arabic) |
| `GET /api/report-exports` | My exports and prints, with `downloadable` |
| `GET /api/report-exports/{id}/file` | Download my background export (another user's id answers 404) |

403 when the caller may not run the report; 404 for an unknown report or a project out of scope; 400 for bad filters.

## 4. Migration

`Reports/InitialReports` (1 table). **Order:** Platform, Access, Projects, Workforce, Subcontractors, Fleet,
Archive, HR, Procurement, **Reports**, Finance.

## 5. Tests

New project `AlMotaheda.Modules.Reports.Tests` (24): filters, totals, the Excel file (RTL, header, typed cells,
totals) and the print page (escaping, RTL); every module's report definitions well formed; who sees which report;
403 / 404 / 400; and one SQL Server flow: ledger, cash, reconciliation, supplier account and cost summary
reports equal the ledger and the screens; Excel at once; print; a background export downloaded only by its requester,
with its notification; the export history.

## 6. Frontend contract changes

1. New menu **Reports**: the catalog from `GET /api/reports`; one generic screen per report (filters → table →
   totals → Excel / Print); rows open their `link`.
2. Large Excel exports answer 202: show "you'll be notified", then the file from **My exports**.
3. New notification types `ExportReady`, `ExportFailed`.

## 7. Open questions (Needs Confirmation)

1. **Money in**: no client income / certificates are recorded, so cash movement shows 0 in.
2. **Office expenses vs budget** and **fuel by driver**: no data yet (no office module, no driver on fuel records).
3. **PDF**: print pages are HTML for the browser's "Save as PDF". A server PDF library (QuestPDF) needs a paid
   licence above US$1M yearly revenue.
4. **Week start**: Saturday (Egyptian working week) — confirm.
5. **Dashboards**: the brief asks dashboards to use the same calculation; the existing project overview already
   shares the cost loader with `PRJ-COST`. Company dashboards are part of the SignalR / caching phases.

## 8. Results

2,058 of 2,058 tests pass, the 12 projects run one after another: Api 33, Access 180, Archive 216, Finance 57,
Fleet 193, HR 99, Platform 38, Procurement 92, Projects 638, Reports 24, Subcontractors 234, Workforce 254.
Found on the way: the export worker polled the database in test hosts that have none, which broke the
"no database work before validation" checks; it now has a switch (`Reports:BackgroundExports`), off in those hosts,
like the outbox dispatcher.
