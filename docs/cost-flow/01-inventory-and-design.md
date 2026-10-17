# Cost-flow integrity: inventory and design

Date: 2026-10-17. Branch `feature/backend-cost-flow-integrity`, from `main` 96ceae9 (= `integration/e2e-qa`).

Labels: **[Company]** a rule from the owner or this brief · **[Industry]** a common practice we chose ·
**[Confirm]** needs the owner's decision (listed in `02-results.md`).

Sources checked against the code: `docs/architecture.md`, `docs/project-workspace/00…07`, `docs/modernization/00…21`,
the module READMEs, the migrations and the tests. The React prototype the workspace docs cite
(`alsaleh-prototype66.html`) is **not on this machine**; the only prototype found
(`~/Downloads/alsaleh-prototype.html`) covers the office, fuel, vehicles and accounts screens, not the project
workspace. The workspace docs (written from prototype66) are the workflow reference. No ERD file was found; the
docs quote it.

## 1. Inventory (verified in the code, not from earlier reports)

| Capability | State before this branch | Where |
|---|---|---|
| Projects, members, project scope | Implemented | Projects |
| Project items ("بنود"), link of supplies / labor / subcontract work, "not linked yet", item detail | Implemented (suppliers, labor, subcontract work only) | Projects `ItemsLedger` |
| Price catalog | **Partial**: one catalog **per project**; no company master catalog | Projects `CatalogItem`, `ItemPrice` |
| Price-change request → decision, history, effective dates | Implemented per project; decided by the **Executive role only** | `RequestPriceChange`, `PriceDecision` |
| Historical price kept on the transaction | Implemented (`ProjectExpense.UnitPrice`, `CatalogPriceAtRecording`) | Projects |
| Price-variance review | **Missing** for direct purchases (Procurement has a 3-way match against the order price) | — |
| Financial Manager role | **Missing** (8 roles) | Access |
| Composite items / bill of materials | **Missing** | — |
| Purchases / supplies / expenses with review, credit vs custody, item link | Implemented | Projects `ProjectExpense` |
| Procurement chain PR → RFQ → PO → GRN → invoice → posted supply | Implemented | Procurement |
| Supplier register | Implemented (names, kind, phone, tax no., status) | Projects |
| Supplier history across projects | **Partial**: balances per project only; no company-wide supplier profile | `GetSupplierBalances` |
| Supplier documents | **Missing** (Archive links can't point at a supplier) | Archive |
| Subcontractors (contracts, measured work, manpower, retention, advances, payments) | Implemented | Subcontractors |
| Daily labor sheets (workers, days, overtime, allowances, deductions with reasons, split over items, submit, review, payments) | Implemented | Workforce |
| Employee salaries, monthly salary sheet, payment | Implemented | HR |
| Engineer / employee salary charged to projects | **Missing** (salaries are company overhead; no project days) | — |
| Equipment register, rentals, assignment history, costs (rental, fuel, maintenance, repair, transport, other), payments, voids, corrections | Implemented | Fleet |
| Equipment cost on a project item | **Missing** (decision F1: not split over items) | — |
| Responsible engineer, spare parts, repair requests | **Missing** | — |
| Payments: request → Executive → bank → paid → ledger; voids keep history | Implemented | Finance + sources |
| Audit trail, outbox, stored notifications, SignalR, push (FCM) | Implemented; push needs the owner's Firebase credentials (not configured: **blocked**) | Platform |
| "Waiting for your review" notifications to reviewers | **Missing** (only the work inbox; notifications go to a record's own people) | Platform |
| One calculation source for overview, items, reports | Implemented (`ProjectCostLoader`, `ItemsLedger`) | Projects |

## 2. Design of the missing parts

### 2.1 Financial Manager role [Company: brief §4.3] (Access)

New role `financial_manager` (id 9), appended like `hr` and `purchasing`. It sees every project (company-wide scope,
like the Accountant) and decides price questions; it **does not** approve payments (`payments` Approve stays with
the Executive [Company]) and has no access to users, roles or salaries.

| Module | Defaults |
|---|---|
| projects, subcontractors, workers, equipment, fleet, site | V F |
| **catalog** | **V A F** (decides price changes and price variances) |
| payments, finance | V F |
| procurement | V F |
| reports | V C |

The Accountant gets `catalog` **E** (to request price changes; until now only an Executive or a Manager could)
[Company: brief]. Price decisions now need `catalog` Approve **re-checked in the database**, instead of the
Executive role. The Executive keeps every permission [Company].

### 2.2 Master catalog and project selection (Projects, permission module `catalog`)

- `MasterItem`: company-wide definition: names, **kind** (`Material`, `Service`, `Work`, `Composite`, `Labor`,
  `Equipment`), category, unit, description, status. `MasterItemPrice`: controlled price rows with the same
  request → decision flow and effective dates as project prices.
- Management area: `/api/catalog/items…` and `/api/catalog/price-changes…`, never inside a project's overview.
- A project **selects** master items: `POST /api/projects/{p}/catalog-selection { masterItemIds }`.
  `Work`/`Composite` → a project item (بند); the other kinds → a project catalog item (priced). Each keeps a
  `MasterItemId` link; the same master item is selected once per project (unique), selecting again changes nothing.
  The master catalog is never copied: no master price rows are copied.
- **Reference price** of a linked project catalog item on a date = the project's own approved price on that date
  when the project has one (a negotiated project price), otherwise the master's approved price on that date
  [Industry, Confirm]. Supplies keep copying the reference price they found (`CatalogPriceAtRecording`), so a later
  master change never rewrites history [Company].
- The first price of a new master item is a pending request, unless the creator may decide prices (then approved
  at once). A master item's unit can't change once a project uses it [Industry].

### 2.3 Price variance review (Projects)

- `PricingPolicy` (one row): `VarianceTolerancePercent` (seed **0**) and `RoundingAllowance` per unit (seed
  **0.01 EGP**) [Confirm values]. Changed by the Executive only, audited.
- A supply with a catalog item and a reference price whose unit price differs by more than
  `max(reference × tolerance %, rounding allowance)` creates a `PriceVariance` (reference, actual, quantity, unit and
  total variance, percent, direction **Above** or **Below**) [Industry: both directions are flagged].
- While its variance is **Open** or **Rejected**, the supply can't be **approved** (409 `price_variance_open`).
  Returning or rejecting the supply stays possible. Editing the supply closes the old variance (`Superseded`) and
  checks again.
- `catalog` Approve (Financial Manager, Executive), never the supply's recorder, **accepts** or **rejects** it
  with a reason. Accepting approves the price **for this transaction only**: the catalog price never changes.
- Wording: "price variance requiring review"; no blame [Company].
- Procurement invoices keep their own control (order price from the Executive's quotation choice + 3-way match);
  they are not checked again against the catalog [Industry, Confirm].

### 2.4 Composite items (Projects)

- `ProjectItemComponent`: an item's planned components: a **material** (a project catalog item), **labor**,
  **equipment** or **subcontract work**, with optional `PlannedQuantity` and `QuantityPerUnit`.
- Costs stay on the source records, linked to the item as today. The item detail breaks the item's own cost down by
  component: a supply counts in the component of its catalog item, labor in the labor component, and so on; what
  matches no component is shown as "other". **Components + other = the item's cost**, and the project total is the
  sum of source records, so nothing is counted twice [Company: brief §5.1].
- Equipment costs can now be linked to an item (one item per cost, like supplies) [Company: brief Test E; replaces
  decision F1]. Engineer and employee salary allocations too (2.6).

### 2.5 Supplier profile (Projects + Archive)

- Optional profile fields: legal name, contact person, email, address, commercial registration no. Nothing new is
  required [Company].
- `GET /api/suppliers/{id}` and `GET /api/suppliers/{id}/history`: per project the caller may see: recognized,
  paid, outstanding (the same calculation as the project balances), deliveries by item and unit, payments, open
  price variances. Amounts only with `projects` See money.
- Archive: a project document can be linked to a supplier; `GET /api/suppliers/{id}/documents` lists them from the
  caller's projects. One stored file, many links: no copy [Company].

### 2.6 Salary cost allocation (HR)

- `CostAllocationPolicy`: `WorkingDaysPerMonth`, **not set** by default; allocation is refused until the Executive
  sets it [Company: brief §11, no universal formula].
- `ProjectWorkDays`: an employee's working days on a project in a month (halves allowed), optional project item;
  recorded by HR (`hr` C), approved by `hr` A (the Accountant), never by its recorder. An approved entry is changed
  by recording a new one, which supersedes it when approved. Days of one employee in a month can't exceed the
  policy's working days.
- `POST /api/hr/salary-allocations/run { month }` (`hr` A + F): for each approved entry, amount =
  (basic + fixed allowance in force on the month's first day) ÷ working days × days, to the piastre. Idempotent: an
  unchanged entry is skipped; a changed or superseded one reverses its old allocation (kept, with who and why) and
  writes the new one.
- The project sees it as cost group **Staff** (new) and, when an item is given, on that item. Paid = the allocation
  once the month's salary sheet is Paid; the salary payment itself stays company money in the ledger, so it is never
  counted twice [Company].

### 2.7 Fleet

- `Equipment.ResponsibleUserId` (optional): the engineer or operator in charge.
- Cost type `SpareParts`.
- A new cost identical to an existing one (same machine, date, type, amount and invoice no., not rejected) is
  refused: 409 `duplicate_cost` [Industry].
- `RepairRequest`: machine, project, requester, problem, priority, status `Open → Approved / Rejected`,
  `Approved → Completed` (with the result and, optionally, the repair cost record) or `Cancelled`. Decided with
  `equipment` Approve (the existing approvers of equipment costs), never by the requester [Confirm]. A request is
  never a cost: the cost is its own record with its own approval and payment [Company].

### 2.8 Review notifications (Platform)

- When a record is submitted for review, its reviewers get a stored notification (and a live signal): users who
  hold the reviewing permission **and** may see the project, never the person who submitted it.
- Records: supplies and expenses (`projects` A), equipment costs and repair requests (`equipment` A), daily labor
  sheets (`workers` A), price changes and price variances (`catalog` A), salary sheets and project days (`hr` A).
- Titles carry no amounts or names [Company].
