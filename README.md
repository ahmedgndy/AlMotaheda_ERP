# AlMotaheda_ERP

Backend for a construction company's digital archive. Every construction project gets its own secure archive:
payment certificates, meeting minutes, site photos and videos, progress reports, contracts, invoices, technical drawings, and more.

## Status

- **Architecture foundation:** done. The solution structure, host, and infrastructure are in place.
- **Login and users (Access module):** done on the backend. Sign-in, users, roles and permissions, stored in SQL Server.
- **Projects (Projects module):** done on the backend. Projects, their teams, and who may open which project.
- **Project Workspace (backend):** done. Expenses and supplies, budgets, project items ("بنود") with item detail
  and the "not linked yet" block, items and prices, subcontractors, workers and daily labor allocation, daily
  site reports, site equipment, the project archive, the overview and reports. Design: `docs/project-workspace/`.
- The frontend (Flutter) is not built yet.

## Technology

- ASP.NET Core Web API on .NET 10 (long-term support until November 2028)
- Modular monolith, with vertical slices inside each module
- SQL Server with EF Core 10, one database schema per module
- JWT access tokens and rotating refresh tokens; ASP.NET Core password hashing
- Built-in structured logging, ProblemDetails error responses, health checks, Swagger
- xUnit tests, including architecture tests that enforce module boundaries, and SQL Server tests in Docker (Testcontainers)

## Local setup

You need the .NET 10 SDK, a SQL Server you can reach, and Docker if you want to run the database tests.

1. **Restore the tools** (for `dotnet ef`):

   ```bash
   dotnet tool restore
   ```

2. **Set the two required secrets.** They live in your user secrets, never in the repository:

   ```bash
   dotnet user-secrets set "ConnectionStrings:Erp" "Server=localhost,1433;Database=AlMotahedaErp;User Id=sa;Password=<your password>;TrustServerCertificate=true" --project src/Host/AlMotaheda.Api
   dotnet user-secrets set "Access:Tokens:SigningKey" "$(openssl rand -base64 48)" --project src/Host/AlMotaheda.Api
   ```

3. **Create the database tables.** Review the SQL first, then apply it:

   ```bash
   dotnet ef migrations script --idempotent --project src/Modules/Platform/AlMotaheda.Modules.Platform -o platform.sql
   dotnet ef migrations script --idempotent --project src/Modules/Access/AlMotaheda.Modules.Access -o access.sql
   dotnet ef migrations script --idempotent --project src/Modules/Projects/AlMotaheda.Modules.Projects -o projects.sql
   dotnet ef database update --project src/Modules/Platform/AlMotaheda.Modules.Platform --connection "<the same connection string>"
   dotnet ef database update --project src/Modules/Access/AlMotaheda.Modules.Access --connection "<the same connection string>"
   dotnet ef database update --project src/Modules/Projects/AlMotaheda.Modules.Projects --connection "<the same connection string>"
   ```

   Then, the same way: Workforce, Subcontractors, Fleet, Archive.

   Always Platform first (every save of every module writes its audit and outbox tables), then Access, then
   Projects, then the others: their tables point to `access.User`, `projects.Project`, `projects.ProjectItem` and
   `projects.Supplier`. Finance last: its second migration copies the payments already recorded into the ledger.

   Or, in development only, set `Database:ApplyMigrationsOnStartup` to `true` and let the API apply it at startup.

4. **Create the first Executive.** On the first start with an empty user table, the API creates one Executive
   from these settings. That account must change its password at first sign-in. Remove the settings afterwards.

   ```bash
   dotnet user-secrets set "Access:Bootstrap:Email" "you@company.com" --project src/Host/AlMotaheda.Api
   dotnet user-secrets set "Access:Bootstrap:Password" "<at least 10 characters>" --project src/Host/AlMotaheda.Api
   ```

5. **Run it:**

   ```bash
   dotnet run --project src/Host/AlMotaheda.Api --launch-profile https
   ```

   Open https://localhost:7180/swagger. Call `POST /api/auth/login`, copy the `accessToken`, press **Authorize**,
   and paste it. Your first call must be `POST /api/auth/change-password`.

## Tests

```bash
dotnet test AlMotahedaERP.slnx
```

