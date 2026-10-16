# MUST items M5, M6, M9 — changes

Date: 2026-10-16. Refactor plan items, after Platform (M4, M7) and Finance (M1–M3, M8, M10).

## M5 — Status changes only through the record's own methods

| Record | Methods | Allowed moves |
|---|---|---|
| `ProjectExpense` | `Review`, `Resubmit` | Recorded → Approved / Returned / Rejected; Recorded or Returned → Recorded |
| `ItemPrice` (price change) | `Decide` | Pending → Approved / Rejected |
| `DailyReport` | `Review`, `Resubmit` | Submitted → Reviewed / Returned; Submitted or Returned → Submitted |
| `Budget` | `Approve`, `Supersede` | Draft → Approved → Superseded |
| `ReportRequest` | `MoveTo` | the moves of `ReportRequestFlow` |
| `DailySheet` (labor) | `Submit`, `Review` | Draft or Returned → Submitted → Approved / Returned / Rejected |
| `ProgressMeasurement`, `ManpowerSheet` | `Review` | Recorded → Approved / Rejected |
| `EquipmentCost` | `Review`, `Resubmit` | like expenses |
| New: `PaymentRequest`, `ExpenseAdjustment`, `EquipmentCostAdjustment` | see Finance and M6 | |

- `Status` has no public setter any more (`init` for the starting status only). A move not in the record's
  `StatusFlow` throws: it would be a bug, because the endpoints keep checking their business rules first and answer
  409 with the same codes as before.
- New shared helper: `BuildingBlocks/Domain/StatusFlow<TStatus>`.
- Master-data statuses (active / inactive suppliers, workers, items, equipment, projects) are plain edits and were
  left as they are.
- No API change, no schema change. Tests: `StatusMethodsTests` (Projects).

## M6 — Corrections of approved records (adjustments)

| | Expenses and supplies (Projects) | Equipment costs (Fleet) |
|---|---|---|
| Table | `projects.ExpenseAdjustment` | `fleet.EquipmentCostAdjustment` |
| Ask | `POST /api/projects/{p}/expenses/{id}/adjustments` `{ amount (signed, ≠ 0), reason }` — projects Create | `POST /api/projects/{p}/equipment-costs/{id}/adjustments` — equipment Create or Edit |
| Decide | `POST /api/projects/{p}/expense-adjustments/{id}/approve` / `reject` | `POST /api/projects/{p}/equipment-cost-adjustments/{id}/approve` / `reject` |
| Who decides | an Executive (re-checked in the database), never the requester | same |
| Floor | the corrected amount ≥ what is paid + what open payment requests hold (≥ 0) | ≥ 0; a credit cost can't be corrected once its payment is requested or made (paid once, in full — decision F2) |

- The approved record itself never changes. Its views now return `amount` (as recorded), `adjustedAmount` (amount
  plus approved adjustments) and `adjustments[]`. Every figure — recognized cost, paid, outstanding, payment state,
  supplier balances, items, reports, payment requests — uses the adjusted amount.
- One open adjustment per record (unique index). New error codes: `adjustment_open`, `adjustment_below_paid`,
  `cost_being_paid`.
- Notifications: the requester hears when their correction is approved or rejected.
- **Not built (follow-up, with reasons):** corrections of approved **labor days** and **subcontract work**. Both
  feed oldest-first payment allocation (worker FIFO, contract settlement), so a correction must also re-run those
  allocations; that needs its own design and owner decisions (who may correct a paid day?).

## M9 — Duplicate prevention

| Index | Table | Error code |
|---|---|---|
| `UX_Supplier_NameAr` (new) + `UX_Supplier_NameEn` | `projects.Supplier` | `supplier_exists` |
| `UX_Supplier_TaxNumber` (new, when given) | `projects.Supplier` | `supplier_tax_number_exists` |
| `UX_Subcontractor_NameAr` (new) + `UX_Subcontractor_NameEn` | `sub.Subcontractor` | `subcontractor_exists` |
| `UX_PaymentRequest_IdempotencyKey`, `UX_PaymentRequest_SourcePayment` | Finance (previous step) | `idempotency_key_reused` |

- Each migration first checks for existing duplicates and stops with a clear message (nothing changed) if there are
  any, so they can be merged before the index is created.
- Worker national id stays optional: **Needs Confirmation** (open question 3 had no proposal).
- HR employee national id and supplier invoice numbers get their unique indexes in the HR and Procurement steps.

## Migrations

| Module | Migration |
|---|---|
| Projects | `ExpenseAdjustments`, `SupplierUniqueness` |
| Fleet | `EquipmentCostAdjustments` |
| Subcontractors | `SubcontractorUniqueness` |

## Frontend contract changes

- Expense and equipment cost screens: show `adjustedAmount` next to `amount`, the list of `adjustments`, an
  "Ask for a correction" action on approved records, and Approve / Reject for the Executive.
- New error codes above.

## Tests

1,830 of 1,831 pass when the 9 projects run one after another: Api 33, Access 169, Archive 215/216, Finance 57,
Fleet 193, Platform 38, Projects 637, Subcontractors 234, Workforce 254. The one failure is the known flaky
`Other_endpoints_keep_the_server_default_request_size` (no database involved). New: `StatusMethodsTests`,
`ExpenseAdjustmentTests`, `EquipmentCostAdjustmentTests`, `SupplierDuplicateTests`.
