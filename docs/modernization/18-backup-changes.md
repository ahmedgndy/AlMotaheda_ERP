# Database backup step (brief Phase 7)

Date: 2026-10-16. Restore steps: `README.md` → "Database backup and restore".

## 1. How it works

| Step | Rule | Label |
|---|---|---|
| Schedule | Hosted service checks every minute: once per company day at or after the set time (default 02:00); "Run backup now" wakes it at once | Company |
| Windows only | `RuntimeInformation.IsOSPlatform(Windows)`; elsewhere it logs once and does nothing | Company |
| Backup | `BACKUP DATABASE @db TO DISK = @path WITH CHECKSUM, INIT, FORMAT, COMPRESSION` (no `COMPRESSION` on Express, detected with `SERVERPROPERTY('EngineEdition')`); names and paths are parameters | Company |
| Verify | `RESTORE VERIFYONLY FROM DISK = @path WITH CHECKSUM`; size from `msdb` | Company |
| Documents | The uploaded documents (`Archive:Storage:RootPath`) zipped next to the backup, same time stamp | Company (brief: back them up or document) |
| Names | `{Database}_Full_{yyyyMMdd_HHmmss}.bak`, `{Database}_Files_{yyyyMMdd_HHmmss}.zip` | Company |
| Retention | Keep the newest N verified backups (default 3, Executive sets 1–365). Older ones are removed **only after the new one is verified**, and **only files matching the exact name pattern** for this database; failed or unverified runs never count as kept | Company |
| Failure | Recorded with its error; every active Executive gets a stored `BackupFailed` notification and a live signal | Company |
| One at a time | A second request while one runs is skipped | Industry |
| Off-machine copy | Recommended, **not automated** (README) | Company |

The application, SQL Server and the backup folder are on the same Windows machine (the folder is local to SQL
Server, and the API zips and removes files there) **[Confirm]** this matches the server setup.

## 2. Endpoints (Executive only; changes re-check the role in the database)

| Endpoint | Notes |
|---|---|
| `GET /api/settings/backup` | `enabled`, `dailyAt` (HH:mm), `folder`, `retentionCount`, `includeDocuments`, `runRequested`, `supportedOnThisServer` |
| `PUT /api/settings/backup` | Same fields + `rowVersion`; folder must be a full local path; written to the audit trail (before/after) |
| `POST /api/settings/backup/run` | 202; 409 `backup_not_supported` on a non-Windows server; audited |
| `GET /api/backups` | History, newest first: trigger, who, start/end, status, file name, size, verified, compressed, documents zip and size, error, when retention removed its files |

## 3. Data

`platform.BackupSettings` (one row, seeded: on, 02:00, `C:\AlMotaheda\Backups`, keep 3, documents included) and
`platform.BackupRun`. Migration `Platform/BackupSettingsAndHistory`.

Settings: `Platform:Backup:PollSeconds` (60), `CommandTimeoutMinutes` (120), `ServiceEnabled` (true; off only in
test hosts without a database), `AllowNonWindows` (tests only).

## 4. Tests

Platform tests 66 → 79: names and the strict pattern (other databases, other names and paths refused), the daily
schedule, retention (newest kept, failed and unverified ignored, the newest always stays), Executive-only endpoints
and settings validation, and one SQL Server flow: three real backups verified and sized, the oldest removed with its
documents zip when keeping two, then a folder SQL Server can't write: the run fails, nothing is removed, and the
Executive is notified.

Found on the way: the audit `Action` column holds 10 characters; "run requested" is stored as `Modified` with
`{"runRequested":true}`.

## 5. Open questions

1. Backup folder on the production server (a second disk is best) and whether the seeded 02:00 suits the company.
2. Off-machine copy: which target (NAS, external disk, cloud) and who checks it.
3. Should the Executive also be told when a backup **succeeds** (today: only failures, as the brief asks)?

## 6. Results

2,099 of 2,099 tests pass, the 12 projects run one after another: Api 33, Access 180, Archive 216, Finance 57,
Fleet 193, HR 99, Platform 79, Procurement 92, Projects 638, Reports 24, Subcontractors 234, Workforce 254.
README: new "Database backup and restore" section; the migration order there now lists HR, Procurement and Reports.
The code lives in `Platform/DatabaseBackup/`: the repository's standard Visual Studio `.gitignore` ignores folders
named `Backup*`, so a `Backup/` folder would silently never be committed.