The SQL Server tests start SQL Server 2022 in Docker. With Docker Desktop running they run automatically; without it
they are reported as skipped. On a Mac with Apple Silicon, turn on "Use Rosetta for x86_64/amd64 emulation" in
Docker Desktop settings.

## Docker, CI and releases

- Run the API and a SQL Server in Docker: `docker compose -f deploy/compose.yaml up -d --build` (fill in
  `deploy/.env` first, from `deploy/.env.example`).
- GitHub Actions builds and tests every push and pull request; a `v*` tag publishes the Docker image, a Windows
  build and the migration scripts.

Details: [deploy/README.md](deploy/README.md).

## Deployment notes

- Set `ConnectionStrings__Erp` and `Access__Tokens__SigningKey` as environment variables. Generate the key once
  with `openssl rand -base64 48` and keep it secret; changing it signs everyone out.
- Set `Archive__Storage__RootPath` to a folder on persistent storage that is backed up. It is required in
  Production; the API refuses to start without it.
- Apply migrations with the reviewed scripts or migration bundles before starting a new version: Platform first,
  then Access, Projects, HR, Workforce, Subcontractors, Fleet, Archive, Procurement, Reports and Finance. HR comes
  before Fleet since the vehicle tables point at `hr.Employee`. Without the Platform tables every save fails.
- `Projects__PriceCheckPolicy` decides what a purchase, expense or supplier invoice line above its reference price
  range does: `ExecutiveApproval` (shipped in `appsettings.json`: saved as entered with a price exception for the
  Executive; nobody approves or pays it, and a supplier invoice stays on price hold, until the Executive decides),
  `Warn` (the code's default when the key is missing: saved with a justification and a case for the inbox and
  reports, nothing gated) or `Block` (refused). See `docs/project-workspace/10-price-control.md`.
- `Projects__CustodyBalanceCheck` decides what a payment out of a cash custody (a purchase, an expense or a weekly
  payroll) does when it is more than the custody holds: `Warn` (default: saved, the balance goes negative until it is
  funded) or `Block` (refused). See `docs/project-workspace/09-project-financial-workspace.md`.
- `Finance__CustodyPeriodCarryForward` decides what closing a custody period needs: `CarryForward` (default: the
  balance continues) or `RequireZeroBalance` (the custody must have been returned in full). See
  `docs/project-workspace/12-treasury-custody.md`.
- `Fleet__ClaimExecutiveApprovalAbove` (default `0`): a vehicle expense claim above this amount is approved by an
  Executive only. `Fleet__MaxClaimAgeDays` (60), `Fleet__ImplausibleDistanceKmPerDay` (1000),
  `Fleet__ImageMaxMegabytes` (15) and `Fleet__DocumentMaxMegabytes` (20) are the claim and evidence limits.
- `Fleet__Gps__Provider` is `None` until a vehicle tracking provider is integrated: the location endpoints then
  answer "not configured" and show no positions. `Fleet__Gps__RetentionDays` (90) applies once a provider exists.
- `Fleet__Storage__RootPath` is where claim evidence (odometer photos, receipts) is kept; by default a
  `fleet-module` folder under `Archive__Storage__RootPath`. Back it up with the archive. `Hr__Storage__RootPath` is
  the same for the evidence of employees' HR requests (default: an `hr-module` folder under the archive folder;
  `Hr__Storage__MaxMegabytes`, default 15). When either follows the archive folder, the Archive module's startup
  check covers it.
- The audit trail table (`platform.AuditEntry`) is append-only at the database: the `ProtectAuditTrail` migration
  adds a trigger that refuses every update and delete. Dropping that trigger is a DBA action, visible as such.
- `Projects__TimeZone` is the company time zone (default `Africa/Cairo`). The server needs time zone data; on a
  minimal container image without it, install `tzdata` or set `UTC`.
- Turn on `READ_COMMITTED_SNAPSHOT` for the database.
- Behind a reverse proxy on another machine, list it in `ForwardedHeaders__KnownProxies__0`.
- Serve the frontend and the API from the same site (for example `erp.example.com` and `erp.example.com/api`):
  the refresh cookie is `SameSite=Strict`.

## Database backup and restore

