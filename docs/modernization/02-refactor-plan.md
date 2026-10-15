# Refactor and extension plan

Date: 2026-10-16. Inputs: `00-current-state.md`, `01-coverage-matrix.md`, the brief.

Labels on every business rule: **[Company]** = stated in the brief or the existing design docs ·
**[Industry]** = common practice, recommended · **[Confirm]** = Needs Confirmation, not built until answered.

## 1. Ground rules for the work

- Keep the existing style: modular monolith, one file per use case, Minimal APIs, hand-written validation,
  `ApiProblem` codes, pure static calculation classes, `…Queries` methods tested with `ToQueryString()`.
  No MediatR and no FluentValidation. The architecture doc rejects both, and adding them now would mean rewriting
  ~150 working endpoints for no gain.
- New modules follow `docs/architecture.md` §8. Each has its own schema, `DbContext`, migrations and
  `.Contracts` project, and only when another module needs it.
- Every change that alters an existing endpoint's behaviour is listed in section 6 and needs the owner's OK.
- New NuGet packages, each with a reason: `Microsoft.Extensions.Caching.Hybrid` (Phase 6, already named in
  architecture.md), `ClosedXML` (Excel, MIT licence), and a PDF library (see [Confirm] 9).
  SignalR is part of ASP.NET Core and needs no package.

## 2. New modules

| Module | Schema | Owns | Talks to |
|---|---|---|---|
| **Finance** | `finance` | Payment requests (one flow for every payee), Executive decision, bank provider (`IBankPaymentProvider` + `MockBankPaymentProvider`), bank confirmation, ledger entries, reconciliation | Calls each source module through a new contract `IPaymentSettlement` (Projects, Workforce, Subcontractors, Fleet, Hr, Procurement) to check what is payable and to write the settlement when paid |
| **Hr** | `hr` | Employees (link to `access.User` optional), job, department, hiring date, status, employee documents metadata (files via Archive storage), salary structure (versioned), bonuses / deductions / advances, monthly salary sheet | Finance (salary payment request) |
| **Procurement** | `proc` | PR, RFQ + quotations, PO (with revisions), GRN (partial), supplier invoice, 3-way match, number series, policy settings | `projects.Supplier` through `Projects.Contracts`; Finance; Projects for recognized cost |
| **Reporting** | `reports` | Report catalog, export jobs, export files metadata, export audit | Every module's read contracts. Dashboards and reports use the same calculation classes |
| **Platform** | `platform` | Audit log, persisted notifications, outbox dispatcher, SignalR hub and group authorization, settings (backup, procurement policy), backup service and history | Every module (writes audit + outbox rows in their own transaction; see M7) |

`Supplier` stays in Projects (the brief: one supplier table). Purchasing gets the right to manage it (section
4 of the permission matrix). Supplier ≠ Subcontractor, as today.

## 3. MUST (financial correctness and security)

