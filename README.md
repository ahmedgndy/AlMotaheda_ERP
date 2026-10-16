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

## Deployment notes

- Set `ConnectionStrings__Erp` and `Access__Tokens__SigningKey` as environment variables. Generate the key once
  with `openssl rand -base64 48` and keep it secret; changing it signs everyone out.
- Set `Archive__Storage__RootPath` to a folder on persistent storage that is backed up. It is required in
  Production; the API refuses to start without it.
- Apply migrations with the reviewed scripts or migration bundles before starting a new version: Platform first,
  then Access, Projects, Workforce, Subcontractors, Fleet, Archive, HR, Procurement, Reports and Finance. Without the
  Platform tables every save fails.
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
- [Workforce module](src/Modules/Workforce/AlMotaheda.Modules.Workforce/README.md): workers, daily labor, allocation to items, worker payments
- [Subcontractors module](src/Modules/Subcontractors/AlMotaheda.Modules.Subcontractors/README.md): companies, contracts, measured work, manpower, payments
- [Fleet module](src/Modules/Fleet/AlMotaheda.Modules.Fleet/README.md): equipment, project assignment, equipment costs
- [Archive module](src/Modules/Archive/AlMotaheda.Modules.Archive/README.md): project documents
- [Platform module](src/Modules/Platform/AlMotaheda.Modules.Platform/README.md): audit trail, outbox, notifications
- [Finance design](docs/modernization/07-finance-design.md): payment requests, the Executive's decision, bank, ledger
