# SignalR step — live notifications and live data (brief Phase 5)

Date: 2026-10-16. Design: `04-signalr-and-cache-maps.md` §1–3 (this file records what was built and where it differs).

## 1. How it works

```
a module saves ──► AuditTrailInterceptor writes the audit entry + an outbox message (same transaction, no amounts)
                                   │  commit
Platform OutboxDispatcher ── stores the notifications ── commit ──► SignalR (only after that commit):
                                                                    • "notification" → user:{recipient}
                                                                    • "changed"      → the groups of LiveScopes
```

- Nothing is announced before it is committed; nothing is lost if the server stops (the outbox row stays until
  handled). Sending is best effort: if it fails, the data is still right and clients refetch on reconnect.
- **One dependency map** (`Platform/Live/LiveScopes.cs`) lists, per record type, the screens and figures it
  affects (`aggregates`) and the groups that hear about it. Phase 6 (cache) will use the same map.

## 2. Client contract (React and Flutter)

| Item | Value |
|---|---|
| URL | **`/api/hubs/erp`** (the plan said `/hubs/erp`; it sits under `/api` with every other endpoint) |
| Auth | The same access token. WebSockets send it as `access_token` in the query string (only for `/api/hubs`; request logs never record query strings). Refused without a valid token or while a password change is pending |
| Token expiry | The server closes the connection when the token expires; the client reconnects with its refreshed token and re-joins. Every join is checked again, so removed permissions or projects stop the signals within the token lifetime (15 min) |
| Server → client | `changed(ChangeSignal)` · `notification(NotificationSignal)` · `resync({ reason })` |
| Client → server | `Join(scope) → bool` (false = not allowed, nothing joined) · `Leave(scope)` |
| On connect | Already in `user:{id}` |
| On reconnect / `resync` | Re-join the scopes, then refetch what is on screen (missed signals are not replayed); unread notifications from `GET /api/notifications` |

```jsonc
// changed: refetch, never trust as data
{ "entityType": "ProjectExpense", "entityKey": "123", "version": 4711, "action": "Modified",
  "aggregates": ["Expenses", "SupplierBalances", "ProjectItems", "ProjectOverview", "CostSummary", "BudgetComparison", "CompanyDashboard"],
  "projectId": 42, "scope": "project:42:money" }
// notification: text and link come from GET /api/notifications
{ "id": 9001, "type": "RecordApproved", "entityType": "ProjectExpense", "entityKey": "123", "projectId": 42 }
```

`version` is the outbox message id: always increasing; ignore a signal older than the data on screen.

## 3. Groups

| Scope | Join allowed when | Hears |
|---|---|---|
| `user:{id}` | automatic; only one's own id | notifications, `resync` |
| `project:{id}` | the project is visible to the caller (same check as the API) | records and lists of the project, no money figures |
| `project:{id}:money` | visible **and** `projects` See money | cost, paid, items, budget, overview, committed |
| `company:finance` | `finance` View or `payments` Approve | payment requests, ledger, dashboards, every money change |
| `company:hr` | `hr` View | employees, payroll, salary sheets |
| `company:procurement` | `procurement` Edit, or Executive | requisitions, quotations, orders, receipts, invoices, catalog prices |

An Engineer can't join a money or company group. `entity:{type}:{id}` groups from the plan are **not built**:
record changes reach the record's project or company group with `entityType` / `entityKey`, and the open screen
filters on them.

## 4. Changed

| Where | Change |
|---|---|
| Platform | `ErpHub`, `LiveAccess` (join checks), `LiveScopes` (dependency map), `ILiveUpdates` / `SignalRLiveUpdates`; the outbox dispatcher sends the signals after its commit; references `Projects.Contracts` for the project check |
| Access | JWT bearer reads `access_token` from the query string for `/api/hubs` only |
| Host | `UseWebSockets()` |
| Tests | `Microsoft.AspNetCore.SignalR.Client` 10.0.9 (Platform tests only) |

No migration. No change to existing endpoints.

## 5. Tests

Platform tests 38 → 60: the dependency map (money vs non-money groups, HR, procurement, detail rows silent,
`resync` on membership / permission change, no amount or name in any signal type), the hub over a real connection
(401 without a token, refused during a pending password change, every company group's permission, no joining
another user's group), and one SQL Server flow: an engineer and an accountant connected; the engineer can't join the
money or finance groups or another project; after an expense is recorded and approved the engineer receives the
notification and the project change, the accountant the money and finance changes, and the engineer nothing about money.

## 6. Scale-out (documented, not built)

One server holds the groups in memory. With two or more: add the Redis backplane (`AddStackExchangeRedis`) or Azure
SignalR in the host only, and keep one outbox dispatcher (it already locks its rows with `UPDLOCK, READPAST`).

## 7. Open questions

1. Live GPS / "engineer left site geofence" and "budget exceeded" notifications: no data for them yet (no GPS module;
   budget alerts need a threshold rule) **[Confirm]**.
2. "Payment request awaiting the Executive" is in the work inbox today; should it also be a stored notification to
   every Executive? (role-based notifications aren't built; notifications go to a record's own people) **[Confirm]**.

## 8. Results

2,080 of 2,080 tests pass, the 12 projects run one after another: Api 33, Access 180, Archive 216, Finance 57,
Fleet 193, HR 99, Platform 60, Procurement 92, Projects 638, Reports 24, Subcontractors 234, Workforce 254.
One existing test changed: the "every endpoint refuses a pending password change" check skips the hub's transport
routes (they take several HTTP methods); the hub's refusal is tested over a real connection instead.