| # | Item | What changes | Rule label |
|---|---|---|---|
| M1 | **One payment flow through Finance** | `PaymentRequest` (status `Draft → Submitted → Approved → SentToBank → Paid` / `Rejected` / `Failed` / `Cancelled`, transitions below) with payee type, payee id, project, purpose, lines pointing at the records it settles, idempotency key, rowversion. The Executive does reject / approve / approve & pay. A bank confirmation (mock provider now) moves it to Paid. On Paid, in **one transaction**: the source module writes its existing allocation rows (`SupplierPaymentAllocation`, `WorkerPaymentAllocation`, `SubPaymentAllocation`, equipment paid columns), and Finance writes ledger entries. Approved requests are frozen; changing amount, payee, project or purpose is impossible after approval, and any edit before payment sends the request back to Draft (approval cleared). | [Company] flow from the brief. Approval invalidation [Company]. |
| M2 | **Ledger** | `LedgerEntry` (insert-only): date, amount (+/-), account (Cash/Bank, Supplier, Worker, Subcontractor, Employee, Equipment supplier…), project, cost item, source document type + id, payment request id, created by/at. Reversal = a new entry with the opposite sign and a link to the original. Every money report reconciles to it. | [Industry] double-entry is **not** proposed (no chart of accounts exists) — single-entry with account tags. [Confirm] 1 |
| M3 | **Existing payment data** | Backfill: every non-voided historical payment becomes a `PaymentRequest` with status `Paid`, origin `Imported`, plus its ledger entries. Voided payments become a pair (entry + reversal). Nothing is deleted. One migration, run once, with a reconciliation check (sum before = sum after). | [Industry] |
| M4 | **Audit log table** | `platform.AuditEntry`: who, when, action, entity type/id, old values, new values (JSON, money and status fields only; never passwords or files), trace id. Written **in the same transaction** as the change, through the outbox table of the module's own `DbContext` (M7). The structured log events stay. | [Company] Phase 8 |
| M5 | **Status changes through entity methods** | `Status { get; private set; }`. Each workflow entity gets methods (`Approve`, `Return`, `Reject`, `Resubmit`, `Void`…) that call the existing `*Rules` decision and throw nothing: they return the same decision enum the slice already maps to 409. Done for every entity touched by money first: `ProjectExpense`, `DailySheet`, `ProgressMeasurement`, `ManpowerSheet`, `EquipmentCost`, `Budget`, `ItemPrice`, and all new entities. | [Company] brief "Domain" |
| M6 | **Correction of approved records** | Approved records stay frozen. A correction is an `Adjustment` record (amount ±, reason, link to the original) that goes through the same approval. Reports add adjustments to the original. | [Company] "corrections through reversal/adjustment". Who may request/approve: **[Confirm] 2** (default proposal: recorder requests, Executive approves) |
| M7 | **Outbox + integration events** | Each module's `DbContext` gets an `OutboxMessage` table in its schema. Slices add events (`ExpenseApproved`, `PaymentRequestPaid`, `LedgerEntryCreated`…) before `SaveChanges`. A single hosted dispatcher in Platform reads every module's outbox, writes notifications + audit, invalidates cache and pushes SignalR signals, then marks the row done. At-least-once; handlers are idempotent by message id. | [Industry] brief Phase 5 |
| M8 | **Database re-check for money actions** | Payment request create/decide, bank confirmation, record approvals with money, and HR salary actions re-check "user active and still holds this permission" in the database (new `IPermissionCheck` in `Access.Contracts`, same idea as `IExecutiveCheck`). Closes the 15-minute stale-token window. | [Industry] |
| M9 | **Duplicate prevention** | Unique (filtered) indexes: `Supplier.NameAr`, `Supplier.TaxNumber` (when present), `Subcontractor.NameAr`, `Subcontractor.TaxNumber` if present, HR `Employee.NationalId`, procurement document numbers, `PaymentRequest.IdempotencyKey`, `SupplierInvoice (SupplierId, InvoiceNo)`. Before each index: a script lists existing duplicates in the **test** DB copy; the owner decides how to merge. Worker national id required for new workers: **[Confirm] 3**. | [Company] brief "Data" |
| M10 | **Payment request immutability under concurrency** | Approve / pay / confirm run in a transaction with an app lock per payment request and per payee, plus rowversion; the "already paid" check is repeated under the lock; unique index on (source record, active request) prevents two open requests for the same record. | [Company] |

### Payment request transitions (M1)

| From | Action | To | Who |
|---|---|---|---|
| Draft | submit | Submitted | creator (`payments:C`) |
| Draft / Submitted | edit amount, payee, project, purpose, lines | Draft (approval cleared) | creator |
| Submitted | reject (note required) | Rejected (final) | Executive (`payments:A`) |
| Submitted | return (note required) | Draft | Executive |
| Submitted | approve | Approved | Executive |
| Submitted | approve & pay | SentToBank | Executive |
| Approved | send to bank | SentToBank | Accountant (`finance:E`) |
| SentToBank | bank confirms | Paid → ledger | bank callback / Accountant with reference (`finance:E`) |
| SentToBank | bank fails | Failed → can be re-sent or cancelled | bank / Accountant |
| Draft / Submitted / Approved / Failed | cancel (note) | Cancelled | creator or Executive |

Paid and Rejected are final. Cash payments (no bank): **[Confirm] 4** — proposal: "approve & pay" with method
Cash moves straight to Paid on the Executive's decision, with the cash receipt reference.

## 4. SHOULD (quality, consistency)

