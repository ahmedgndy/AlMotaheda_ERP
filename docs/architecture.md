# Backend architecture

This document explains how the AlMotaheda ERP backend is organised, why, and the rules for adding to it.

## 1. Summary

- **One deployable application.** A single ASP.NET Core Web API on .NET 10 (long-term support until November 2028). It is a modular monolith, not microservices.
- **Split into business modules.** Each module owns one business area. Each module is its own project, with explicit boundaries.
- **Organised by feature.** Inside a module, code is grouped by use case (vertical slices), not by technical layer.
- **Boundaries are enforced by tests**, not only by convention.

**Current status:** the foundation, sign-in, users, roles and permissions (the Access module), and projects with
their teams and project-level access (the Projects module), all stored in SQL Server through EF Core.
Archive is still an empty shell.

## 2. Solution layout

```
AlMotaheda_ERP/
├── AlMotahedaERP.slnx                  Solution file
├── Directory.Build.props               Shared build settings (net10.0, nullable, implicit usings)
├── Directory.Packages.props            Every NuGet version, declared once
├── .editorconfig                       Code style
├── docs/architecture.md                This document
├── src/
│   ├── Host/
│   │   └── AlMotaheda.Api/             The deployable application (composition root)
│   ├── BuildingBlocks/
│   │   └── AlMotaheda.BuildingBlocks/  Technical code shared by modules (no business logic)
│   └── Modules/
│       ├── Access/
│       │   ├── AlMotaheda.Modules.Access/
│       │   └── AlMotaheda.Modules.Access.Contracts/
│       ├── Projects/
│       │   └── AlMotaheda.Modules.Projects/
│       └── Archive/
│           └── AlMotaheda.Modules.Archive/
└── tests/
    ├── AlMotaheda.Api.Tests/           Host-level tests and architecture rules
    └── Modules/
        ├── AlMotaheda.Modules.Access.Tests/
        └── AlMotaheda.Modules.Projects.Tests/
```

## 3. What each project is for

| Project | Purpose | May reference |
|---|---|---|
| `AlMotaheda.Api` | The application that gets deployed. It wires up infrastructure (logging, errors, health checks, API docs) and plugs in the modules. No business logic. | BuildingBlocks, all modules |
| `AlMotaheda.BuildingBlocks` | Technical, business-neutral code that every module needs: the `IModule` contract and module registration, the permission check (`RequirePermission`), the access-token claim names, and error responses with a `code` (`ApiProblem`). | .NET framework only |
| `AlMotaheda.Modules.Access` | Sign-in, users, roles and permissions. Issues the access tokens. See its README. | BuildingBlocks, other modules' `.Contracts` |
| `AlMotaheda.Modules.Access.Contracts` | What other modules may ask Access: user names, roles and status (`IUserDirectory`), and "is this caller still an active Executive?" (`IExecutiveCheck`). Interfaces and records only. | Nothing |
| `AlMotaheda.Modules.Projects` | Construction projects, their teams (`ProjectMember`), and project-level access: who may open which project. See its README. | BuildingBlocks, Access.Contracts |
| `AlMotaheda.Modules.Archive` | The documents and media of each project: categories, upload, storage, search, download, preview. | BuildingBlocks, other modules' `.Contracts` |
| `AlMotaheda.Api.Tests` | Tests the host pipeline over real HTTP, plus the architecture rules. | Api (and through it, everything) |
| `AlMotaheda.Modules.Access.Tests` | The Access module's unit tests, API tests, and SQL Server tests (Testcontainers). | Api, Access |
| `AlMotaheda.Modules.Projects.Tests` | The Projects module's unit tests, API tests (including every query's SQL translation without a database), and SQL Server tests. Reuses the Access test helpers. | Api, Projects, Access |

**Why a project per module, not just a folder?** With separate projects, the compiler enforces the boundary.
Anything marked `internal` in a module is invisible to every other module. Folders cannot do that.

