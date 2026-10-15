# Feature matrix

How each feature was verified:

- **API**: covered by the live run `_sync/qa/e2e.py` against the isolated database (section letter in brackets).
- **Test**: covered by the backend's automated tests (xUnit, real SQL Server in a container).
- **App**: `flutter analyze` and `flutter test` pass, and the screen calls the endpoint shown. Clicking through
  every screen by hand is listed separately in [progress.md](progress.md).

All routes are under `/api`; `{p}` is the project id.

| Feature | Backend endpoint(s) | App screen | Verified |
| --- | --- | --- | --- |
| Sign in, refresh, sign out, change password | `/auth/login`, `/auth/refresh`, `/auth/logout`, `/me` | Login, Change password, My account | API (A), Test |
| Users, roles and permissions | `/users`, `/roles` | Users, User form, Roles | API (B), Test |
| Projects list, create, edit, team | `/projects`, `/projects/{p}`, `/projects/{p}/members` | Projects, Project form, Team | API (C), Test |
| Project overview | `GET /projects/{p}/overview` | Workspace → Overview | API (H) |
| Site location and map | project `siteLatitude/Longitude/RadiusMeters` | Overview map, Project form | API (C), App |
| Items ("بنود") with money | `GET /projects/{p}/items`, item detail, `items/unallocated` | Workspace → Items, Item detail | API (D, F), Test |
| Items without money (pickers) | `GET /projects/{p}/items/options` **(new)** | Item picker on supply, report, labor, budget forms | API (D), Test |
| Link records to an item | `PUT /projects/{p}/expenses/{id}/item` (and the item field on labor sheets and subcontract lines) | Link records sheet | API (F), Test |
| Expenses and supplies, review cycle | `/projects/{p}/expenses` (+ `approve`, `return`, `reject`) | Workspace → Expenses, Expense form, Expense detail | API (E), Test |
| Supplier payments and balances | `/projects/{p}/supplier-payments` (+ `void`), `supplier-balances` | Supplier payments | API (G), Test |
| Budget | `/projects/{p}/budgets` (+ `approve`), `/projects/{p}/budget-comparison` | Workspace → Budget | API (I), Test |
| Catalog and price changes | `/projects/{p}/catalog-items`, `…/price-changes` | Workspace → Catalog | API (J), Test |
| Workforce: workers, assignment, labor sheets, worker pay | `/workers`, `/projects/{p}/workers`, `/projects/{p}/labor/sheets`, worker payments | Workspace → Labor, Sheet form | API (K), Test |
| Subcontractors: contracts, lines, measurements, payments | `/projects/{p}/subcontracts/…` | Workspace → Subcontractors, Subcontract page | API (L), Test |
| Equipment: assignment, costs, payments | `/equipment`, `/projects/{p}/equipment`, `/projects/{p}/equipment-costs` | Workspace → Equipment, Equipment cost | API (M), Test |
| Daily reports | `/projects/{p}/daily-reports` (+ submit, review, return) | Workspace → Daily reports | API (N), Test |
| Report requests | `/projects/{p}/report-requests` (+ start, submit, return, complete, cancel) | Workspace → Report requests | API (N), Test |
| Archive: upload, list, download, preview, link to records | `/projects/{p}/documents` (+ `/content`) | Workspace → Archive, Document page, attachments on records | API (O), Test |
| Cost reports | `/projects/{p}/reports/cost-summary`, `/reports/obligations` | Overview cards | API (H) |
| Work inbox / notifications | `GET /inbox` **(new)** | Bell in the top bar, My work page | API (E, J, K, L, M, N), Test (`WorkInboxTests`) |
| CSV export | none needed: the app exports what the API returns | Export CSV on Items and Expenses **(new)** | App (unit tests for the encoder) |
| Cross-project access (IDOR) | every `{p}` route | — | API (P), Test |
| Completed project is read-only | every write route | — | API (Q), Test |
