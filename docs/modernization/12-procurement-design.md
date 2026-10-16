# Procurement — design (brief Phase 3B)

Date: 2026-10-16. Labels: **[Company]** from the brief · **[Industry]** recommendation · **[Confirm]** Needs Confirmation.
Permissions: `03-permission-matrix.md` §3 (procurement).

## 1. The document chain

```
PR-000001  Purchase requisition (project, lines: catalog item + cost item + quantity, estimate = current item price)
   │  approval step: Auto-Approval Policy ON  → "Approved by: System (Auto-Approval Policy)"      [Company]
   │                 policy OFF → Project Manager, then Accountant (never the requester)          [Company]
   ▼
RFQ-000001 Quotation set — only when the Executive asks for quotations (note, how many wanted)    [Company]
   │  Purchasing adds quotations (supplier, unit prices, delivery days, payment terms, validity),
   │  may recommend one with a reason, submits the set; the Executive selects one (reason required
   │  if not the cheapest) or returns it with a note                                               [Company]
   ▼
PO-000001  Purchase order — from an approved PR; supplier and prices from the selected quotation when
   │       quotations were asked for, otherwise chosen by Purchasing. PO price = committed price.  [Company]
   │       Changes = a revision (number + reason + snapshot of the previous lines).               [Company]
   ▼
GRN-000001 Goods receipt — recorded on site by a project member with site Create; partial deliveries;
   │       never more than ordered (tolerance 0)                                    [Company / Industry]
   ▼
INV-000001 Supplier invoice — recorded by the Accountant (finance Create) against one PO          [Company]
   │       3-way match per line: ordered | received | invoiced qty, PO price | invoice price      [Company]
   │       Invoicing more than received is refused outright                                      [Company]
   │       Price outside tolerance (default 0) → Mismatch, blocked; Accountant (finance Approve)
   │       resolves: accept with reason / return to supplier / credit note                       [Company]
   ▼
Posted     Matched (or resolved) invoice lines become approved supplier records ("supply, on credit") in the
           project's cost breakdown, on their cost item — once, by a unique link to the invoice line  [Company]
   ▼
PAY-000001 Paid through the existing supplier-payment request → Executive → bank → Paid → ledger   [Company]
```

Every document: number series from its id, link to the previous document, project, status with explicit
moves (`StatusFlow`), created by/at, decision history, audit trail (Platform). The chain endpoint
`GET /api/procurement/requisitions/{id}/chain` returns PR → RFQ → POs → GRNs → invoices → posted supplier records.
**Attachments**: not built in this step (the Archive module is per project document type) **[Confirm]** — see §7.

## 2. Statuses

| Document | Moves |
|---|---|
| PR | Submitted → (ManagerApproved →) Approved → Ordered; Submitted/ManagerApproved → Rejected; Submitted/ManagerApproved/Approved → Cancelled |
| RFQ | Open → Submitted → Selected; Submitted → Returned → Submitted |
| PO | Issued → PartiallyReceived → Received; Issued → Cancelled (only with nothing received) |
| GRN | Recorded (final; a wrong receipt is corrected by the Accountant's invoice review, **[Confirm]** reversal) |
| Invoice | Matched → Posted; Mismatch → Posted (accepted / credit note) or Returned (to supplier) |

A PR becomes **Ordered** when its approved quantity is fully on POs. A PR with an open RFQ can't get a PO until the
Executive selects a quotation.

## 3. Rules

| Rule | Label |
|---|---|
| Auto-approval is a stored policy (`AutoApproveRequisitions`, default **on**), changed only by the Executive, audit-logged; the PR still records an approval step | Company |
| With the policy off: the project's manager approves first, then an Accountant (procurement Approve); nobody approves their own PR | Company |
| PR estimate = the project's current approved catalog price; PO price is fixed when issued; history never recalculated | Company |
| A PO line can't exceed what the PR line still needs (approved − already ordered) | Industry |
| A revision can't bring a line below what was received; revision needs a reason; previous lines kept as a snapshot | Company / Industry |
| Receipt ≤ ordered − already received (no over-delivery) | Industry, tolerance **[Confirm]** |
| Invoice qty ≤ received − already invoiced (on active invoices) | Company |
| Price tolerance %, default 0, stored with the policy (Executive) | Company default, value **[Confirm]** |
| Supplier invoice number unique per supplier (`UX_SupplierInvoice_Supplier_Number`) | Industry (M9) |
| Posting creates one approved credit supply record per invoice line (credit-note resolution posts at the PO price) | Company (no double counting) / Industry |
| Committed cost = Σ (PO qty − invoiced qty) × PO price over Issued/PartiallyReceived/Received POs, shown in the project's Materials group as **Committed**, apart from actual cost | Company |
| Purchasing sees every procurement document; managers and engineers only their projects' | Company |

## 4. Who does what

| Action | Needs |
|---|---|
| Create PR | procurement C + can see the project (member, or company-wide role) |
| Approve / reject PR (policy off) | procurement A; step 1 = project manager of that project, step 2 = Accountant role |
| Ask for quotations, select, return | Executive (role, re-checked in the database) |
| Add quotations, recommend, submit set, create / revise / cancel PO | procurement E |
| Record GRN | site C + member of the project (or Executive) — Purchasing can't |
| Record invoice / resolve mismatch | finance C / finance A (Accountant) |
| Pay | existing `POST /api/projects/{p}/supplier-payments` (payments C) → Finance |
| Change the policy | Executive (role, re-checked in the database) |
| Suppliers (list / create / edit) | projects V/C/E **or** procurement V/E |

Prices and amounts in procurement views need procurement See money (`F`); otherwise `null`.

## 5. Data (schema `proc`)

`ProcurementPolicy` (one row), `Requisition` + `RequisitionLine` + `RequisitionEvent`, `QuotationRequest` +
`Quotation` + `QuotationLine`, `PurchaseOrder` + `PurchaseOrderLine` + `PurchaseOrderRevision`, `GoodsReceipt` +
`GoodsReceiptLine`, `SupplierInvoice` + `SupplierInvoiceLine`. Supplier, catalog items, cost items and supply
records stay in Projects and are reached through `Projects.Contracts` (no second supplier table).

## 6. Changes to existing modules

- **Access**: role `purchasing` (id 8), permission module `procurement` (ids 91–95), defaults per the matrix.
- **Projects**: `purchasing` sees all projects (data scope only; it has no `projects` permission); supplier
  endpoints accept procurement permissions; new contract `IProcurementPosting` creates the approved supply records
  of a posted invoice in the shared transaction; `ProjectExpense` gets `SourceType` / `SourceId` (unique) so a
  posted line can't be posted twice and can't be edited as an ordinary expense; the Materials cost group accepts
  committed cost from Procurement.
- **BuildingBlocks**: `RequireAnyPermission` (one of several module/action pairs).

## 7. Needs Confirmation

1. Receipt tolerance (over-delivery) and the price tolerance value (built: 0 %, configurable).
2. Correcting a wrong GRN after it is recorded (built: no; ask the Accountant to return the invoice).
3. Attachments on procurement documents (built: no; files would need a company-wide archive area).
4. Cancelling a PO after partial delivery (built: no; revise the remaining quantities to what was received).
5. Payment terms from the selected quotation are stored for reference only; due dates are not enforced.
