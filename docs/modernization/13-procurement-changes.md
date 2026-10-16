# Procurement step — requisitions to paid supplier cost (brief Phase 3B)

Date: 2026-10-16. Design: `12-procurement-design.md`. Permissions: `03-permission-matrix.md`.

## 1. Added

| Added | Why |
|---|---|
| Module `AlMotaheda.Modules.Procurement` (schema `proc`), between HR and Finance in `ModuleCatalog` | The full document cycle the brief asks for |
| `AlMotaheda.Modules.Procurement.Contracts` with `IInvoicePosting` (implemented by Projects) | A matched invoice becomes the project's supplier cost, once, in the same transaction |
| Role `purchasing` (id 8), permission module `procurement` (ids 91–95) | Executive VCEAF, Manager VCAF, Engineer VC, Accountant VAF, Purchasing VCEF |
| `ProcurementPolicy` (one row): `AutoApproveRequisitions` (default **on**), `PriceTolerancePercent` (default **0**) | Auto-approval is a policy: the PR still records an approval step, by "System (Auto-Approval Policy)" |
| Documents with number series: `PR-`, `RFQ-`, `PO-`, `GRN-`, `INV-` (`PAY-` stays in Finance) | Each links to the previous one; `GET …/requisitions/{id}/chain` shows the whole trail |
| `CommittedCostSource` | Open PO value not invoiced yet appears as **Committed** in the project's Materials group |
| `IProjectCatalog` (Projects.Contracts) | The PR estimate is the project's current approved catalog price |
| `RequireAnyPermission` (BuildingBlocks) | Suppliers are managed with `projects` or `procurement` rights |
| Notifications | The requester hears when a PR is approved / rejected by people; Purchasing hears when its quotation set is selected / returned |

## 2. Endpoints (all under `/api/procurement`)

| Endpoint | Needs | Notes |
|---|---|---|
| `GET /policy`, `PUT /policy` `{ autoApproveRequisitions, priceTolerancePercent }` | V / Executive (DB re-check) | Change is in the audit trail and the log |
| `POST /requisitions` `{ projectId, neededBy?, notes?, lines: [{ catalogItemId?, projectItemId?, description?, unit?, quantity }] }` | C + sees the project | Auto-approved under the policy |
| `GET /requisitions`, `GET /requisitions/{id}`, `GET /requisitions/{id}/chain` | V | Lists filtered to the caller's projects (Purchasing, Accountant, Executive: all) |
| `POST /requisitions/{id}/approve` `{ note? }`, `/reject` `{ reason }` | A | Step 1 the project's manager, step 2 an Accountant; 409 `own_record`, `not_project_manager`, `accountant_step` |
| `POST /requisitions/{id}/cancel` `{ reason }` | requester, Purchasing (E) or Executive | Only before anything is ordered |
| `POST /requisitions/{id}/quotation-request` `{ quotationsWanted?, note? }` | Executive | Approved PR, nothing ordered, once per PR |
| `GET /quotation-requests/{id}` | V | Side by side, `cheapestQuotationId` |
| `POST /quotation-requests/{id}/quotations`, `DELETE /quotations/{id}`, `POST …/recommend`, `POST …/submit` | E (Purchasing) | |
| `POST /quotation-requests/{id}/select` `{ quotationId, reason? }`, `/return` `{ note }` | Executive | 400 `reason_required` when not the cheapest |
| `POST /purchase-orders` `{ requisitionId, supplierId?, lines: [{ requisitionLineId, quantity, unitPrice? }] }` | E | From the selected quotation when one was asked for (409 `quotations_pending` before); 409 `exceeds_requisition` |
| `POST /purchase-orders/{id}/revise` `{ reason, lines: [{ lineId, quantity, unitPrice }] }`, `/cancel` `{ reason }` | E | Numbered revision keeps the old lines; 409 `below_received`, `price_already_invoiced`, `has_receipts` |
| `GET /purchase-orders`, `GET /purchase-orders/{id}` | V | Received, invoiced, still due per line; committed |
| `POST /purchase-orders/{id}/receipts` `{ receivedOn, deliveryNoteNo?, lines: [{ orderLineId, quantity }] }` | site C + project team | 409 `exceeds_ordered`; 403 `not_project_member` |
| `GET /goods-receipts/{id}` | V | |
| `POST /purchase-orders/{id}/invoices` `{ invoiceNo, invoiceDate, lines: [{ orderLineId, quantity, unitPrice }] }` | finance C | 409 `exceeds_received`, `invoice_exists`; Matched → Posted at once, else Mismatch |
| `POST /supplier-invoices/{id}/resolve` `{ resolution: Accept\|CreditNote\|ReturnToSupplier, reason, creditNoteNo? }` | finance A | |
| `GET /supplier-invoices` (status=Mismatch is the review queue), `GET /supplier-invoices/{id}` | V | Posted record ids, paid amount, `paymentState` |

