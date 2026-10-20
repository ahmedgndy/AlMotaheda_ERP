# Project financial workspace: engagements, suppliers, custody and weekly pay

Builds on `00-overview.md` (money rules 3.1 to 3.8) and `08-cost-management.md` (main items, sub-items, the item
breakdown and price checks). Nothing here changes those rules: a record is a cost only once someone else approves
it, a payment settles a debt and is never a second cost, and every screen reads the same rows through the same pure
calculation classes.

## 1. How the records relate

```
Project ─┬─ Main work item ─┬─ Sub-item
         │                  │
         │   every cost record points to ONE item (main or sub), of the same project (composite keys / checks)
         │
         ├─ Subcontract (engagement) ── Subcontractor company      one per company, project and MAIN work item
         │     ├─ value + approved Variations = effective value     (ContractVariation, decided by someone else)
         │     ├─ contract items → main item or one of its sub-items
         │     ├─ measured work / manpower (approved = cost, owed)
         │     └─ payments (Finance request → paid) with allocations to approved work
         │
         ├─ Supply agreement ── Supplier company                   one per supplier, project, work item, material
         │     └─ supplies recorded under it take its work item and unit
         ├─ Supplies / site expenses (ProjectExpense)             channel Credit (owed) or Custody (paid in cash)
         │     └─ supplier payments (Finance) with allocations to approved credit records
         ├─ Purchase orders (Procurement)                          commitments: ordered / received / invoiced
         │
         ├─ Daily labor sheets (Workforce) ─ lines per worker ─ allocations per work item (portions)
         │     └─ worker payments: Finance requests, or a weekly PayrollSettlement paid in cash from custody
         │
         └─ Cash custody per holder (CustodyEntry)
               fundings (Finance request paid) · returns · payroll outflows · + purchases/expenses on Custody
```

The company account of a supplier or a subcontractor is never stored: it is the sum of its project rows, read on
request, filtered to the projects the caller may see.

## 2. The formulas (implemented as pure classes)

| | Figure | Formula | Where |
|---|---|---|---|
| A | Work-item actual cost | Σ approved supplies and expenses (supplier, direct-from-custody) + approved subcontract work + approved labor portions + approved equipment costs on the item; a main item shows its own + its sub-items' | `ItemsLedger`, `ItemTree.WithSubItems`, labor/subcontract/equipment contracts (`IEquipmentItemCosts`) |
| B | Subcontractor outstanding | approved work − allocations of non-voided payments (retention per contract) | `ContractAccount.Compute` |
| C | Remaining subcontract | (signed value + Σ approved variations) − executed (approved) work | `ContractAccount`, `ContractValues.EffectiveAsync` |
| D | Supplier outstanding | approved credit records (adjusted by approved corrections) − allocations of non-voided payments; custody purchases are paid when approved | `SupplierFigures.Of`, `ExpenseSettlement` |
| E | Labor outstanding | approved line nets − allocations of non-voided worker payments; days held by pending settlements and open payment requests are shown apart ("being paid") | `PaymentFifo`, `SettlementPlan.Payables` |
| F | Custody balance | Σ posted fundings − Σ returns − Σ custody purchases/expenses not rejected − Σ payroll outflows not released | `CustodyBalance.Compute` |
| G | Remaining budget | approved budget lines of the item (and its sub-items) − actual cost (A); commitments (contract not executed, orders not invoiced) shown apart | `ItemPlanFigures.For` |

Four figures are never mixed on any screen: **commitment** (ordered or contracted, not cost), **cost** (approved),
**owed** (approved, not paid) and **pending** (recorded, not reviewed).

### How double counting is prevented

- **Payment ≠ cost.** Payments only reduce "owed"; cost comes from approved records. A weekly settlement writes
  worker payments against labor already counted; the labor cost does not change (test
  `A_weekly_cash_settlement_pays_the_labor_once_…`).
- **Machinery once.** An equipment cost names one work item or none. On an item it is in that item's cost (and the
  items total); on none it is a cost of the whole site, in the items table's "other costs" and never in "not linked
  yet". The overview's Equipment group is all of them; items total + not linked + other costs = the project's
  recognized cost whether or not a cost is linked (`ListProjectItems.Reconcile`, test
  `A_rental_on_the_excavation_sub_item_costs_that_sub_item_and_earthworks_once_…`).
- **Funding ≠ cost.** A custody funding is money moved to the holder (Finance ledger: money out, purpose
  `CustodyAdvance`). The cost is the purchase recorded on custody, once (test `A_sand_purchase_of_200_…`).
- **One source per payment.** Supplier, subcontract and worker payments each allocate to records of one module; a
  company account sums the projects' rows, so a payment allocated to project A appears once, in project A.
- **Same days never paid twice.** A settlement holds its days (allocations) while pending; a Finance worker-payment
  request subtracts those holds, a settlement subtracts open requests' reservations, and a confirm re-checks under
  the project and worker locks (`settlement_not_payable` if paid since). Concurrent settlements of the same week:
  exactly one is created (test `Two_settlements_of_the_same_week_…`).
- **Idempotency.** Funding requests, returns, settlements, custody purchases and payments carry client keys (unique
  indexes); a repeat answers with the first record.

## 3. Endpoints

### Cash custody (Projects; permission module `custody`)