**Why so few projects?** Every project must earn its place. There are no empty Domain / Application / Infrastructure
layer projects per module. Feature code lives inside the module project (see section 6).
A `.Contracts` project is created only when another module actually needs something.

## 4. Dependency rules

```
                        ┌──────────────────┐
                        │  AlMotaheda.Api  │   composition root
                        └────────┬─────────┘
          ┌──────────────────────┼──────────────────────┐
          ▼                      ▼                      ▼
 ┌─────────────────┐   ┌─────────────────┐   ┌─────────────────┐
 │  Access module  │   │ Projects module │   │ Archive module  │   no direct references between modules
 └────────┬────────┘   └────────┬────────┘   └────────┬────────┘
          └──────────────────────┼──────────────────────┘
                                 ▼
                       ┌───────────────────┐
                       │  BuildingBlocks   │
                       └───────────────────┘
```

1. The host references modules. Modules never reference the host.
2. A module never references another module's implementation project.
   If Archive needs something from Projects, Projects publishes a small `AlMotaheda.Modules.Projects.Contracts`
   project (public interfaces and DTOs only), and Archive references that.
3. Inside a module, everything is `internal` except the module class itself.
4. BuildingBlocks never references modules or the host, and holds no business logic.
5. There are no circular references. This follows from rules 1 to 4.
6. Every module the host references is listed in `ModuleCatalog`.
7. Infrastructure vendors stay in the host. Modules use framework abstractions directly
   (`ILogger`, `HybridCache`, `IHubContext`, `ActivitySource`, `Meter`). Vendor-specific setup
   (Redis, telemetry exporters, SignalR backplanes, cloud SDKs) is configured only in the host.

Rules 1 to 6 are checked by `tests/AlMotaheda.Api.Tests/Architecture/ModuleBoundaryTests.cs`.
The tests find module assemblies automatically, so a new module is checked without editing the tests.
They were verified to fail when a rule is broken. Rule 7 is checked in code review.

How rule 7 applies to the database and sign-in: a module that owns tables references the EF Core SQL Server
provider itself, because its `DbContext` and migrations are part of the module. The Access module also registers
the JWT bearer scheme, because it issues the tokens and must decide how they are checked. Both are part of the
framework, not infrastructure vendors like Redis or telemetry exporters. The host still owns everything about the
environment: the connection string, rate limits, forwarded headers and the database health check.

**Data rule:** each module owns its own tables, in its own database schema (Access uses `access`).
No module reads another module's tables. Modules exchange data only through contracts.

## 5. How the host works

### Startup

`Program.cs` calls two methods in `ApiHost.cs`:

- `AddApiHost()` registers the infrastructure services, then calls `RegisterServices` on every module.
- `UseApiHost()` builds the request pipeline, then calls `MapEndpoints` on every module.

The integration tests call the same two methods. So the tests always run the production pipeline.

### Request pipeline, in order

1. **Forwarded headers**: behind a trusted proxy, restores the real client address and scheme.
2. **Request logging**: one structured log entry per request.
3. **Exception handler**: unhandled exceptions become ProblemDetails responses.
4. **Status code pages**: empty error responses (404, 405, ...) become ProblemDetails responses.
5. **Swagger**: only when enabled.
6. **Authentication**: reads the `Authorization: Bearer` access token.
7. **Rate limiter**: per-client budgets for sign-in and token refresh.
8. **Authorization**: every `/api` endpoint needs a signed-in user unless it allows anonymous access.
9. **Endpoints**: health checks, then module endpoints under `/api`.

Before step 1, .NET adds routing automatically. Health probes rely on that to skip request logging.
If you ever call `app.UseRouting()` yourself (for example when adding authentication or CORS),
call it before request logging, or probe requests will be logged again.

### The module contract

```csharp
public interface IModule
{
    string Name { get; }
    void RegisterServices(IServiceCollection services, IConfiguration configuration);
    void MapEndpoints(IEndpointRouteBuilder endpoints);
}
```

