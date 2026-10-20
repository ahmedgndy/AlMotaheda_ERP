# Central Price Control (ضبط الأسعار)

Builds on `08-cost-management.md` (the company price list, `PriceItem` / `PriceRange`, and the price check of a
supply). Nothing there changes: an approved range is never edited, a record keeps the comparison it was made with,
and a record is a cost only once someone else approves it. This document adds the control around it: configurable
categories and units, price rules, the **price exception** case a record above its range opens, the Executive's
three decisions, the gate on approval and payment, the Procurement path, notifications, reports and the offline rules.

## 1. The pieces

```
PriceCategory (code, names, active, order)      PriceUnit (code, names, active, order)
        │                                                │
        └── PriceItem (names, category, unit, status) ───┘
                 └── PriceRange  insert-only, Pending → Approved | Rejected, general or per supplier, from a date
                        kind: Range (min–max) | Fixed (reference ± tolerance %) | LumpSum (a total for one unit)
                        min/max are what every comparison uses; Fixed computes them from reference and tolerance

A record names a price item  ──►  PriceCheck.Compare(unit price, quantity, min, max)   (per unit, never totals)
        │                              result: NoReference | WithinRange | AboveRange | BelowRange | (UnitMismatch)
        │
        └── AboveRange ──► PriceException (one open case per record; UX_PriceException_Source_Pending)
                               Pending → ApprovedOnce | ApprovedWithPriceChange | Rejected | Withdrawn
                               Rejected → Withdrawn (the record was corrected)
                               + PriceExceptionAlert rows: one per person who hears of it
```

Sources of a case (`PriceExceptionSources`): `ProjectExpense` (a supply or expense recorded on a project, by hand
or from custody) and `SupplierInvoiceLine` (Procurement). A line without a price item is **not checked**: naming an
item is what puts a record under Price Control (the requisition form and the supply form offer the active items).

Variance (per case, kept as recorded): `VariancePerUnit = unit price − max`, `VarianceTotal = VariancePerUnit ×
quantity`, `VariancePercent = VariancePerUnit / max × 100` (2 decimals, away from zero). All money is `decimal`.

## 2. The policy (`Projects:PriceCheckPolicy`)

| Value | A record above its range |
|---|---|
| `Warn` | saved with its case; the case feeds the inbox and the reports; nothing is gated |
| `ExecutiveApproval` (**shipped default**) | saved with its case; **no one approves or pays the record** until the Executive approves the case; a supplier invoice is held unposted |
| `Block` | refused (`409 price_above_range`), nothing is saved |

The code's own default (no setting) is `Warn`, so existing tests and installs that never set the key keep their
behaviour; `appsettings.json` ships `ExecutiveApproval`.

## 3. Where the gate is enforced (backend only, every path)

| Path | Check |
|---|---|
| `POST/PUT /projects/{pid}/expenses` | compares the unit price (an expense with a price item: its amount, quantity 1); a unit that differs from the item's is `400 unit`; above the range needs `priceJustification`; saves the record **as entered** and opens the case in the same transaction; an edit withdraws the open case and compares again |
| `POST …/expenses/{id}/approve` | `409 price_exception_pending` / `409 price_exception_rejected` while the newest case is open — for the accountant **and** the Executive (the case is decided on its own screen); records from before cases existed keep `403 price_needs_executive` |
| `POST …/supplier-payments` (Projects) and Finance settlement (`SupplierPaymentSource`) | the same refusal; Finance's `IPaymentSource.SettleAsync` throws `PaymentNotPayableException` |
| Procurement `POST …/purchase-orders/{id}/invoices` | each line whose order line names a price item is compared at the invoice date with the supplier's range; `Block` → `409 price_above_range`; gated → invoice status **`PriceHold`**, `line.PriceCheck`/`PriceExceptionId` set, nothing posted, `priceReason` kept on the case |
| Procurement `POST …/supplier-invoices/{id}/resolve` | `Accept` is refused while a line's case is pending or rejected; a not-yet-compared line above its range holds the invoice instead of posting |
| Projects `IInvoicePosting.PostAsync` | belt and braces for every module: a line with a pending case is never posted; a rejected one is posted only at a price inside the range (a credit note to the order price), never at the rejected price |

The record is never rewritten by a decision; the catalog is never rewritten by a record.

## 4. The Executive's decisions (`/api/price-exceptions/{id}/…`, catalog Approve + Executive, never the submitter)

| | Endpoint | Effect |
|---|---|---|
| A | `approve` | `ApprovedOnce`: this transaction may be approved and paid; the reference is unchanged, the next purchase at this price opens a new case |
| B | `approve-with-price` | `ApprovedWithPriceChange` + a new **approved** `PriceRange` (general, or `forSupplier`) from `effectiveFrom` (default today), with kind/min/max/reference/tolerance/source note; `NewRangeId` links them; earlier records keep their comparison |
| C | `reject` | `Rejected`: the record stays blocked until its price is corrected (the edit withdraws the case) or the reviewer returns it |

