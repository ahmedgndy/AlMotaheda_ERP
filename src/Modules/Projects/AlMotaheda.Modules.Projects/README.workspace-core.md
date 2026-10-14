# Projects module: workspace part 1 (suppliers, project items, expenses and supplies, supplier payments)

Text to merge into `README.md`. Design: `docs/project-workspace/01-projects-core.md`.

## What it does

- **Suppliers:** one company-wide list (materials, equipment, services, fuel, other). An inactive supplier keeps
  its history on every project but can't be chosen for new records.
- **Expenses and supplies (`ProjectExpense`):** a supplier delivery (`Supply`: item, unit, quantity, unit price)
  or a site expense (`Expense`: category, description, amount). Saved as `Recorded`; a cost of the project only
  once someone else approves it. The server computes a supply's amount (quantity × unit price, to the piastre) and
  copies the approved catalog price of the day when a catalog item is named. The client's amount is never trusted.
- **Workflow:** `Recorded` → `Approved` (frozen; only the item link can still change), `Returned` (the recorder
  fixes and resubmits; back to `Recorded`) or `Rejected` (final). The recorder never reviews their own record.
- **Payment channel:** `Custody` (paid on site from petty cash: paid when approved) or `Credit` (a supplier
  invoice: paid only through supplier payment allocations). Buying on credit needs a supplier.
- **Project items ("بنود", `ProjectItem`):** the pieces of work of a project that collect cost. An item holds no
  amount: its figures are sums of the approved records linked to it, from three sources that never overlap:
  supplier records (this module), subcontract work (`ISubcontractItemCosts`) and internal labor
  (`ILaborItemCosts`). A supplier-side record (a supply, anything from a supplier, or anything linked by hand) that
  has no item is in the "not linked to an item yet" group. A site expense with no supplier and no link is an
  "other project cost": in the project total, on no items screen.
- **Supplier payments (`SupplierPayment` + `SupplierPaymentAllocation`):** money paid to a supplier for one
  project, allocated to the approved credit records it settles. Never edited or deleted: a wrong payment is voided
  and ignored by every calculation. Idempotent by a client key.
- **What is never stored:** paid, outstanding, totals. Every figure is calculated from the source records by the
  pure classes `ExpenseSettlement`, `ItemsLedger`, `ExpenseSummary`, `SupplierBalances` and `PaymentAllocations`,
  which the screens and the overview share (decision G5), so two screens can't disagree.

## Who may do what

| Action | Who | Checked how |
|---|---|---|
| See and search suppliers | projects View | `ListSuppliers` |
| Add / edit a supplier | projects Create / Edit | `CreateSupplier`, `UpdateSupplier` |
| See a project's records | projects View, in scope. Without projects See money: only records you recorded, and no summary | `ListExpenses`, `GetExpense` (an outsider's record answers 404) |
| Record a supply or expense | projects Create, in scope, project not completed | `CreateExpense` |
| Fix and resubmit | the recorder only, while Recorded or Returned | `ExpenseRules.Resubmit` |
| Approve / return / reject | projects Approve, not the recorder, only from Recorded; under the project lock | `ExpenseRules.Review`, `ExpenseReview` |
| Link a record to an item, move it, unlink it | projects Edit **or** Approve, in scope, item active and on the same project, record not rejected | `LinkExpenseItem` |
| See the items table and details | projects See money, in scope | `ListProjectItems`, `GetProjectItem`, `GetUnallocatedItems` |
| Add / edit / deactivate / reactivate / delete an item | projects Edit, and an Executive or the project's manager; project not completed | `ItemRules.ManageProblem` |
| Record a supplier payment | payments Create, in scope, project not completed; under the project lock | `RecordSupplierPayment` |
| Void a payment | payments Approve (Executive by default), reason required | `VoidSupplierPayment` |
| See payments and supplier balances | projects See money, in scope | `ListSupplierPayments`, `GetSupplierBalances` |

A completed project answers 409 `project_completed` to every write, except from an Executive. A project outside
the caller's scope answers 404 everywhere, the same as a project that doesn't exist.

## Endpoints

