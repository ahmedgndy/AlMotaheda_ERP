# Finance — design (refactor plan M1, M2, M3, M8, M10)

Date: 2026-10-16. Approved by the owner: the four payment endpoints become payment requests; proposals in the
refactor plan are used for the open questions that had one.

## 1. The one rule

**No money counts as paid until the Executive approved it and the payment was confirmed.** Every payment —
supplier, worker, subcontractor, equipment, and later salaries and supplier invoices — goes through one
`PaymentRequest` in the Finance module, and the ledger is written in the same transaction as "Paid".

## 2. Who owns what

| Thing | Owner | Why |
|---|---|---|
| What can be paid, and how much is outstanding | The source module (Projects, Workforce, Subcontractors, Fleet) | It owns the records and the rules (approved, credit, FIFO, contract value) |
| The request, its approval, the bank step, its history | Finance (`finance.PaymentRequest`, `…Line`, `…Event`) | One flow and one place to approve, for every payee |
| The payment row with its allocations (`SupplierPayment`, `WorkerPayment`, `SubPayment`, equipment cost paid columns) | The source module, **written only when the request is Paid** | Every existing "paid / outstanding" calculation stays exactly as it is: a payment row still means money that left |
| The ledger (`finance.LedgerEntry`) | Finance | One book of record; reports reconcile to it |

**Reservation.** While a request is open (Submitted, Approved, SentToBank, Failed), its lines reserve the amounts
they plan to pay. The source module counts reserved amounts as taken when it checks a new request, so two open
requests can never pay the same invoice, day or contract value twice. Rejected and cancelled requests free their
reservation.

## 3. One transaction across two modules

A request is created by the source module (it holds its own lock and checks its rules) and stored by Finance; a
payment is settled by Finance (it moves the request to Paid and writes the ledger) and written by the source
module. Each pair runs in **one database transaction**: the module that starts it passes a `SharedTransaction`
(BuildingBlocks.Data) and the other joins it with a short-lived context on the same connection. Both modules'
audit entries are written in that same transaction. Nothing can be half done.

## 4. Status flow

| From | Action | To | Who (default) | Checks |
|---|---|---|---|---|
| — | create (the existing payment endpoints) | Submitted | `payments` Create (Accountant) | source rules + reservation, idempotency key |
| Submitted | reject (note required) | Rejected (final) | `payments` Approve (Executive) | not the creator; DB re-check |
| Submitted | approve | Approved | Executive | same |
| Submitted | approve & pay | Paid (Cash) or SentToBank (bank transfer, cheque) | Executive | same; bank step below |
| Approved | send to bank | SentToBank | `finance` Edit (Accountant) | bank transfer or cheque only |
| Approved (Cash), SentToBank | confirm payment | **Paid** → source payment row + ledger | `finance` Edit | paid date, reference; DB re-check |
| SentToBank | record failure (reason) | Failed | `finance` Edit | |
| Failed | send to bank again | SentToBank | `finance` Edit | |
| Submitted, Approved, Failed | cancel (reason) | Cancelled (final) | creator or Executive | frees the reservation |
| Paid | void the payment (existing void endpoints) | Reversed | `payments` Approve | ledger reversal entries; the source marks its payment voided as today |

- A request is never edited. Amount, payee, project and purpose are fixed at creation; to change them, cancel and
  create a new one. So "changing them after approval invalidates the approval" can't be bypassed.
- Every transition is stored in `PaymentRequestEvent` (who, when, from, to, note): the approval history.
- The creator of a request can't approve or reject it (separation of duties) [Industry]. With one Executive, the
  Accountant creates requests and the Executive decides.
- Cash "approve & pay" goes straight to Paid (proposal 4, accepted).

## 5. Bank

`IBankPaymentProvider` with `MockBankPaymentProvider` (setting `Finance:Bank:Provider = Mock`, the only value
today). The mock accepts every order and returns a reference `MOCK-<request number>`. The real NBE integration
replaces it later [Needs Confirmation: payee bank account data is not in the system yet]. Confirmation stays a
person's action (`confirm payment` with the bank's reference) until the bank sends confirmations itself.

## 6. Ledger

Single entry with account tags (proposal 1, accepted): one row per paid amount per project — signed amount
(money out is negative), account `Bank` or `Cash`, counterparty (Supplier, Worker, Subcontractor), project,
purpose, the request and the source payment. A void writes reversal rows (opposite sign, pointing at the
original). Rows are never updated or deleted.

## 7. Existing data (backfill)

The Finance migration copies every existing payment into an **imported** request (status Paid, or Reversed when
voided) with its lines, history and ledger rows, so the ledger starts complete. The SQL is idempotent and is also
run by a database test against hand-made legacy rows. Finance migrates last (it reads the other modules' tables
once, for this copy).

## 8. Endpoints

New (Finance): `GET /api/payment-requests`, `GET /api/payment-requests/{id}`, `POST …/{id}/approve`,
`…/reject`, `…/approve-and-pay`, `…/send-to-bank`, `…/confirm-payment`, `…/bank-failure`, `…/cancel`,
`GET /api/ledger`.

Changed (owner-approved): `POST /api/projects/{p}/supplier-payments`, `POST /api/workers/{id}/payments`,
`POST /api/projects/{p}/subcontracts/{c}/payments`, `POST /api/projects/{p}/equipment-costs/{id}/payment` now
answer **201 Created** with the payment request (status Submitted, `Location: /api/payment-requests/{id}`) instead of a payment. The same idempotency
key returns the same request.