- Modules are listed explicitly in `ModuleCatalog.cs`. An explicit list is easier to read than assembly scanning and needs no reflection at startup.
- `MapModules()` gives every module the shared `/api` route group, and returns that group.
  The host calls `.RequireAuthorization()` on it, so every module endpoint needs a signed-in user by default.
  An endpoint opts out with `.AllowAnonymous()` (only sign-in, refresh and sign-out do).
- Two modules with the same name stop the application at startup.
- At startup, the log shows `Module <Name> loaded` for each module.

### Dependency injection conventions

- Each module registers its own services in its `RegisterServices`. The host never registers a module's internals.
- Register the concrete class. Add an interface only for a real reason: a second implementation,
  a cross-module contract, or an external boundary you must replace in tests (file storage, the clock, email).
- Lifetimes: `Scoped` for anything that uses the current request or the database. `Singleton` only for stateless, thread-safe services.
- Settings: bind a module's configuration section to an options class and call `ValidateOnStart()`.
  Then a bad setting stops the application at startup, not on the first request.
- Lifetimes are checked in tests: `TestApplication` turns on `ValidateScopes` and `ValidateOnBuild`,
  so a singleton that captures a scoped service (such as a database context) fails the tests.

## 6. Vertical slice conventions

One use case = one slice = one folder. A slice holds everything for that use case:
endpoint, request and response models, validation, business logic, and data access.
Its tests live in the module's test project, in a folder with the same name.

```
AlMotaheda.Modules.Archive/
├── ArchiveModule.cs
├── Features/
│   ├── UploadDocument/
│   │   └── UploadDocument.cs
│   ├── SearchDocuments/
│   │   └── SearchDocuments.cs
│   └── DownloadDocument/
│       └── DownloadDocument.cs
└── Data/                         (added in the database phase)
```

### Slice template

This is an illustration only. It is not in the code base.

```csharp
namespace AlMotaheda.Modules.Projects.Features.CreateProject;

internal static class CreateProject
{
    internal sealed record Request(string Name);

    internal sealed record Response(Guid Id, string Name);

    public static void MapEndpoint(IEndpointRouteBuilder group) =>
        group.MapPost("/", HandleAsync)
            .WithName(nameof(CreateProject))
            .Produces<Response>(StatusCodes.Status201Created)
            .ProducesValidationProblem();

    private static async Task<IResult> HandleAsync(
        Request request,
        /* module services, injected by the framework */
        CancellationToken cancellationToken)
    {
        // 1. Validate the request.
        // 2. Apply the business rules.
        // 3. Save.
        // 4. Return a typed result.
    }
}
```

The module maps it:

```csharp
public void MapEndpoints(IEndpointRouteBuilder endpoints)
{
    var projects = endpoints.MapGroup("/projects").WithTags(Name);
    CreateProject.MapEndpoint(projects);
}
```

### Rules for slices

- **Name** a slice after its use case, verb first: `CreateProject`, `UploadDocument`, `GetDocumentById`.
  Names must be unique across the API, because they also become Swagger schema names (`CreateProjectRequest`).
- **Visibility:** everything in a slice is `internal`.
- **Independence:** slices do not call each other. If several slices in one module truly share logic,
  move it into a well-named class in that module, and only once it is really shared.
- **Size:** keep a slice in one file while it is small. When it grows, split it into several files in the same folder.
- **Failures:** expected failures (validation error, not found, forbidden) are returned as results with a `code`,
  through `ApiProblem` in BuildingBlocks (`ApiProblem.Validation(errors)`, `ApiProblem.NotFoundResult()`).
  Exceptions are only for the unexpected.
- **Validation:** each slice validates its own request explicitly and returns every failing field at once.
  The server is the authority; the frontend's checks are only for convenience.
