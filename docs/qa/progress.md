# Audit progress

Status of each phase of the 2026-10-15 audit. ✅ done and verified · ⚠️ done with a caveat · ⏳ not done.

| Phase | Status | Notes |
| --- | --- | --- |
| 0. Access checklist | ✅ | Approved by the owner. Local commits only; nothing pushed. |
| 1. Discover both apps | ✅ | Backend: .NET 10 modular monolith (Access, Projects, Workforce, Subcontractors, Fleet, Archive), SQL Server, JWT + refresh cookie. App: Flutter 3.47, Riverpod, go_router, Dio. |
| 2. Safe test environment | ✅ | Isolated database `AlMotahedaErp_Test`, separate archive folder, synthetic users (see [README](README.md)). |
| 3. Backend audit | ✅ | 9 defects fixed (B1–B9 in the [audit report](audit-report.md)). |
| 4. Flutter audit | ✅ | 13 defects or gaps fixed (F1–F13). |
| 5. End-to-end workflows A–G | ✅ | Live API run: 149 passed, 0 failed, 0 blocked. |
| 6. Location / GPS | ✅ | Audited and documented; attendance and tracking need decisions ([doc](location-and-notifications.md)). |
| 7. Notifications | ✅ | In-app work inbox built and tested; push needs FCM/APNs credentials. |
| 8. Missing features | ⚠️ | Built: work inbox, money-free item options, CSV export. Not built (no backend module yet): finance, client income, vehicles/fuel, office, guards. |
| 9. Build, test, regression | see below | |
| 10. Safety | ✅ | No secrets in tracked files; developer database only read after the first start; user's uncommitted work untouched. |
| 11. Documentation | ✅ | This folder. |

## Phase 9 results (final run)

### Backend — `dotnet build` and `dotnet test`, one project at a time

Build: succeeded (warnings are NuGet advisories only).

| Test project | Tests | Result |
| --- | --- | --- |
| AlMotaheda.Api.Tests | 31 | ✅ all passed |
| Modules.Access.Tests | 169 | ✅ all passed |
| Modules.Archive.Tests | 216 | ✅ all passed (one size-limit test failed once under load, passed 3 times alone and in the full rerun) |
| Modules.Fleet.Tests | 189 | ✅ all passed |
| Modules.Projects.Tests | 626 | ✅ all passed after one test was corrected (below) |
| Modules.Subcontractors.Tests | 234 | ✅ all passed after one test was updated (below) |
| Modules.Workforce.Tests | 254 | ✅ all passed |
| **Total** | **1,719** | **✅ 0 failing** |

Two existing tests were changed, and why:

- `SubcontractFlowTests` expected a *different* payment sent with the same idempotency key to return the first
  payment. That is the unsafe behaviour fixed in B3; the test now expects `409 idempotency_key_reused` for a
  different payment and still expects the first payment back for an identical retry.
- `ExpenseWorkflowTests` expected an engineer approving their own supply to get `409 own_record`. Engineers have
  no projects Approve by default, so the server correctly answers `403` first. The test now checks the `403`
  for the engineer and checks `own_record` with an Executive approving their own record.

Live API run against the isolated database after all changes: **149 passed, 0 failed, 0 blocked**.

### Flutter

- `flutter analyze`: no issues.
- `flutter test`: 65 passed (59 before, plus 6 for the CSV encoder).
- macOS debug app: built and launched against the test backend.
- Android debug APK: built (exit 0, 169 MB) at
  `~/StudioProjects/al_motaheda_erp_flutter/build/app/outputs/flutter-apk/app-debug.apk`,
  SHA-256 `402f4eb547ebd8e3ac1346de42559f8e75a1da0529ed8409b63f0b9eb1ac6ba1`. The first build downloads Android SDK
  platforms 34 and 36 and the Flutter engine, which took about 25 minutes on this connection.

### Manual (by hand in the running app)

⏳ Not done. The app opens on the sign-in screen (Arabic, right-to-left, pointing at `https://localhost:7180`), but
the click-through of every tab with each role needs a person to sign in with the test accounts in
`_sync/qa/test-accounts.local`. Suggested route: sign in as `qa.engineer@test.local` (record a supply, see the
bell), then `qa.accountant@test.local` (bell shows it, approve it, export CSV), then `qa.manager@test.local`
(Items tab, link the supply, export CSV). The APK was built but not installed on a phone or emulator.