| Endpoint | Who | Notes |
|---|---|---|
| `GET /api/projects/{pid}/custody` | custody View | own custody only, unless custody Approve/See money or projects See money (manager, accountant, Executive) |
| `GET /api/projects/{pid}/custody/{holderUserId}` | same | statement with running balance; someone else's custody is 404 for a holder |
| `POST /api/projects/{pid}/custody/fundings` | custody Create | creates a Finance payment request (Executive approves, then it is paid); for someone else needs custody Approve; holder must be an active member |
| `POST /api/projects/{pid}/custody/returns` | custody Approve | never the holder; ≤ balance; idempotent |

Purchases and expenses paid from custody are the existing `POST /projects/{pid}/expenses` with `channel=Custody`;
a supply bought from a shop with no supplier record sends `payeeName` instead of `supplierId`. Policy
`Projects:CustodyBalanceCheck`: `Warn` (default; saved, balance goes negative) or `Block` (409
`custody_insufficient`, checked under the custody lock). The CostRecorded alert of a custody record goes to the
project managers, the Executives and the accountants.

### Suppliers (Projects)

| Endpoint | Who |
|---|---|
| `GET /api/projects/{pid}/supply-agreements` | projects View (prices only with See money) |
| `POST/PUT /api/projects/{pid}/supply-agreements[/{id}]`, `POST …/{id}/close` | projects Edit (manager or Executive) or procurement Edit |
| `GET /api/projects/{pid}/suppliers` | projects See money: per supplier figures, agreements, order lines (Procurement), per work item, per material |
| `GET /api/suppliers/{id}/account` | projects See money: across the visible projects, with the chronological ledger |

A supply under an agreement must match its supplier and unit and goes to the agreement's work item; a closed
agreement takes no new supply (409 `supply_agreement_closed`).

### Subcontractors

| Endpoint | Who |
|---|---|
| `POST /api/projects/{pid}/subcontracts` with `projectItemId` | subcontractors Create; must be an active **main** item (409 `contract_item_not_main`) |
| `POST /api/projects/{pid}/subcontracts/{cid}/variations` | subcontractors Edit; one pending at a time (409 `variation_pending`) |
| `POST /api/projects/{pid}/subcontract-variations/{id}/approve|reject` | subcontractors Approve, never the requester; not below executed/paid (409 `variation_below_executed`) |
| `GET /api/subcontractors/{id}/account` | subcontractors See money: engagements, per project, ledger |

Contract items of an engagement may only point to its main item or that item's sub-items (409
`item_outside_contract_scope`).

### Weekly labor settlements (Workforce)

| Endpoint | Who |
|---|---|
| `GET /api/projects/{pid}/labor/payable?from&to` | workers View + (workers See money or custody Create) |
| `GET /api/projects/{pid}/labor/settlements[/{id}]` | same; a holder without workers See money sees only the settlements they recorded or paid |
| `POST /api/projects/{pid}/labor/settlements` | workers Create + (workers See money or custody Create); a holder records only from their own custody |
| `POST …/settlements/{id}/confirm|reject|reverse` | custody Approve (accountant, Executive); never the recorder nor the holder |

Flow: the engineer pays the week in cash → records the settlement (status Pending): the days are held, the cash is
out of the custody (payroll entry Pending), alerts go to the project managers, the Executives and the accountants →
the accountant confirms: one `WorkerPayment` per worker with the planned allocations (`SettlementId` set), the
custody entry is posted → labor paid/outstanding, the item breakdown and the worker accounts update. Reject frees the
days and the cash; reverse voids the payments and releases the custody entry. A settlement's payment cannot be voided
on its own (409 `settlement_payment`).

## 4. Notifications

Persisted through the existing outbox after the commit (`NotificationRules`), deduplicated per recipient and type:

| Event | Recipients |
|---|---|
| Custody purchase or expense recorded (`CostRecordedAlert`) | project managers, Executives, accountants (never the recorder) |
| Settlement recorded (`SettlementAlert` rows) | project managers, Executives, accountants (never the recorder) |
| Settlement confirmed / rejected / reversed | its recorder and the custody holder |
| Custody funding paid, return, payroll by someone else (`CustodyEntry` added) | the holder |
| Variation approved / rejected | its requester |

Live updates: `LiveScopes` routes `CustodyEntry`, `PayrollSettlement`, `SupplyAgreement` and `ContractVariation` to
the project's money group and Finance/Procurement; the app refreshes the custody, suppliers, labor and items screens.

## 5. Migrations (all additive)

| Module | Migration | Change |
|---|---|---|
| Projects | `CustodyAndSupply` | `CustodyEntry`, `SupplyAgreement`; `ProjectExpense.PayeeName`, `SupplyAgreementId`; record check widened (custody purchase may name a payee); hand FK holder → `access.User` |
| Subcontractors | `EngagementsAndVariations` | `SubContract.ProjectItemId` (hand FK → `projects.ProjectItem`); unique index per company, project and item (NULL = old contracts, unchanged); `ContractVariation` (hand FK → `projects.Project`) |
| Workforce | `PayrollSettlements` | settlements, lines, allocations, alerts; `WorkerPayment.SettlementId`; hand FKs to project and users |
| Finance | `CustodyFunding` | allowed values: payee `CustodyHolder`, purpose `CustodyAdvance`, source `CustodyFunding` |
| Archive | `CustodyAndSettlementLinks` | documents may link custody entries, supply agreements, settlements, variations |
| Fleet | `EquipmentCostWorkItem` | `EquipmentCost.ProjectItemId` (NULL = whole site, as every existing cost); index `IX_EquipmentCost_Project_Item`; hand composite FK `(ProjectId, ProjectItemId)` → `projects.ProjectItem (ProjectId, Id)` |

No existing row is changed or removed. Each `Down` documents when it would fail (new data of the new kinds).