- **Cancellation:** handlers accept a `CancellationToken` and pass it on.
- **No mediator framework:** no MediatR or CQRS library. An endpoint calling a handler method is enough.
  A library is added only for a clear, concrete need.

## 7. Cross-cutting behaviour

### Configuration and environments

| File | Used in | Contents |
|---|---|---|
| `appsettings.json` | All environments | Defaults: log levels, readable console logs, Swagger off, token issuer and audience, rate limits |
| `appsettings.Development.json` | Development | More detailed logs |
| `appsettings.Production.json` | Production | JSON logs, one object per line |

- The environment comes from `ASPNETCORE_ENVIRONMENT` (`Development`, `Staging`, `Production`).
- Environment variables override files. Use `__` for nesting, for example `ApiDocumentation__Enabled=true`.
- **Secrets never go into appsettings files in git.** Locally, use User Secrets (the host already has a `UserSecretsId`;
  run `dotnet user-secrets set ... --project src/Host/AlMotaheda.Api`). On servers, use environment variables or a secret store.
- Required settings, checked at startup: `ConnectionStrings:Erp` and `Access:Tokens:SigningKey`.
  The API refuses to start without them. See the README for the exact commands.

### Logging

- Code logs through the built-in `ILogger<T>`.
- Always use message templates with named placeholders, never string interpolation:
  `logger.LogInformation("Document {DocumentId} uploaded to project {ProjectId}", documentId, projectId);`
  Placeholders become searchable fields in the JSON logs.
- Every request is logged once: method, path, status code, and duration.
  Headers, query strings, and bodies are never logged, because they can hold tokens, personal data, or file content.
- Health probes are not logged, so they don't bury real traffic.
- In Production, every log entry written during a request carries a `TraceId` (the JSON logs include scopes).
  Development logs are single-line text without it. Error responses return the same value as `traceId`,
  so a user's error report can be matched to its logs.
- **A slow log output must never stall requests.** In Production, the console logger drops messages when its
  queue is full (`QueueFullMode: DropWrite`) instead of making requests wait. In load tests, the default (`Wait`)
  cut throughput from about 27,000 to about 600 requests per second when output was slow.
  The trade-off: during a backlog, some log lines are lost.
- Logging every request has a cost: about 40% of peak throughput on a trivial endpoint in load tests.
  Once OpenTelemetry tracing exists, log only failed or slow requests in Production.
- **Why not Serilog now?** The built-in logger already gives structured logs, with no extra package.
  All code logs through `ILogger<T>`, so Serilog (file or Seq sinks) or OpenTelemetry can be added later in the host only,
  once the hosting environment is known. No feature code would change.

### Error responses

