# Audit report

Date: 2026-10-15. Scope: the project workspace of the backend (Projects, Workforce, Subcontractors, Fleet,
Archive, Access) and the Flutter app that uses it. Method: code review of both sides, the backend's own test
suites, a scripted run of every workflow against the real API on an isolated database, and builds of the app.

## 1. Results at a glance

| Check | Result |
| --- | --- |
| Backend build | Succeeds. Warnings are NuGet advisories (see §6), no compiler errors. |
| Backend tests | 1,719 tests in 7 projects, 0 failing ([per project](progress.md)) |
| Live API run (`e2e.py`, isolated DB) | 149 passed, 0 failed, 0 blocked |
| `flutter analyze` | No issues |
| `flutter test` | 65 passed |
| Android debug APK / macOS debug app | Both built; APK path and checksum in [progress.md](progress.md) |
| Manual click-through of the app | Not done yet — needs a person to sign in ([progress.md](progress.md)) |

## 2. Defects found and fixed in the backend

| # | Problem | Risk | Fix |
| --- | --- | --- | --- |
| B1 | A supplier payment sent again with the same idempotency key but a different body (amount, date, allocations) silently returned the first payment. | The second payment was lost while the app showed success. | The key now matches only an identical payment; a different body gets `409 idempotency_key_reused`. The check is repeated after the project lock is taken, so two simultaneous requests can't both create a payment. Same fix for worker payments (B2) and subcontractor payments (B3). |
| B4 | Over-paying a worker returned the worker's outstanding amount across all projects, including projects the caller can't see. | Leaked pay information outside the caller's projects. | The amount is named only when the caller sees every project involved. |
| B5 | A worker payment could be voided, and its detail read, by a user who couldn't see all the projects it settled. | Acting on records outside the caller's scope. | 404 unless every settled project is visible; views come from one place (`WorkerPaymentVisibility`). |
| B6 | A subcontract measurement amount and a contract line total (quantity × price) had no upper bound. | A huge value overflowed the money column and failed with a 500. | Both capped at the same maximum as every other amount; a validation error explains it. |
| B7 | The items table requires *See money*, so a site engineer (no See money) could not choose the item a supply, daily report or labor sheet belongs to. | Records were left "not linked yet" or engineers were blocked. | New `GET /api/projects/{id}/items/options`: names, category, unit and status only, no money; projects View and project scope. |
| B8 | There was no way to know that something was waiting for you (a record to review, a record returned to you, a report asked of you). | Records sat unreviewed; returns went unnoticed. | New work inbox, `GET /api/inbox` (see [location-and-notifications.md](location-and-notifications.md)). |
| B9 | Five API tests failed on a developer machine because the developer's user-secrets (apply migrations on start, bootstrap account) leaked into the test host. | Red test runs on the machine that matters. | The test host now pins those settings in memory. |

Every fix has a test: `SupplierPaymentTests`, `LaborFlowTests`, `SubcontractFlowTests`, `CoreApiSecurityTests`
(new endpoint in the permission matrix) and the new `WorkInboxTests`. One older test (`ExpenseWorkflowTests`)
expected a response that contradicts the role defaults and was corrected; see [progress.md](progress.md).

## 3. Defects found and fixed in the Flutter app

| # | Problem | Fix |
| --- | --- | --- |
| F1 | The item picker used the money endpoint, so it failed for engineers. | Uses `items/options`; the See-money gate on the picker is gone. |
| F2 | "Paid" on an item added supplier and subcontractor payments on the phone. | Shows the server's three figures separately (suppliers · subs · labor). |
| F3 | Payment buttons checked the wrong permission module on three screens. | Check `payments` Create, as the server does. |
| F4 | The supplier payment form loaded only the first page of open records and sent unrounded allocations. | Pages through every unpaid and partly paid record, oldest first; allocations rounded to 2 decimals. |
| F5 | Query building dropped `false` booleans, so "show inactive = false" was never sent. | `cleanQuery` keeps `false`. |
| F6 | The site map jumped to the user's own position. | The map stays on the site; "locate me" only measures the distance. |
| F7 | Refreshing after an action could use a disposed provider (`ref` after `await`). | `projectRefresher` captures the notifier first. |
| F8 | Downloads overwrote a file with the same name and could leave half-written files; a preview could download twice. | `name (2).ext`, write to `.part` then rename, in-flight previews de-duplicated. |
| F9 | Cached previews survived sign-out. | Cleared on sign-out. |
| F10 | macOS: secure storage threw `PlatformException` (the data-protection keychain needs a signing entitlement); downloads folder not writable. | Login keychain on macOS; `files.downloads.read-write` entitlement. |
| F11 | Android build hung trying to install NDK 28.2. | `ndkVersion` set to the installed NDK. |
| F12 | No notifications. | Bell with a badge (polls every 60 s and on resume) and a "My work" page that opens each record. |
| F13 | The prototype offers "Export CSV" on lists; the app had none. | Export CSV on the items table and the expenses list (every page of the current filter, UTF-8 with BOM for Arabic in Excel, formula-injection safe). |

## 4. Verified as working (live API, isolated database)

Authentication (login, wrong password, refresh rotation, logout, refresh after logout), role visibility, IDOR
across projects, the expense review cycle (record → return with note → fix → approve, frozen after approval),
linking a record to an item without creating a new record, "not linked yet" and other costs reconciling to the
project cost, partial / idempotent / voided supplier payments, budgets, catalog price changes, labor sheets split
across items and worker pay, subcontract measurement and payment, equipment fuel cost and payment, daily reports,
report requests, archive upload/download with type checks and `nosniff`, completed projects read-only, the inbox
for each role, and the money-free item options. The full list is in `_sync/qa/e2e-results.md`.

## 5. Behaviour that is by design, but worth a decision

These come from the default role permissions (`PermissionCatalog.RoleDefaults`); the Executive can change them on
the Roles page.

- A **Manager** cannot approve expenses, supplier payments or budgets (no projects Approve). Approvals go to the
  Accountant and the Executive.
- A **Manager** cannot assign workers to a project (needs workers Edit).
- **Subcontract** measurements and manpower are approved by the Executive only.
- An **Engineer** without See money sees only their own expenses, without paid/outstanding.

## 6. Open items

- **NuGet advisories**: `System.Security.Cryptography.Pkcs` 9.0.4 is flagged (NU1902/NU1903) through a transitive
  dependency. Upgrading it is a dependency change and was left for the team.
- **Not in the backend yet** (prototype screens with no API): client income and certificates, finance /
  transactions and bank reconciliation, office management, company vehicles and fuel, guards, the cross-project
  reports centre and Excel/PDF exports. The app does not fake these.
- **GPS attendance, location tracking and push notifications**: need product and privacy decisions first; see
  [location-and-notifications.md](location-and-notifications.md).
- **Developer database**: when the backend was first started with the developer's own settings, it applied five
  additive workspace migrations to the developer database (new tables and columns; nothing dropped). Every later
  write test used the isolated database.