| Endpoint | Who may call |
|---|---|
| `GET /api/suppliers` | projects View. `search` (names, phone), `status`, `kind`, `page`, `pageSize` |
| `POST /api/suppliers` | projects Create. 409 `supplier_exists` on the same English name |
| `PUT /api/suppliers/{id}` | projects Edit. `rowVersion` |
| `GET /api/projects/{pid}/items` | projects See money. `search` (names), `includeInactive`. Returns `items`, `totals` (all items, whatever the search), `unallocated`, `reconciliation` (`itemsTotalCost + unallocatedTotalCost + otherCosts = projectRecognizedCost`) |
| `GET /api/projects/{pid}/items/{itemId}` | projects See money. `item`, `supplierLines`, `subcontractLines`, `laborLines`, `pendingSupplierLines`, `totals` |
| `GET /api/projects/{pid}/items/unallocated` | projects See money. Same shape, `item` is null |
| `POST /api/projects/{pid}/items` | projects Edit, Executive or the project's manager. 409 `item_name_taken` |
| `PUT /api/projects/{pid}/items/{itemId}` | same. `rowVersion` |
| `POST /api/projects/{pid}/items/{itemId}/deactivate`, `…/reactivate` | same. `rowVersion`; 409 `invalid_status` when already so |
| `DELETE /api/projects/{pid}/items/{itemId}` | same. 409 `item_has_records` when any record anywhere is linked |
| `GET /api/projects/{pid}/expenses` | projects View. Filters `kind`, `status`, `channel`, `supplierId`, `itemId`, `unlinked=true`, `paymentState`, `from`, `to`, `search` (item name, description, invoice no., delivery note no., supplier name), `sort` (`date` newest first, `amount`, `status`), paging. `summary` for the whole project (See money only) |
| `GET /api/projects/{pid}/expenses/{id}` | projects View. With its payments |
| `POST /api/projects/{pid}/expenses` | projects Create. 201, saved as Recorded |
| `PUT /api/projects/{pid}/expenses/{id}` | projects Create, the recorder. Fix and resubmit; `rowVersion` |
| `POST /api/projects/{pid}/expenses/{id}/approve` | projects Approve. `rowVersion`, optional `note` |
| `POST /api/projects/{pid}/expenses/{id}/return`, `…/reject` | projects Approve. `note` required, `rowVersion` |
| `PUT /api/projects/{pid}/expenses/{id}/item` | projects Edit or Approve. `{ itemId, rowVersion }`; `itemId: null` removes the link |
| `GET /api/projects/{pid}/supplier-payments` | projects See money. `supplierId`, `includeVoided`, paging |
| `POST /api/projects/{pid}/supplier-payments` | payments Create. 201, or 200 with the first payment on an idempotent repeat |
| `POST /api/projects/{pid}/supplier-payments/{id}/void` | payments Approve. `{ reason, rowVersion }` |
| `GET /api/projects/{pid}/supplier-balances` | projects See money. Per supplier: `creditRecognized`, `custodyRecognized`, `paid`, `outstanding`, `openInvoices` |

Dates are `yyyy-MM-dd` and can't be in the future (company time zone). Enums go by name, any letter case.
`paymentState` (`Unpaid`, `PartiallyPaid`, `Paid`) exists only on approved records; it is `null` before.

### Supplier payment rules (one transaction, under the project lock)

1. At least one allocation, no record twice, every amount above zero, the sum equal to `amount`.
2. Every record: this project (else 404), this supplier (else 400 on `allocations`), approved and on credit
   (else 409 `invalid_status`).
3. Already allocated (payments not voided) + this allocation ≤ the record's amount, else 409 `over_allocation`.
4. The same `idempotencyKey` again returns the first payment (200) and records nothing; the same key on another
   project answers 409 `idempotency_key_reused`. A race on the key is caught by the unique index and answered
   the same way.

## Errors