Every error uses the RFC 9457 ProblemDetails format (`application/problem+json`):

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.6.1",
  "title": "An error occurred while processing your request.",
  "status": 500,
  "instance": "/api/projects",
  "traceId": "1bdb218431014c6084ad30fd544d910c",
  "code": "server_error"
}
```

- `code` is machine-readable, so the frontend can show its own Arabic or English message. Features set specific
  codes (`email_taken`, `concurrency_conflict`, ...). Errors the framework produces get one from their status
  (`validation_failed`, `unauthenticated`, `forbidden`, `not_found`, `too_many_requests`, `server_error`).
- Validation errors (400) also list each failing field under `errors`.
- `detail` (the exception message) appears only in Development. It can reveal internals.
- `traceId` is the bare 32-character trace id, the same value as the logs' `TraceId` field.
- Unhandled exceptions are logged exactly once, by `GlobalExceptionHandler`: with a stack trace for server errors (5xx),
  as a short warning for client errors (4xx). Since .NET 10, the framework's middleware doesn't log an exception
  that a handler has handled, so there are no duplicates.

### Health checks

- `GET /health/live`: the process is up. Runs no checks. Use it for liveness probes.
- `GET /health`: runs every registered check and returns 503 if any check is unhealthy. Use it for readiness and monitoring.
  Today it checks the database (`SELECT 1`, 5-second timeout). File-storage checks come with the Archive module.
- Both endpoints are anonymous, and stay anonymous after authentication is added.
- Check descriptions appear only in Development. When a check throws, .NET puts the exception message
  in its description, which can reveal connection strings. Elsewhere, the response shows each check's name,
  status and duration.
- Register optional dependencies (a cache, for example) with `failureStatus: HealthStatus.Degraded`.
  Degraded still returns 200, so their outage doesn't take servers out of the load balancer. Give every check a timeout.

### API documentation

- Swagger UI is at `/swagger`. The OpenAPI JSON is at `/swagger/v1/swagger.json`.
- It is on automatically in Development. In other environments, set `ApiDocumentation:Enabled` to `true`.
- It uses Swashbuckle 9.0.x. Only the generator and UI packages are referenced; the meta-package would add
  a build step that starts the app after every build. .NET 10 also has a built-in OpenAPI generator
  (`Microsoft.AspNetCore.OpenApi`), and switching to it is optional. Don't move to Swashbuckle 10 casually:
  it uses Microsoft.OpenApi 2.x, which changes the namespaces used in `ApiDocumentationSetup.cs`.

### Memory

The app uses .NET's default server garbage collector. Since .NET 9, it runs in DATAS mode, which sizes the heap
to the workload instead of growing it per CPU core. In load tests on .NET 8, classic Server GC kept about 400 MB
after load; DATAS kept about 78 MB at the same throughput.

### Caching (when it is added)

No caching exists yet. When a feature shows a measured need, follow these rules:

- **One cache API:** `HybridCache`, registered in the host with `AddHybridCache()`. Add Redis only as its
  second level, once you run two or more servers.
- **Keys:** `{module}:{entity}:{id}:v{n}`. Only the owning module reads, writes and invalidates its keys.
  Bump the version suffix when the cached shape changes.
- **Expiry:** every entry has an absolute expiry, and the local expiry is shorter than the shared one.
- **Invalidation:** on every write, by key or by tag.
- **Memory:** never cache file contents (photos, videos, PDFs) in memory.
- **Authorization:** cache data, not access decisions. Run the project permission check on every request,
  even when the data comes from cache. Keys for user- or project-specific data include that user or project.
- **Consistency:** cache only data where a short delay is harmless.
- **Failure:** the app must work, only slower, without the cache. A cache health check reports Degraded, not Unhealthy.
- **Not for project data:** output caching (whole responses), because responses depend on the user and the project.

### Database

- **One SQL Server database, one schema per module.** The connection string is `ConnectionStrings:Erp`.
- **Each module has its own `DbContext` and migrations**, with its own migration history table in its schema
  (`access.__EFMigrationsHistory`, `projects.__EFMigrationsHistory`).
- **Foreign keys between schemas** are allowed only towards the modules the ERD names as shared (Access, Projects,
  Finance), and only for rows that are never deleted. Today there is one: `projects.ProjectMember.UserId` →
  `access.User`. EF can't model a key into another module's context, so it is written by hand in the migration.
  It makes the order matter: **apply the Access migrations before the Projects migrations.** At startup, modules
  migrate in `ModuleCatalog` order (Access first; a host test checks it), and Projects stops with a clear message
  if `access.User` is missing.
- **Migrations are internal classes.** `dotnet ef` generates them as `public`; change them to `internal`,
  or the module-boundary test fails.
- **Creating a migration** (from the repository root):
  `dotnet tool restore`, then
  `dotnet ef migrations add <Name> --project src/Modules/<Module>/AlMotaheda.Modules.<Module> --output-dir Data/Migrations`.
- **Applying migrations:**
  - Review the SQL first, one script per module, and run them in this order:
    `dotnet ef migrations script --idempotent --project src/Modules/Access/AlMotaheda.Modules.Access -o access.sql`, then
    `dotnet ef migrations script --idempotent --project src/Modules/Projects/AlMotaheda.Modules.Projects -o projects.sql`.
  - On a server, run that script, or a migration bundle (`dotnet ef migrations bundle`), before the new version starts.
  - In development you can set `Database:ApplyMigrationsOnStartup` to `true`. It is `false` by default, so the
    application never changes a schema nobody reviewed.
- **Turn on `READ_COMMITTED_SNAPSHOT`** on the database (`ALTER DATABASE AlMotahedaErp SET READ_COMMITTED_SNAPSHOT ON`).
  Readers then don't wait for writers, which removes most blocking between sign-ins and user changes.
  Azure SQL has it on by default; a self-hosted SQL Server doesn't.
- EF Core's own error logs are switched off for each module's context: the host's exception handler logs each failure
  once, and EF's "save failed" entry would copy duplicate key values (emails, project codes) into the logs.

### Authentication and authorization

The Access module issues the tokens; every module checks them through BuildingBlocks.

- **Access token:** a JWT (HS256) that lasts 15 minutes, sent as `Authorization: Bearer`. It carries the user id (`sub`),
  role (`role`), sign-in id (`sid`) and one `perm` claim per module, such as `projects:VCEF`
  (V = View, C = Create, E = Edit, A = Approve, F = See money).
- **Checking a permission in any module:**
  ```csharp
  group.MapGet("/", ListProjects.HandleAsync).RequirePermission("projects", PermissionAction.View);
  ```
  In a handler: `httpContext.User.HasPermission("projects", PermissionAction.Edit)` and `httpContext.User.GetUserId()`.
- **Building your own policy:** start from `PermissionAuthorization.SignedInPolicy()`. An endpoint with its own
  policy object doesn't get the default policy, so a policy built from scratch would let a user who still has
  to change their password call the endpoint. A test checks every `/api` endpoint for this.
- **Freshness:** permissions in the token can be up to 15 minutes old. That is fine for reading and ordinary work.
  Anything that grants or removes access must re-check the caller in the database; the Access module does this
  for every user-management change.
- **Project-level access** (which projects a user may open) belongs to the Projects module:
  - Executive and Accountant see every project. Everyone else sees only projects where they have an active
    `ProjectMember` row. Membership is read from the database on every request, never from the token.
  - A project outside the caller's scope answers 404, exactly like a project that doesn't exist.
  - Membership is access, so only an Executive changes it, with the same database re-check as user management
    (through `IExecutiveCheck` in `Access.Contracts`).
  - Inside the module, `ProjectAccess` is the one place these rules live. When another module needs them
    (Archive is next), they will be published in `AlMotaheda.Modules.Projects.Contracts`.

### Security defaults already in place

- No `Server` response header.
- No exception details outside Development.
- No headers, query strings, or bodies in request logs.
- No exception details or check descriptions in health responses outside Development.
- Every `/api` endpoint needs a signed-in user unless it explicitly allows anonymous access.
- Passwords are stored only as PBKDF2 hashes; refresh tokens only as SHA-256 hashes. Neither is ever logged.
- Sign-in and token refresh are rate-limited per client address. Behind a proxy on another machine, list the
  proxy in `ForwardedHeaders:KnownProxies`; otherwise all clients share one budget. The API logs a warning
  the first time it ignores an `X-Forwarded-For` header from an untrusted address.

## 8. Adding a new module

1. Create `src/Modules/<Name>/AlMotaheda.Modules.<Name>/`. Copy an existing module's `.csproj`
   (framework reference + BuildingBlocks reference only).
2. Add `public sealed class <Name>Module : IModule`. Keep everything else `internal`.
3. Reference the project from `AlMotaheda.Api.csproj`, and add `new <Name>Module()` to `ModuleCatalog`.
4. Add the project to `AlMotahedaERP.slnx` (your IDE, or `dotnet sln AlMotahedaERP.slnx add <path>`).
5. Run `dotnet test`. The architecture tests check the new module automatically.

## 9. Adding a new feature (vertical slice)

1. Agree on the requirement and the data it needs.
2. Create `Features/<UseCase>/` in the module that owns the use case.
3. Write the slice: endpoint, request/response, validation, logic, data access.
4. Map the endpoint in the module's `MapEndpoints`. Register any new services in its `RegisterServices`.
5. Add tests in the module's test project. The first time, create `tests/Modules/AlMotaheda.Modules.<Name>.Tests`
   and add `<InternalsVisibleTo Include="AlMotaheda.Modules.<Name>.Tests" />` to the module's `.csproj`.
   `tests/Modules/AlMotaheda.Modules.Access.Tests` is the example to copy, including its SQL Server test setup.
6. Run `dotnet build` and `dotnet test`, then try the endpoint in Swagger.

## 10. Decisions intentionally left open

These are decided phase by phase, when the requirements are provided.

| Topic | When | Current plan |
|---|---|---|
| Database and ORM | **Decided** (Access module) | SQL Server with EF Core 10: one DbContext per module, each in its own schema, with its own migrations. |
| Authentication and users | **Decided** (Access module) | JWT access tokens (15 min) plus rotating refresh tokens in an HttpOnly cookie. The `/api` group requires a signed-in user. |
| Project-level authorization | **Decided** (Projects module) | Role scope (company or assigned) plus active `ProjectMember` rows, checked in SQL on every request. Other modules will ask through `Projects.Contracts`, created when Archive needs it. |
| Validation approach | **Decided** (Access module) | Explicit validation in each slice, collecting every failing field, returned as a 400 with `errors` and a `code`. |
| Audit log table | Next small feature | User and permission changes are written to structured security logs until then. |
| File storage | Archive upload phase | Local disk or cloud storage; size limits (Kestrel, IIS and multipart); file type checks. Shared storage once there are two or more servers. |
| Observability | First feature | OpenTelemetry tracing and metrics, with an OTLP exporter that stays idle until configured. |
| HTTPS, reverse proxy, CORS | When the hosting target is known | Forwarded headers are in place (configure `ForwardedHeaders:KnownProxies`). HTTPS redirection, HSTS and CORS depend on where the API and frontend run; the sign-in cookie assumes they share a site. |

## 11. Build, run, and test

```bash
dotnet build AlMotahedaERP.slnx
dotnet test AlMotahedaERP.slnx
dotnet run --project src/Host/AlMotaheda.Api --launch-profile https
# then open https://localhost:7180/swagger and https://localhost:7180/health
```

Use the `https` profile when you test sign-in in a browser: the refresh cookie is `Secure`, and some browsers
drop it over plain HTTP.

Requirements:

- **.NET 10 SDK.** It builds the `net10.0` projects and reads the `.slnx` solution file.
  A recent Visual Studio, Rider, or VS Code with C# Dev Kit also opens it.
- Running the app needs the **ASP.NET Core 10 runtime**, which the SDK includes. Servers need it installed.

About the integration tests: `TestApplication` starts the real host on a free local port, using the same `ApiHost`
methods as `Program.cs`, and tests call it over real HTTP. This needs no extra test package.
It supplies a test signing key and a connection string that points nowhere, so tests that don't need a database
never touch one.

The Projects API tests also run every endpoint's queries through EF Core's SQL Server translation against a stand-in
database that answers with no rows (`StandInDatabase`), so a query EF can't translate fails without SQL Server.

The SQL Server tests (`[DatabaseFact]`, in the Access and Projects test projects) start SQL Server 2022 in Docker with Testcontainers,
and give each test its own new database, created by the API's own startup migration. They need Docker running;
without it they are reported as skipped, with the reason. On a Mac with Apple Silicon, turn on
"Use Rosetta for x86_64/amd64 emulation" in Docker Desktop.
