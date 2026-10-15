# QA audit — backend and Flutter app (2026-10-15)

This folder records an end-to-end audit of the ERP: the ASP.NET backend in this repository and the Flutter app
in `~/StudioProjects/al_motaheda_erp_flutter`.

| File | What it holds |
| --- | --- |
| [audit-report.md](audit-report.md) | What was checked, what was wrong, what was fixed, and what is still open |
| [feature-matrix.md](feature-matrix.md) | Every workspace feature → backend endpoint → app screen → how it was verified |
| [location-and-notifications.md](location-and-notifications.md) | How location/GPS and notifications work today, and the decisions still needed |
| [progress.md](progress.md) | The audit checklist, phase by phase |

## The isolated test environment

All write tests ran against a separate database, `AlMotahedaErp_Test`, on the same local SQL Server. The
developer database was only read.

- The test backend is started by `_sync/qa/start-test-backend.sh` in the Flutter repo. It reads the developer
  connection string from user-secrets, swaps only the database name, and passes it as an environment variable. No
  secret is written to a tracked file or printed.
- It runs on `https://localhost:7180` with its own archive folder (`_sync/qa/archive-storage`) and a synthetic
  Executive (`qa.executive@test.local`). Test users `qa.manager@`, `qa.engineer@`, `qa.engineer2@` and
  `qa.accountant@test.local` are created through the API. Their passwords live only in
  `_sync/qa/test-accounts.local` (mode 600, never committed).
- `_sync/qa/e2e.py` drives the real API through every workflow (about 150 checks) and writes
  `e2e-results.md`.

To throw the test data away, drop the `AlMotahedaErp_Test` database and delete `_sync/qa/archive-storage`. Nothing
else is touched.

## Running the automated tests without exhausting memory

The database tests start one SQL Server container (Testcontainers) per test assembly. On an 8 GB machine running
several assemblies in parallel ran out of memory and the operating system killed containers. Run the test
projects one at a time:

```sh
dotnet build AlMotahedaERP.slnx
for p in tests/AlMotaheda.Api.Tests tests/Modules/*.Tests; do dotnet test "$p" --no-build -m:1; done
```