| # | Item | What changes |
|---|---|---|
| S1 | **Remove duplicated infrastructure** | `AppLock` (×4) + `ProjectLock`, `SqlList` (×4), `SqlViolations` / `RowVersions` / `DataHelpers` (×6) and the shared parts of `Validation` (×6) move into one new project `AlMotaheda.BuildingBlocks.Data` (references EF Core relational only). Module-specific parts stay. Lock names keep their exact strings so behaviour doesn't change. |
| S2 | **Vulnerable dependency** | Pin `System.Security.Cryptography.Pkcs` to the patched version in `Directory.Packages.props` (transitive pinning). |
| S3 | **Permission oddities to confirm and fix** | `UpdateCatalogItem` needs catalog **Create**, not Edit. Report request `cancel` needs only `reports:V` (the handler then limits it to the requester — check). Both are probably deliberate; confirm and comment, or fix. |
| S4 | **Inbox and notifications side by side** | Keep `GET /inbox` ("work waiting for me", computed). Add persisted notifications ("something happened"). Different questions; no duplication. |
| S5 | **Equipment cost payment** | After M1 the equipment cost's paid columns are written only by Finance on Paid; the direct pay/void endpoints become internal settlement. |
| S6 | **Docs** | `architecture.md` §1 and §2 are out of date (say Archive is "an empty shell", list 3 modules). Update with every phase. |
| S7 | **Every list paginated** | Already true for lists; check the few `pageSize: 100` "all" calls the app makes and the new modules. |

## 5. LATER

| # | Item | Why later |
|---|---|---|
| L1 | Redis backplane / Azure SignalR, Redis as HybridCache L2 | Needed only with two or more API servers |
| L2 | OpenTelemetry tracing and metrics | architecture.md already plans it; not in the brief |
| L3 | Push notifications (FCM/APNs) | Needs credentials; the persisted notifications are the source of truth |
| L4 | Real NBE bank integration | Behind `IBankPaymentProvider`; mock now |
| L5 | Off-machine backup copy | The brief says recommend, not implement |
| L6 | Screens for the "Unused" endpoints (coverage matrix C) | Frontend work |

## 6. Existing endpoints whose behaviour changes (owner OK needed)

| Endpoint | Today | After |
|---|---|---|
| `POST /projects/{p}/supplier-payments` | Records a paid payment at once | Creates a payment **request** (Draft or Submitted). Paid only after Executive + bank |
| `POST /workers/{id}/payments` | Same | Same as above; weekly batches add a second way to create a request |
| `POST /projects/{p}/subcontracts/{c}/payments` | Same | Same as above |
| `POST /projects/{p}/equipment-costs/{id}/payment` | Same | Same as above |
| `…/void` on the four payment types | Voids a payment | Kept for imported (historical) payments. New payments are corrected by a ledger reversal + new request |
| Supplier create/update | `projects:C/E` | `projects:C/E` **or** `procurement:C/E` |
| `GET /me` | Permissions of 17 modules | Adds the new modules (`hr`, `procurement`) and the role codes `hr`, `purchasing` |

Alternative if the owner wants no breaking change: keep the old endpoints as they are and add the request flow
next to them. **Not recommended**: two ways to pay means the control in M1 can be bypassed.

## 7. Order of work (after review)

1. S1, S2 (no behaviour change, small, makes everything after easier). Full Phase 2 file-by-file review runs
   with it; dead code found is listed with the reason before removal.
2. Platform: audit log, outbox, notifications tables (M4, M7).
3. Finance: payment requests, bank mock, ledger, backfill (M1–M3, M10), then switch the four payment endpoints.
4. M5, M6, M8, M9.
5. HR (Phase 3), then Procurement (Phase 3B).
6. Reporting (Phase 4), SignalR (Phase 5), caching (Phase 6), backup (Phase 7).
7. Phase 8 integration checks and Phase 9 tests run with every step, not only at the end.

## 7a. Needs Confirmation (numbered, referenced above)

1. Single-entry ledger with account tags is enough (no chart of accounts, no double entry)?
2. Who may request and who approves a correction (adjustment) of an approved record?
3. Must every new worker have a national id (prevents duplicates; some daily workers may not give one)?
4. Cash payments: does "approve & pay" with Cash go straight to Paid?
5. Weekly payroll: which day does the week start? Does the batch replace per-worker payments, or do both stay?
6. Salaries: are engineers' salaries charged to project cost (by assignment) or company overhead only?
7. HR: leave policies, attendance-based deductions, employee overtime — all out until defined (per the brief).
8. Procurement: price and quantity tolerance for the 3-way match (default 0 per the brief).
9. PDF: QuestPDF (free only under its community licence terms: company revenue below USD 1M) or a print-ready
   HTML page that the browser prints to PDF (no package, no licence)? Recommendation: print-ready HTML.
10. Office expenses, fuel per vehicle/driver, money in (client certificates), custody funds: build them now
    (the Phase 4 reports need them) or leave those reports out until the data exists?
11. Budget approval by its own author (open question 11 of `07-decisions.md`).
