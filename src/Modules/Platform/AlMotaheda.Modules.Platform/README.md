# Platform module

The audit trail, the outbox and the stored notifications. Schema `platform`.

## How a change becomes an audit entry and a notification

```
any module's slice ── SaveChanges ──► AuditTrailInterceptor (BuildingBlocks.Data)
                                        │ same transaction as the change
                                        ├─► platform.AuditEntry   (one row per added/changed/deleted record, old → new values)
                                        └─► platform.OutboxMessage (one row per save: which records changed, no amounts)
                     COMMIT
                                      OutboxDispatcher (this module, background, every ~2 s)
                                        └─► platform.Notification (NotificationRules) and marks the message handled
```

- **Nothing to do in a slice.** Every module's `DbContext` is registered with `AddModuleDbContext`, which adds the
  interceptor. A new module gets the audit trail by registering its context the same way.
- **Atomic.** The audit and the outbox are written inside the save's transaction (the handler's, or one the
  interceptor opens). A rolled-back change leaves no audit; a committed change always has one.
- **Left out:** classes or properties marked `[NotAudited]` (password hash, refresh tokens, sign-in state), row
  versions, and bulk `ExecuteUpdate`/`ExecuteDelete` statements (only Access uses them, for sign-in bookkeeping and
  removing permission overrides, which its security log records).
- **Order:** Platform is first in `ModuleCatalog` and its migrations run first: every module writes to its tables.

## Notifications (who hears what)

`Notifications/NotificationRules.cs`, pure and unit-tested. Nobody is told about their own action.

| Record | Status change | Who | Type |
|---|---|---|---|
| Expense / supply, equipment cost | → Approved, Returned, Rejected | recorder (`CreatedBy`) | `RecordApproved`, `RecordReturned`, `RecordRejected` |
| Daily labor sheet | → Approved, Returned, Rejected | recorder (`RecordedBy`) | same |
| Measured work, manpower sheet | → Approved, Rejected | recorder | same |
| Daily site report | → Reviewed, Returned | author | `RecordReviewed`, `RecordReturned` |
| Price change | → Approved, Rejected | requester | `RecordApproved`, `RecordRejected` |
| Budget | → Approved | author | `RecordApproved` |
| Report request | added | recipient | `ReportRequested` |
| Report request | → InProgress, Submitted | requester | `ReportStarted`, `ReportSubmitted` |
| Report request | → Returned, Completed, Cancelled | recipient | `RecordReturned`, `ReportCompleted`, `ReportCancelled` |
| Location alert (no position) | added, → Recovered | project manager and Executives, one row each | `LocationUnavailable`, `LocationAvailable` |
| Location alert (outside the site) | added, → Recovered | project manager and Executives, one row each | `OutsideSite`, `BackInsideSite` |

"Something is waiting for your review" is not a notification: it stays in the work inbox (`GET /api/inbox`),
computed from the records and the reviewer's permissions.

## Endpoints

| Endpoint | Who | Notes |
|---|---|---|
| `GET /api/notifications?unreadOnly=&page=&pageSize=` | signed in, own only | `{ items, page, pageSize, totalCount, unreadCount }` |
| `GET /api/notifications/unread-count` | signed in | `{ count }` for the bell badge |
| `POST /api/notifications/{id}/read` | recipient | 204; another user's notification answers 404 |
| `POST /api/notifications/read-all` | signed in | `{ marked }` |
| `GET /api/audit-entries?module=&entityType=&entityKey=&projectId=&userId=&from=&to=&page=&pageSize=` | Executive only | `from`/`to` are company dates (`yyyy-MM-dd`); each item has `changes: { "Field": [old, new] }` |

A notification item: `{ id, type, titleAr, titleEn, entityType, entityKey, projectId, createdAt, readAt }`. The
client opens the record from `entityType` + `entityKey` + `projectId` (for example `ProjectExpense` 40 on project 3
→ the expense page).

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `Platform:Outbox:Enabled` | `true` | Off only in test hosts without a database |
| `Platform:Outbox:PollInterval` | `00:00:02` | Wait when nothing is pending |
| `Platform:Outbox:BatchSize` | `50` | Messages per transaction |
| `Platform:Outbox:MaxAttempts` | `10` | A message that keeps failing is skipped and kept (`LastError`) for a person to look at |

## Open questions

- Should the Accountant also read the audit trail (financial records only)? Executive only for now.
- How long are handled outbox messages and old notifications kept? Nothing is deleted yet.