The API backs up the database itself on the Windows server (Settings → Backup, Executive only): every day at the
set time, or at once with "Run backup now". Each backup is `BACKUP DATABASE ... WITH CHECKSUM` (and `COMPRESSION`
except on Express), checked with `RESTORE VERIFYONLY`, and saved in the backup folder as
`{Database}_Full_{yyyyMMdd_HHmmss}.bak`, with the uploaded documents zipped next to it as
`{Database}_Files_{yyyyMMdd_HHmmss}.zip`. The newest N verified backups are kept (default 3); older ones are deleted
only after a new one is verified, and only files with these exact names. A failed backup notifies every Executive.
History: Settings → Backup history (`GET /api/backups`).

- The SQL Server service account must be able to write to the backup folder, and the API must run on the same
  machine (it zips the documents and removes old files locally).
- **Keep a copy off this machine** (another disk, a NAS or cloud storage, copied daily): a backup on the same disk
  is lost with the disk. This is not automated.

**Restore** (stop the API first, so nothing writes during the restore):

1. Pick the newest `*_Full_*.bak` that the history shows as verified.
2. In SQL Server Management Studio, or with `sqlcmd`:

   ```sql
   RESTORE VERIFYONLY FROM DISK = N'D:\Backups\AlMotahedaErp_Full_20261016_020000.bak' WITH CHECKSUM;
   ALTER DATABASE [AlMotahedaErp] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
   RESTORE DATABASE [AlMotahedaErp] FROM DISK = N'D:\Backups\AlMotahedaErp_Full_20261016_020000.bak'
       WITH REPLACE, CHECKSUM, RECOVERY;
   ALTER DATABASE [AlMotahedaErp] SET MULTI_USER;
   ```

   To restore on another server or under another name, add `MOVE` for the data and log files
   (`RESTORE FILELISTONLY FROM DISK = ...` lists their logical names).
3. Unzip the `*_Files_*.zip` of the same time into the `Archive__Storage__RootPath` folder, replacing its content.
4. Start the API. Everyone signs in again if the restored data predates their last sign-in.

## Documentation

- [Backend architecture](docs/architecture.md): structure, dependency rules, and how to add modules and features
- [Access module](src/Modules/Access/AlMotaheda.Modules.Access/README.md): sign-in, users, roles and permissions
- [Projects module](src/Modules/Projects/AlMotaheda.Modules.Projects/README.md): projects, teams and project-level access
- [Project Workspace design](docs/project-workspace/00-overview.md) and its [decisions and open questions](docs/project-workspace/07-decisions.md)
- [Project cost management](docs/project-workspace/08-cost-management.md): main items and sub-items, the work-item cost breakdown, price management and price checks, cost alerts
- [Project financial workspace](docs/project-workspace/09-project-financial-workspace.md): subcontractor engagements per work item and variations, supply agreements and supplier accounts, cash custody, weekly labor settlements, the financial formulas
- [Treasury, custody hierarchy, payroll funding, vehicles and vehicle expenses](docs/project-workspace/12-treasury-custody.md): the company treasury, custody accounts and transfers, payroll batches, vehicles with drivers and an operating custody, expense claims with evidence and price control, violations and HR referrals, the GPS abstraction (audit: [11-treasury-custody-audit.md](docs/project-workspace/11-treasury-custody-audit.md))
- [Central Price Control](docs/project-workspace/10-price-control.md): configurable categories and units, price rules, the price exception a record above its range opens, the Executive's three decisions, the approval and payment gate on every path (site records, supplier payments, Procurement invoices), reports, offline rules
- [Workforce module](src/Modules/Workforce/AlMotaheda.Modules.Workforce/README.md): workers, daily labor, allocation to items, worker payments
- [Subcontractors module](src/Modules/Subcontractors/AlMotaheda.Modules.Subcontractors/README.md): companies, contracts, measured work, manpower, payments
- [Fleet module](src/Modules/Fleet/AlMotaheda.Modules.Fleet/README.md): equipment, project assignment, equipment costs
- [Archive module](src/Modules/Archive/AlMotaheda.Modules.Archive/README.md): project documents
- [Platform module](src/Modules/Platform/AlMotaheda.Modules.Platform/README.md): audit trail, outbox, notifications
- [Finance design](docs/modernization/07-finance-design.md): payment requests, the Executive's decision, bank, ledger
