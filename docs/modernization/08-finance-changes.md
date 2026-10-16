# Finance step — payment requests, bank mock, ledger, import (refactor plan M1, M2, M3, M8, M10)

Date: 2026-10-16. Design: `07-finance-design.md`.

## 1. Added

| Added | Why |
|---|---|
| Module `AlMotaheda.Modules.Finance` (schema `finance`), last in `ModuleCatalog` | One payment flow for every payee; the ledger |
| `AlMotaheda.Modules.Finance.Contracts` | `IPaymentRequests` (source modules create, reserve, reverse) and `IPaymentSource` (Finance asks the source to write the paid payment) |
| `finance.PaymentRequest` (+ `Line`, `Event`) | The request, what it pays for, its approval history. Number `PAY-000123` from the id. Status changes only through the entity's methods (M5 for new code) |
| `finance.LedgerEntry` | Insert-only book of money out, per project; reversals point at the entry they cancel |
| `SharedTransaction` (BuildingBlocks.Data) | A request and the source module's rows commit or roll back together |
| `IPermissionCheck` (Access.Contracts) | Database re-check before every money step (M8): a removed permission or ended sign-in stops it at once |
| `IVisibleProjects` (Projects.Contracts) | Which projects a caller sees, for lists across projects |
| `IBankPaymentProvider` + `MockBankPaymentProvider` | The bank step; the mock answers `MOCK-<number>` (setting `Finance:Bank:Provider = Mock`) |
| `SupplierPaymentSource`, `WorkerPaymentSource`, `SubcontractPaymentSource`, `EquipmentCostPaymentSource` | Each module writes its payment row exactly as approved, re-checking its own rules under its own lock |
| `LegacyPaymentImport` + migration `ImportExistingPayments` | Every existing payment copied as an imported request (Paid, or Reversed when voided) with its ledger rows |
| Endpoints | `GET /api/payment-requests` (+ counts per status), `GET /api/payment-requests/{id}`, `POST …/{id}/approve`, `/reject`, `/approve-and-pay`, `/send-to-bank`, `/confirm-payment`, `/bank-failure`, `/cancel`, `GET /api/ledger` (with totals of the filtered set) |
| Test project `AlMotaheda.Modules.Finance.Tests` | 57 tests: rules, ledger posting, schema, query translation, API security, and real SQL Server flows (bank flow + ledger + void reversal; reject / cancel / own request; permission removed mid-session; import run twice) |

## 2. Changed (owner-approved breaking change)

| Endpoint | Before | Now |
|---|---|---|
| `POST /api/projects/{p}/supplier-payments` | 201 with the payment | **201 with the payment request** (`Location: /api/payment-requests/{id}`, status `Submitted`). Same body. Amounts held by open requests count as taken (409 `over_allocation`) |
| `POST /api/workers/{id}/payments` | 201 with the payment | 201 with the request; its lines are the planned days (only the caller's visible projects shown). 409 `over_payment` counts held days |
| `POST /api/projects/{p}/subcontracts/{c}/payments` | 201 with the payment | 201 with the request; open requests count against the contract value (409 `exceeds_contract_value`) |
| `POST /api/projects/{p}/equipment-costs/{id}/payment` | 200 with the cost, now paid | 201 with the request; 409 `payment_requested` while one waits. The idempotency key is made from the cost and its row version |
| The four `…/void` endpoints | Void the payment | Same, and also write the ledger reversal and mark the request Reversed, in the same transaction |
| Same idempotency key | Returned the first payment | Returns the first request (200); a different body with the key is still 409 `idempotency_key_reused` |

Paid amounts, balances, outstanding, item costs and reports read the payment rows as before: a request is not
money paid until it is confirmed.

## 3. Migrations

| Migration | Contents |
|---|---|
| `Finance/InitialFinance` | 4 tables, 9 check constraints, unique indexes on number, idempotency key, paid source payment, reversal |
| `Finance/ImportExistingPayments` | Data only: copies existing payments into requests and the ledger. `Down` removes only imported rows |

**Order:** Platform, Access, Projects, Workforce, Subcontractors, Fleet, Archive, **Finance last**.

## 4. Tests

1,815 of 1,816 pass when the 9 projects run one after another: Api 33, Access 169, Archive 215/216, Finance 57,
Fleet 189, Platform 38, Projects 627, Subcontractors 234, Workforce 254. The one failure is the known flaky
`Other_endpoints_keep_the_server_default_request_size` (broken pipe while sending 31 MB; no database involved).

Existing tests changed because the behaviour changed as approved: the payment helpers now request, approve and
confirm (`ProjectsApi.SettleAsync`); the idempotency tests expect a request; new tests check that open requests
hold their money (suppliers, workers, equipment).

## 5. Frontend contract changes (React and Flutter must apply)

1. The four "record payment" screens now **request** a payment. Show the answer as a request (`number`,
   `status`), not as a payment, and refresh the payment lists only when the request is Paid.
2. New screens: **Payment requests** (list with `countsByStatus`; the Executive's queue is `status=Submitted`),
   **Request detail** (lines, history, buttons by status and permission), **Ledger** (`finance` See money).
3. Buttons and who sees them: Approve / Reject / Approve & pay → `payments` Approve; Send to bank / Confirm
   payment / Record bank failure → `finance` Edit; Cancel → the creator or an Executive. Every step sends the
   request's `rowVersion`.
4. New error codes: `own_record` (the creator can't decide), `wrong_method`, `not_payable`, `bank_refused`,
   `permission_revoked` (sign in again), `payment_requested`.
5. Amounts in requests are `null` without `payments` See money (the Manager's view).

## 6. Rules applied (labels)

| Rule | Label |
|---|---|
| Request → Executive (reject / approve / approve & pay) → bank → Paid → ledger | Company (brief) |
| Approval can't be bypassed by editing: requests are never edited | Company (brief: changing amount/beneficiary after approval invalidates it) |
| Cash "approve & pay" is paid at once | Owner accepted proposal 4 |
| Single-entry ledger with account tags | Owner accepted proposal 1 |
| The creator of a request can't decide it | Industry recommendation (with one Executive, the Accountant creates requests) |
| Reference on the payment row = the requester's reference; the bank confirmation reference stays on the request | Industry recommendation |

## 7. Open questions (Needs Confirmation)

1. Payee bank account details (needed by the real NBE integration) are not stored anywhere yet.
2. May the Executive pay a request they created when no second approver exists? Today: no (cancel, and the
   Accountant requests it).
3. Should the bank confirm payments itself (callback) instead of the Accountant? Today a person confirms.
