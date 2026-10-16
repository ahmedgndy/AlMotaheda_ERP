# Platform step — audit trail, outbox, notifications (refactor plan M4 + M7)

Date: 2026-10-16. Design and contract: `src/Modules/Platform/AlMotaheda.Modules.Platform/README.md`.

## 1. Added

| Added | Why |
|---|---|
| Module `AlMotaheda.Modules.Platform` (schema `platform`), first in `ModuleCatalog` | Owns the audit trail, the outbox and the stored notifications |
| `platform.AuditEntry` (insert-only) | Who, when, which record, old → new value of every changed field, trace id. Replaces "audit only in logs, which can drop messages" (Current State problem 5) |
| `platform.OutboxMessage` | "These records changed", committed with the change; handled after the commit. The base for SignalR and cache invalidation (Phases 5–6) |
| `platform.Notification` | Persisted notifications in Arabic and English, with a link to the record (entity type, key, project) |
| `AuditTrailInterceptor` (BuildingBlocks.Data) | Writes the audit and the outbox for every save of every module, in the save's transaction, with no change to the ~150 existing slices |
| `ModuleDatabase.AddModuleDbContext` (BuildingBlocks.Data) | One way to register a module's context (connection string check, warning filters, the interceptor). Replaces six copies |
| `[NotAudited]` | Keeps secrets and bookkeeping out of the trail: `User.PasswordHash`, `RefreshToken`, `UserSignInState` |
| `OutboxDispatcher` + `NotificationRules` | Background worker; pure rules: the recorder hears approved / returned / rejected; report requests notify recipient and requester; nobody hears about their own action |
| `CompanyClock.StartOfDayUtc` | Company dates → UTC moments, for the audit filters (and the reports later) |
| Endpoints | `GET /api/notifications`, `GET /api/notifications/unread-count`, `POST /api/notifications/{id}/read`, `POST /api/notifications/read-all`, `GET /api/audit-entries` (Executive) |
| Test project `AlMotaheda.Modules.Platform.Tests` | Rules, outbox handling, schema, query translation, API security, and real SQL Server flows |
| Test infrastructure | `StandInDatabase` swaps the audit interceptor (a keyed service, `ModuleDatabase.AuditTrailServiceKey`) for one that does nothing: the stand-in has no Platform tables. `TestApplication` turns the outbox dispatcher off when there is no database. `ModuleRegistrationTests` now expects Platform first |

## 2. Removed

| Removed | Why |
|---|---|
| The connection-string check and `AddDbContext` block in each of the six modules | Now `AddModuleDbContext` |
| `ConnectionStringName` in each module's `*Database` class | Only that block used it |

## 3. Migration

`20261015234820_InitialPlatform` (schema `platform`: 3 tables, 1 check constraint each on `AuditEntry` and
`OutboxMessage`, 2 unique indexes, 5 other indexes). **Apply it before every other module**: without these tables
every save fails. No change to any other module's schema.

## 4. Behaviour changes to existing endpoints

None in their responses. Every save now also writes one audit row per changed record and one outbox row, in the
same transaction (a few milliseconds per save).

## 5. Frontend contract (React and Flutter)

- The bell can use `GET /api/notifications/unread-count` and the list `GET /api/notifications`, instead of (or
  next to) the work inbox. The inbox stays: it answers "what waits for me", notifications answer "what happened".
- A notification opens its record from `entityType` + `entityKey` + `projectId`.
- No change is required: existing screens keep working.

## 6. Tests

All 8 test projects pass when run one after another (Docker memory, see the test notes), including 38 new Platform
tests. `ArchiveApiTests.Other_endpoints_keep_the_server_default_request_size` failed once in a full run and passed
3 of 3 times alone and in the other full runs: the known flaky request-size test, not caused by this step.

## 7. Open questions (Needs Confirmation)

1. May the Accountant read the audit trail (financial records only)? Executive only for now.
2. Retention: how long to keep handled outbox messages, read notifications and audit entries? Nothing is deleted.
