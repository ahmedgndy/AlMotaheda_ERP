# Final report: backend modernization

Date: 2026-10-16. Branch `refactor/shared-building-blocks` (local only, 13 commits on top of `main`).
Each step has its own detailed note in this folder (`05` to `20`); this report sums them up.

Labels: **[Company]** = a rule from the brief or the owner; **[Industry]** = a common practice we chose;
**[Confirm]** = needs the owner's decision (listed in section 8).

---

## 1. What was removed, and why

No feature or endpoint was removed. What was removed was copied code (details: `05-phase2-changes.md`).

| Removed | Why |
|---|---|
| 5 to 6 identical copies each of the input checks, row version / paging / number helpers, the SQL list type, the SQL lock code, the startup migration code and the column conventions | One shared copy now lives in `BuildingBlocks` and `BuildingBlocks.Data`. Same behaviour |
| The "register the database" block in each of the 6 modules | Replaced by one helper that also adds the audit trail |
| Two copies of the "Executive only" check | One shared `RequireRole` |
| `WorkspaceAccess.CallerManagesProject`, `WorkerAssignment.Covers` | Never used |
| Public status setters on records (expenses, sheets, costs, budgets…) | Status now changes only through the record's own methods, along an allowed path (M5) |
| Direct "record a payment" in 4 places | Replaced by a **payment request** that the Executive approves (owner-approved, see section 4) |

The 12 endpoints the Flutter app doesn't call yet were **kept**: the app will need them.

## 2. What was added

| Area | Added |
|---|---|
| **Platform** | Audit trail (who, when, old → new for every change, same transaction), outbox, stored notifications (AR/EN) |
| **Finance** | One payment flow for every payee: request → Executive → bank (NBE behind a provider interface, **mock** for now) → Paid → ledger. Insert-only ledger with reversals. Existing payments imported once |
| **Corrections** | Approved expenses and equipment costs are never edited: a correction is an adjustment the Executive approves (M6) |
| **Duplicates** | Unique supplier and subcontractor names / tax number; idempotency keys on payments (M9) |
| **HR** | Departments, employees, documents (data only), salary versions, monthly inputs (bonus, deduction, advance recovery), monthly salary sheet: HR prepares → Accountant approves → Finance pays. New role `hr` |
| **Procurement** | Requisition (auto-approval policy, on by default) → quotations (Executive selects) → purchase order (with revisions) → goods receipt → supplier invoice with 3-way match → supplier cost → payment. Committed cost on the project. New role `purchasing` |
| **Reports** | 12 reports with one calculation source, Excel (Arabic right-to-left), print pages, background exports with a notification |
| **Live updates (SignalR)** | Hub `/api/hubs/erp`: notifications and "this changed, refetch" signals, sent only after the data is saved; groups by user, project, project money, finance, HR, procurement |
| **Caching** | `HybridCache` for project cost figures, worker trades and the role matrix; cleared by the same change map as the live signals. Money checks before approving or paying are never cached |
| **Database backup** | Windows service: daily (02:00) or on demand, checksum, verified, compressed, uploaded files zipped too, keeps the newest N verified (default 3), Executive alerted on failure. Restore steps in the README |
| **Safety checks** | Every `/api` endpoint must have an authorization rule (test); permission re-checked in the database before every money step (M8) |
| **Packages** | ClosedXML 0.105.0 (MIT), `Microsoft.Extensions.Caching.Hybrid` 10.7.0; tests: SignalR client 10.0.9. Security: `System.Security.Cryptography.Pkcs` 9.0.21 pin, Testcontainers 4.16.0 |

## 3. What was refactored

- Shared building blocks (`BuildingBlocks`, new `BuildingBlocks.Data`) used by every module; modules still never
  reference each other, only `*.Contracts`.
- Status changes through `StatusFlow<TStatus>` on each record (M5).
- One database transaction across modules when a payment is settled (`SharedTransaction`).
- The project cost loader is the single source for the overview, the cost summary, the budget comparison and the
  `PRJ-COST` report, now also counting Materials committed by Procurement.
- Fixed on the way: a crash in the equipment cost and fuel reports, a catalog price query SQL couldn't run, a
  duplicate OpenAPI schema name.

## 4. New and changed endpoints, and the SignalR contract

**Changed (breaking, owner-approved):** these now answer **201 with a payment request** (status `Submitted`), not a
payment. The money is paid only after the Executive approves and the payment is confirmed.

| Endpoint | Before → now |
|---|---|
| `POST /api/projects/{p}/supplier-payments` | payment → request |
| `POST /api/workers/{id}/payments` | payment → request |
| `POST /api/projects/{p}/subcontracts/{c}/payments` | payment → request |
| `POST /api/projects/{p}/equipment-costs/{id}/payment` | 200 cost → 201 request |
| The four `…/void` endpoints | also write the ledger reversal |
| Same idempotency key again | returns the first request (200) |

