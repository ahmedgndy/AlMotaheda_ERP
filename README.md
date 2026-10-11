# AlMotaheda_ERP

Backend for a construction company's digital archive. Every construction project gets its own secure archive:
payment certificates, meeting minutes, site photos and videos, progress reports, contracts, invoices, technical drawings, and more.

## Status

- **Architecture foundation:** done. The solution structure, host, and infrastructure are in place.
- **Login and users (Access module):** done on the backend. Sign-in, users, roles and permissions, stored in SQL Server.
  The frontend screens are not connected yet.
- Projects and Archive: not started. They will be added one phase at a time.

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
   dotnet ef migrations script --idempotent --project src/Modules/Access/AlMotaheda.Modules.Access -o access.sql
   dotnet ef database update --project src/Modules/Access/AlMotaheda.Modules.Access --connection "<the same connection string>"
   ```

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
- Apply migrations with the reviewed script or a migration bundle before starting a new version.
- Turn on `READ_COMMITTED_SNAPSHOT` for the database.
- Behind a reverse proxy on another machine, list it in `ForwardedHeaders__KnownProxies__0`.
- Serve the frontend and the API from the same site (for example `erp.example.com` and `erp.example.com/api`):
  the refresh cookie is `SameSite=Strict`.

## Documentation

- [Backend architecture](docs/architecture.md): structure, dependency rules, and how to add modules and features
- [Access module](src/Modules/Access/AlMotaheda.Modules.Access/README.md): sign-in, users, roles and permissions
- [Projects module](src/Modules/Projects/AlMotaheda.Modules.Projects/README.md)
- [Archive module](src/Modules/Archive/AlMotaheda.Modules.Archive/README.md)