| Status | Code | When |
|---|---|---|
| 400 | `validation_failed` | Field errors, all together. Unknown or inactive supplier, catalog item or project item of another project are reported on their field |
| 403 | `forbidden` | Missing permission; not the recorder (resubmit); not an Executive or the project's manager (items); neither Edit nor Approve (link) |
| 404 | `not_found` | Project outside your scope or missing; record, item or payment not on this project; a record you may not see without See money |
| 409 | `own_record` | Reviewing a record you recorded |
| 409 | `invalid_status` | Reviewing a record that isn't Recorded; editing one that isn't Recorded or Returned; linking a rejected record; paying a record that isn't approved credit; deactivating an inactive item |
| 409 | `item_inactive` | Linking a new record to an inactive item |
| 409 | `item_name_taken`, `item_has_records`, `supplier_exists` | Rule conflicts |
| 409 | `over_allocation`, `idempotency_key_reused`, `payment_already_voided` | Payment rule conflicts |
| 409 | `project_completed`, `concurrency_conflict` | Completed project (non-Executive); stale `rowVersion` |
| 503 | `busy` | The project lock was held for more than 10 seconds; retry |

## Tables (schema `projects`)

- `Supplier`, `ProjectItem`, `CatalogItem`, `ItemPrice`, `ProjectExpense`: created by the foundation (see
  `01-projects-core.md` for the columns and checks).
- `SupplierPayment`: project, supplier, date, amount (= sum of allocations), method (`BankTransfer`, `Cheque`,
  `Cash`), reference, notes, `IdempotencyKey` (unique `UX_SupplierPayment_IdempotencyKey`), void details (set
  together, `CK_SupplierPayment_Void`), created by, row version. Indexes on (project, date) and (project, supplier).
- `SupplierPaymentAllocation`: payment, expense, amount > 0. Unique (payment, expense); index on expense.
- No foreign key cascades. No cross-schema key is added by this part.

## Code layout

- `Features/Suppliers`: `ListSuppliers`, `CreateSupplier`, `UpdateSupplier`, `SupplierFields` (form, view, queries).
- `Features/Expenses`: `ListExpenses`, `GetExpense`, `CreateExpense`, `UpdateExpense`, `ReviewExpense` (approve,
  return, reject), `LinkExpenseItem`; `ExpenseFields` (form + reference checks), `ExpenseQueries`, `ExpenseViews`,
  `ExpenseRules` (workflow), `ExpenseSettlement` (recognized / paid / outstanding, `ExpenseSummary`).
- `Features/Items`: `ListProjectItems`, `GetProjectItem` (+ `GetUnallocatedItems`, `ItemDetails`),
  `CreateProjectItem`, `UpdateProjectItem`, `SetProjectItemStatus`, `DeleteProjectItem`; `ItemsLedger` (pure),
  `ItemsLedgerSource` (loads the flat rows and asks Workforce and Subcontractors), `ItemViews`, `ItemFields`;
  `IProjectItemsSummary` (the ledger for the overview and the reports).
- `Features/SupplierPayments`: `ListSupplierPayments`, `RecordSupplierPayment`, `VoidSupplierPayment`,
  `GetSupplierBalances`; `PaymentAllocations` and `SupplierBalances` (pure), `PaymentViews`.
- `Common/CoreProblems.cs`, `Common/ProjectsLog.Core.cs` (event ids 2401–2432), `Data/SupplierPayment.cs`,
  `Data/Configurations/SupplierPaymentConfiguration.cs`, `Data/ProjectsDbContext.Core.cs`.

## Known limits (accepted)

- The items table loads every item and every record of the project and computes in memory (decision G5). Its
  `search` and `includeInactive` filter those loaded rows; there is no paging, as in the prototype. If a project
  grows very large, the next step is SQL aggregation of the fully paid rows, not a stored total.
- Project-level counts of workers and subcontractors are sums of the per-item counts the other modules report; a
  worker on two items counts twice at project level. Suppliers are counted once.
- Deleting an item checks the three "has links" sources and then deletes, without one lock across modules. The
  foreign keys are the last guard: a record linked in between makes the delete fail with the same 409.
- A supplier payment can name an inactive supplier: it settles old invoices, it doesn't create a new purchase.
- A caller without projects See money sees the amounts of the records they recorded themselves (they typed them),
  but no summary and nobody else's records.