**Changed (not breaking):** expense and equipment cost views add `adjustedAmount` and `adjustments[]`; supplier
endpoints also accept `procurement` rights; supply records add `sourceType` / `sourceId`.

**New endpoint groups** (full lists in each step's note):

| Group | Note |
|---|---|
| `/api/notifications…`, `/api/audit-entries` | `06` |
| `/api/payment-requests…` (approve, reject, approve-and-pay, send-to-bank, confirm-payment, bank-failure, cancel), `/api/ledger` | `08` |
| `…/expenses/{id}/adjustments`, `…/expense-adjustments/{id}/approve\|reject`, same for equipment costs | `09` |
| `/api/hr/…` (departments, employees, documents, salaries, payroll-inputs, salary-sheets, payment-request) | `11` |
| `/api/procurement/…` (policy, requisitions, quotation-requests, quotations, purchase-orders, receipts, invoices) | `13` |
| `/api/reports`, `/api/reports/{code}`, `/excel`, `/print`, `/api/report-exports`, `/file` | `15` |
| `/api/settings/backup` (GET, PUT), `/api/settings/backup/run`, `/api/backups` (Executive only) | `18` |

**SignalR** (details: `16-signalr-changes.md`):

| Item | Value |
|---|---|
| URL | `/api/hubs/erp`, with the same access token (`access_token` query string for WebSockets) |
| Server → client | `changed({ entityType, entityKey, version, action, aggregates[], projectId, scope })` = refetch; `notification({ id, type, entityType, entityKey, projectId })`; `resync({ reason })` |
| Client → server | `Join(scope) → bool`, `Leave(scope)` |
| Scopes | `user:{id}` (automatic), `project:{id}`, `project:{id}:money`, `company:finance`, `company:hr`, `company:procurement` |
| Rules | Signals carry no amounts or names; sent only after commit; on reconnect or `resync`, re-join and refetch |

## 5. Migrations created

Run in module order: **Platform, Access, Projects, Workforce, Subcontractors, Fleet, Archive, HR, Procurement,
Reports, Finance** (the API applies them at start-up in this order).

| Module | Migrations |
|---|---|
| Platform | `InitialPlatform`, `BackupSettingsAndHistory` |
| Access | `AddHrRole` (role 7, permissions 86–90), `AddPurchasingRole` (role 8, permissions 91–95) |
| Projects | `ExpenseAdjustments`, `SupplierUniqueness` (stops with a message if duplicates exist), `ExpenseSource` |
| Subcontractors | `SubcontractorUniqueness` (same duplicate check) |
| Fleet | `EquipmentCostAdjustments` |
| HR | `InitialHr` |
| Procurement | `InitialProcurement` (policy row seeded: auto-approval on, 0 % tolerance) |
| Reports | `InitialReports` |
| Finance | `InitialFinance`, `ImportExistingPayments` (copies existing payments into requests and the ledger, once), `SalaryPayments` |

**Before running on the real database:** take a backup; check for duplicate supplier / subcontractor names (the
migrations stop and say which); the server needs the .NET 10 runtime **10.0.13 or later** (security fix, `05` §3).

## 6. Tests added and results

| Project | Tests | New in this work |
|---|---|---|
| Api | 34 | endpoint authorization guard, architecture rules |
| Integration (new) | 7 | 5 business chains on SQL Server (Phase 8) + 2 high-risk tests (Phase 9) |
| Access | 180 | role and permission counts, re-check before money steps |
| Archive | 216 | — |
| Finance (new) | 57 | payment flow, ledger, import, permission removed mid-session |
| Fleet | 193 | equipment cost adjustments, payment requests |
| HR (new) | 99 | calculator, status flow, every endpoint's permissions, SQL Server flows |
| Platform (new) | 79 | audit, outbox, notifications, live updates, cache, backup |
| Procurement (new) | 92 | rules, flows, permissions, the full chain to a paid supplier cost |
| Projects | 638 | status methods, adjustments, duplicates, committed cost |
| Reports (new) | 24 | filters, totals, Excel, print, background exports, reconcile with the ledger |
| Subcontractors | 234 | uniqueness, payment requests |
| Workforce | 254 | payment requests |

The brief's high-risk list and where each item is proved: `20-high-risk-tests.md`.

**Result:** **2,107 of 2,107 tests pass**, the 13 projects run one after another (Docker has 4 GB).
The known flaky Archive request-size test passed in this run; its fix is open question 28.

## 7. Frontend contract changes (React and Flutter)

1. **Payments become requests.** The four "record payment" screens show the answer as a request (`number`,
   `status`) and refresh paid figures only when it is Paid. New screens: Payment requests (Executive queue =
   `status=Submitted`), request detail with buttons by status, Ledger. Every step sends `rowVersion`.
2. **Corrections:** show `adjustedAmount` next to `amount`, the adjustments list, "Ask for a correction" on approved
   records, Approve / Reject for the Executive.
3. **New roles and menus:** `hr` (HR menu: employees, payroll inputs, salary sheets) and `purchasing`
   (Procurement menu: requisitions, quotations, orders, receipts, invoices; Settings → Procurement policy).
4. **Reports menu:** catalog from `GET /api/reports`, one generic screen per report, Excel / Print; large exports
   answer 202 and arrive in "My exports" with a notification.
5. **Notifications:** bell from `GET /api/notifications/unread-count`; open a notification's record from
   `entityType` + `entityKey` + `projectId`.
6. **Live updates:** connect to `/api/hubs/erp`, join the scopes of the open screen, refetch on `changed`.
7. **Settings → Backup** (Executive): schedule, folder, how many to keep, "Run backup now", history.
8. **New error codes** to show in plain words: `own_record`, `wrong_method`, `not_payable`, `bank_refused`,
   `permission_revoked`, `payment_requested`, `adjustment_open`, `adjustment_below_paid`, `cost_being_paid`,
   `supplier_exists`, `supplier_tax_number_exists`, `subcontractor_exists`, `month_locked`, `sheet_exists`,
   `missing_salary`, `negative_net`, `empty_sheet`, `exceeds_requisition`, `below_received`,
   `price_already_invoiced`, `exceeds_ordered`, `exceeds_received`, `invoice_exists`, `quotations_pending`,
   `reason_required`, `backup_not_supported`.
9. Amounts are `null` when the caller lacks "See money" (unchanged rule, now also for HR and Procurement).

## 8. Open questions (Needs Confirmation)

**Not built yet — need your decision first**

1. **Weekly payroll batch** for daily workers (today: paid per worker, oldest day first). How is a week closed and paid?
2. **Company dashboards** (Executive, Accountant): not built; they must reuse the same calculation as the reports.
3. **Corrections of approved labor days and subcontract work** (M6 follow-up): who may correct a paid day?

**Money and approvals**

4. **Purchase order revisions are not re-approved**, even when Purchasing raises a price the Executive chose.
   Should a price increase go back to the Executive (always, or only above the price tolerance)? (`20` §3)
5. Payee bank account details (needed for the real NBE link) aren't stored anywhere yet.
6. May the Executive pay a request they created when no second approver exists? (today: no)
7. Should the bank confirm payments itself (callback) instead of the Accountant?
8. Should "payment request waiting for the Executive" also be a stored notification? (today: work inbox only)

**HR**

9. Engineers' salaries: charged to their projects, or company overhead (built: overhead)?
10. Partial months: full month (built) or prorated by days?
11. Leave, attendance deductions, employee overtime, social insurance, income tax: not built.
12. Paying an advance before payroll: not built (only its recovery).
13. Where employee document files live (built: data only).
14. Correcting a paid salary sheet: through next month's inputs?
15. Non-Egyptian employees (passport instead of the 14-digit national id)?
16. Worker national id stays optional?

**Procurement**

17. Over-delivery tolerance (built: none) and the price tolerance (built: 0 %).
18. Correcting a recorded goods receipt; attachments on procurement documents; cancelling an order after a partial
    delivery (built: revise down); may the invoice recorder also resolve its mismatch (built: yes); payment terms.

**Reports**

19. Money in (client income / certificates) isn't recorded, so cash movement shows 0 in.
20. Office expenses vs budget and fuel by driver: no data yet.
21. PDF: print pages use the browser's "Save as PDF" (a server PDF library needs a paid licence above US$1M revenue).
22. Week starts on Saturday: confirm.

**Platform, live updates, backup**

23. May the Accountant read the audit trail (financial records only)? (today: Executive only)
24. How long to keep handled outbox rows, read notifications and audit entries? (today: nothing is deleted)
25. GPS / geofence and "budget exceeded" alerts: no data or threshold rule yet.
26. Backup folder on the production server (a second disk is best) and the 02:00 time; the off-machine copy (NAS,
    external disk or cloud, and who checks it); a message when a backup succeeds?
27. Confirm the API, SQL Server and the backup folder are on the same Windows machine.

**Housekeeping**

28. The flaky Archive request-size test is fixed in commit `2c7a689` on another branch; bring it in (local
    cherry-pick) when you agree.
29. Every server needs the .NET 10 runtime 10.0.13 or later.