Every decision needs a `note` (the audit reason) and the case's `rowVersion`; the case is locked
(`AlMotaheda.Projects.PriceException.{id}`) so two decisions land exactly one (`Two_decisions_on_the_same_case_at_once_land_exactly_one`).
Approval of the price **never** approves or pays the record. For an invoice line, Procurement's
`InvoicePriceExceptionSource` runs in the same transaction: once every held line is approved the invoice posts (or
goes to the mismatch queue when its prices also differ from the order); a rejection releases it to that queue.

## 5. Permissions (existing Access model, module `catalog`)

| Who | May |
|---|---|
| catalog View (engineers, accountants, managers) | see the price list, categories, units, their own cases and the cases of their projects; the money figures of a case need catalog SeeMoney |
| projects Create | record a supply with a price item and a justification (the engineer: the record stays theirs, as entered) |
| catalog Create | add/edit items, categories and units |
| catalog Edit | request a range change |
| catalog Approve **and** Executive role | decide range requests and price exceptions, update the reference price |
| accountant | approves/pays records only once the case is approved; cannot decide a case (`403`) |

## 6. Notifications and live updates

- `PriceExceptionAlert` rows (one per recipient: the project managers, the Executives and the accountants, never
  the submitter) → `PriceAboveRange` notification through the existing outbox after the commit.
- `PriceException` `ApprovedOnce`/`ApprovedWithPriceChange` → `RecordApproved` to the submitter; `Rejected` →
  `RecordRejected`; `Withdrawn` tells nobody.
- Work inbox: pending cases appear for catalog Approve callers (not their own) as record type `PriceException`.
- `LiveScopes`: `PriceException` → `PriceExceptions`, `PriceManagement`, `Expenses`, `SupplierInvoices` (+ Procurement, Finance);
  `PriceCategory`/`PriceUnit` → `PriceManagement`; `PriceExceptionAlert` is silent.

## 7. Reports (Reports module, permission catalog SeeMoney)

| Code | Rows |
|---|---|
| `PRICE-EXCEPTIONS` | every case (filters: from, to, project, supplier, status): date, project, item, category, supplier, unit, quantity, entered price, reference max, variance per unit / total / %, status, submitter, decider, record |
| `PRICE-CHANGES` | every approved range (from, to, supplier): effective date, item, category, supplier or "All suppliers", rule, min, max, reference, **origin** ("Price exception #n" = permanent change born from one transaction, or "Price list request"), requested by, approved by/on, reason |
| `PRICE-VARIANCE` | cases grouped by `groupBy` = Item (default), Project, Category, Supplier or Submitter: cases, variance total, pending, one-off approvals, price changes, rejected |

`GET /api/price-exceptions/summary` gives the same counts to the app's Reports tab.

## 8. Offline (Flutter)

- The supply form's item picker saves the active price list (items + today's general range) on the phone after
  every full fetch (`shared_preferences`, `price_catalog_snapshot`, with the fetch time). Offline, the picker shows
  that snapshot **with its time**, and the price check is computed on the phone with the server's arithmetic and
  labelled *provisional*. Nothing on the phone ever opens, approves or claims a decision: the server compares again at
  save, and the exception status shown on a record always comes from the server.
- Saves carry an idempotency key, so a retry after the catalog changed answers with the first record and its one case
  (`A_retry_after_the_reference_changed_…`); a new save is compared with the reference of its day.
- Queued (outbox) saves and the encrypted response cache live on the Flutter branch
  `feature/flutter-offline-ux-location-sync`; this checkout only snapshots the catalog.

## 9. Migrations

| Module | Migration | Change |
|---|---|---|
| Projects | `PriceControl` | `PriceCategory` and `PriceUnit` (seeded: the 11 classifications and 16 units the list knew, same codes, + Meals, Fuel, Internet, OfficeExpenses, Maintenance, Transport, EquipmentRental; WorkerDay, Meal, Month, Subscription, Hour, Km); `PriceItem.CategoryId` **filled from `Classification` by code** (unknown → Other), then `Classification` and its checks dropped; `PriceRange.Kind` (existing rows `Range`), `ReferencePrice`, `TolerancePercent`, `SourceNote`; `PriceException`, `PriceExceptionAlert`; hand FKs: supplier, submitter and recipient → `access.User` |
| Procurement | `PriceControlLinks` | `RequisitionLine.PriceItemId`, `PurchaseOrderLine.PriceItemId`, `SupplierInvoiceLine.PriceCheck`/`PriceExceptionId`, invoice status check + `PriceHold` |

No price, range or record is changed. Apply with the host (`dotnet run --project src/Host/AlMotaheda.Api` migrates
at startup) or `dotnet ef database update` per context. `Down` of `PriceControl` writes the classification back from
the category code and fails if an item uses one of the new categories.

## 10. Known limits

- Subcontractor measured work / manpower lines and equipment costs are not compared with the price list (the
  cost records are per contract rate; a later step can name a price item on them the same way).
- A purchase order is not held at order time: the check and the hold happen on the supplier invoice, which is the
  document the money follows. Requisition lines validate the item and its unit only.
- Evidence files for a catalog price are a free-text source note; the Archive stores documents per project, not per
  price item.
- Currency is EGP throughout (`PriceCurrencies`), as the rest of the money rules.