**Changed:** `GET/POST /api/suppliers`, `PUT /api/suppliers/{id}` also accept procurement View / Edit. Supply records
gain `sourceType` / `sourceId` in their views (a posted invoice line).

**Paying an invoice:** the existing `POST /api/projects/{p}/supplier-payments` on the posted records → Executive →
bank → Paid → ledger. No second payment path.

## 3. Migrations

| Module | Migration |
|---|---|
| Procurement | `InitialProcurement` (14 tables, the policy row seeded on) |
| Access | `AddPurchasingRole` (role 8, permissions 91–95, 18 role defaults) |
| Projects | `ExpenseSource` (`SourceType`, `SourceId`, unique when set) |

**Order:** Platform, Access, Projects, Workforce, Subcontractors, Fleet, Archive, HR, **Procurement**, Finance.

## 4. Tests

New project `AlMotaheda.Modules.Procurement.Tests` (92): rules (tolerance, quantities, committed), status flows
(policy approval recorded as the system, two-step human approval, order and invoice moves), schema, every
endpoint's 401/403, Executive-only steps, validation, and two SQL Server flows: (1) PR auto-approved →
quotations → selection with reason → PO from the quotation → partial receipts → over-invoicing refused → matched
invoice posted → price mismatch resolved by credit note → committed back to 0 → paid through the supplier payment
request; (2) policy off: manager then Accountant, own requisition refused, PO revision and cancellation, visibility.
Access, Projects and Api tests updated (8 roles, 95 permissions, migrations, module order, Materials committed).

## 5. Frontend contract changes

1. New role `purchasing`; new permission module `procurement`; new menu **Procurement** (Requisitions,
   Quotations, Purchase orders, Receipts, Supplier invoices) with `procurement` View; Settings → Procurement policy
   for the Executive.
2. Requisition history shows system steps with `actorLabel` and `policyName` instead of a person.
3. Supplier screens open to Purchasing. Project overview: Materials now has a `committed` figure.
4. The invoice screen links each line to its supply record; pay from the existing supplier payment screen.

## 6. Open questions (Needs Confirmation)

1. Over-delivery tolerance (built: none) and the price tolerance value (built: 0 %, Executive can change it).
2. Correcting a recorded GRN (built: not possible; the invoice review is the control).
3. Attachments on procurement documents (built: no).
4. Cancelling an order after a partial delivery (built: revise the rest down instead).
5. May the person who records an invoice also resolve its mismatch? (built: yes, both are the Accountant's job.)
6. Quotation payment terms are kept for reference; due dates are not enforced.

## 7. Results

2,034 of 2,034 tests pass when the 11 projects run one after another: Api 33, Access 180, Archive 216, Finance 57,
Fleet 193, HR 99, Platform 38, Procurement 92, Projects 638, Subcontractors 234, Workforce 254.
Fixed on the way: a catalog-price query that couldn't be translated to SQL, the project cost loader ignoring a
Materials source, and a duplicate OpenAPI schema name (`SupplierView`, now `OrderingSupplier` in Procurement).
