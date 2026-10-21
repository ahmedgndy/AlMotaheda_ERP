# Office module (عهدة المكتب)

The company office's petty cash: one or more cash custodies, each held by an office custodian (role `office_admin`),
with an append-only ledger, office expenses with camera receipts, funding through Finance, repair and purchase
requests, and cash counts. Company overhead: nothing here belongs to a project.

## Money rules

- **There is no balance column.** The recorded balance is the sum of `office.OfficeLedgerEntry`. Entries are never
  edited or deleted; a mistake is corrected by a linked entry (a reversal, an adjustment).
- Every entry has a unique source (`SourceType`, `SourceId`): the same expense, funding, return or count can't be
  posted twice, whatever the retries. Postings run under the custody's SQL application lock
  (`AlMotaheda.Office.Custody.{id}`).
- The custody view keeps apart: recorded cash, expenses not posted yet, funding asked / agreed / paid out but not
  received, approved repair estimates, and **available** = recorded − pending expenses (policy) − commitments.

## Workflows

| Record | Flow |
|---|---|
| Funding (طلب تغذية العهدة) | Submitted → (Accountant agrees, full or partial: a Finance payment request is made) Approved → (Finance pays it) Disbursed → (custodian confirms the cash) Received = **one Funding ledger entry**. Received less than paid out: reason required, reviewers alerted. Rejected / Cancelled (cancel after approval only once Finance rejected or cancelled its request). |
| Expense (تسجيل مصروف من العهدة) | Submitted → PartlyApproved → Approved → posted (−amount) when the price case (if any) is approved and the cash covers it; otherwise it waits and posts by itself after the next funding or price decision. Returned → corrected → Submitted (new round; old approvals don't count). Rejected (never posted). A posted expense is reversed by a linked entry. |
| Repair / purchase request | Submitted → Approved → Completed by recording its one expense (`spendRequestId`; unique while not rejected). Repair needs before photos; its final cost needs after photos unless a reviewer waived them with a reason. Emergency repair: recorded at once with a reason. |
| Cash count | Matched (nothing to do) or Submitted (difference + explanation, reviewers alerted) → Accepted (balance unchanged) or Adjusted (difference posted with the reason). |
| Cash return / close | Reviewer records cash handed back (−amount, idempotent). A custody closes only with a zero balance and nothing waiting. Suspended custodies accept nothing new. |

## Approval policy (server-side, `ExpensePolicy`)

One approval for a routine expense. **Two** (two different people, one of them the Executive) when the total is above
`Office:RoutineApprovalLimit`, the price is above the central range or its unit can't be compared, it looks like a
duplicate (same receipt SHA-256, same vendor + invoice, or same vendor + amount + date), it is a repair, an emergency,
above its estimate, or above the available cash. Nobody approves what they recorded or a custody they hold. A double
tap answers 200 with the same record; the unique index `UX_OfficeExpenseApproval_Once` keeps one approval per person
per round.

## Price Control

An expense may name an item of the central price list in the office categories (`Office:PriceCategories`, default
Internet, OfficeExpenses, Maintenance). The comparison (range, variance) is copied onto the expense when submitted and
never rewritten. Above the range under the gating policy, a central case (`PriceExceptionSources.OfficeExpense`, no
project) is opened for the Executive: **approve once** (catalog unchanged), **approve with a new price** (a new central
range from its date; history kept), or **reject** (the expense never posts). `IPriceControl` gained `ListItemsAsync`
and `WithdrawExceptionsAsync`; office cases are visible to office reviewers in the Price Control inbox.

## Roles

| Role | Office permissions | Can |
|---|---|---|
| Office Admin (custodian) | office VCE | Hold a custody; record expenses, funding, repairs, counts on it; confirm received funds. Sees only the custodies they hold. |
| Accountant | office VCEAF, payments VCF | Open/suspend/close custodies, agree funding (makes the Finance request), review everything, adjust counts, record returns, reverse. |
| Executive | all | Review, second approval of high-risk expenses, decide price cases, approve the funding payment in Finance. The Executive does not agree funding in Office (Finance forbids deciding a request you created). |

There is no "Financial Manager" role in the system; the Accountant fills that place (owner decision pending).

## Files

Receipts and photos are stored privately in `Office:Storage:RootPath` (default: `<Archive:Storage:RootPath>/office-module`,
or `App_Data/office` outside Production), as `office/yyyy/MM/<guid>` without extension. The type comes from the
first bytes (JPEG, PNG, WebP, HEIC; PDF only for quotations, transfer slips and supporting papers). Files are read
only through `GET /api/office/attachments/{id}/content` after a permission check, `Cache-Control: private, no-store`.

"Camera only" is enforced by the app (it opens the camera, never the gallery). The server records the app's
`captureSource` and capture time but cannot prove how a photo was made.

## Notifications

Alert rows (`OfficeAlert` subclasses) become stored notifications and pushes through the Platform outbox after the
commit: expense submitted / posted, funding requested / paid out, low balance (once per crossing), cash discrepancy,
repair or purchase requested. Status changes notify the custodian (approved, returned, rejected, reviewed).
`GET /api/office/alerts/{id}` tells the app which record an alert is about.

## Setup

```
dotnet ef migrations script --idempotent --project src/Modules/Office/AlMotaheda.Modules.Office -o office.sql
```

Also apply the Projects migration `OfficePriceExceptions` and the Finance migration `OfficeCustodyFunding` (both keep
existing data). Development applies them at startup (`Database:ApplyMigrationsOnStartup`).
