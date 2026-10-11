# Backend architecture

This document explains how the AlMotaheda ERP backend is organised, why, and the rules for adding to it.

## 1. Summary

- **One deployable application.** A single ASP.NET Core Web API on .NET 8. It is a modular monolith, not microservices.
- **Split into business modules.** Each module owns one business area. Each module is its own project, with explicit boundaries.
- **Organised by feature.** Inside a module, code is grouped by use case (vertical slices), not by technical layer.
- **Boundaries are enforced by tests**, not only by convention.

**Current status:** foundation only. There are no business features, entities, database, or authentication yet.

## 2. Solution layout

```
AlMotaheda_ERP/
├── AlMotahedaERP.slnx                  Solution file
├── Directory.Build.props               Shared build settings (net8.0, nullable, implicit usings)
├── Directory.Packages.props            Every NuGet version, declared once
├── .editorconfig                       Code style
├── docs/architecture.md                This document
├── src/
│   ├── Host/
│   │   └── AlMotaheda.Api/             The deployable application (composition root)
│   ├── BuildingBlocks/
│   │   └── AlMotaheda.BuildingBlocks/  Technical code shared by modules (no business logic)
│   └── Modules/
│       ├── Projects/
│       │   └── AlMotaheda.Modules.Projects/
│       └── Archive/
│           └── AlMotaheda.Modules.Archive/
└── tests/
    └── AlMotaheda.Api.Tests/           Host-level tests and architecture rules
```

## 3. What each project is for

| Project | Purpose | May reference |
|---|---|---|
| `AlMotaheda.Api` | The application that gets deployed. It wires up infrastructure (logging, errors, health checks, API docs) and plugs in the modules. No business logic. | BuildingBlocks, all modules |
| `AlMotaheda.BuildingBlocks` | Technical, business-neutral code that every module needs. Today: the `IModule` contract and module registration. | .NET framework only |
| `AlMotaheda.Modules.Projects` | Construction projects, and project-level access (who may open which project). | BuildingBlocks, other modules' `.Contracts` |
| `AlMotaheda.Modules.Archive` | The documents and media of each project: categories, upload, storage, search, download, preview. | BuildingBlocks, other modules' `.Contracts` |
| `AlMotaheda.Api.Tests` | Tests the host pipeline over real HTTP, plus the architecture rules. | Api (and through it, everything) |

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
             ┌─────────────┴─────────────┐
             ▼                           ▼
   ┌───────────────────┐       ┌───────────────────┐
   │  Projects module  │       │  Archive module   │   no direct references between modules
   └─────────┬─────────┘       └─────────┬─────────┘
             └─────────────┬─────────────┘
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

These rules are checked by `tests/AlMotaheda.Api.Tests/Architecture/ModuleBoundaryTests.cs`.
The tests find module assemblies automatically, so a new module is checked without editing the tests.
They were verified to fail when a rule is broken.

**Data rule (from the database phase on):** each module owns its own tables.
No module reads another module's tables. Modules exchange data only through contracts.

## 5. How the host works

### Startup

`Program.cs` calls two methods in `ApiHost.cs`:

- `AddApiHost()` registers the infrastructure services, then calls `RegisterServices` on every module.
- `UseApiHost()` builds the request pipeline, then calls `MapEndpoints` on every module.

The integration tests call the same two methods. So the tests always run the production pipeline.

### Request pipeline, in order

1. **Request logging**: one structured log entry per request.
2. **Exception handler**: unhandled exceptions become ProblemDetails responses.
3. **Status code pages**: empty error responses (404, 405, ...) become ProblemDetails responses.
4. **Swagger**: only when enabled.
5. **Endpoints**: health checks, then module endpoints under `/api`.

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
  When authentication is added, one call (`.RequireAuthorization()`) on that group will protect every module endpoint by default.
- Two modules with the same name stop the application at startup.
- At startup, the log shows `Module <Name> loaded` for each module.

### Dependency injection conventions

- Each module registers its own services in its `RegisterServices`. The host never registers a module's internals.
- Register the concrete class. Add an interface only for a real reason: a second implementation,
  a cross-module contract, or an external boundary you must replace in tests (file storage, the clock, email).
- Lifetimes: `Scoped` for anything that uses the current request or the database. `Singleton` only for stateless, thread-safe services.
- Settings: bind a module's configuration section to an options class and call `ValidateOnStart()`.
  Then a bad setting stops the application at startup, not on the first request.

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
- **Failures:** expected failures (validation error, not found, forbidden) are returned as results
  (`TypedResults.ValidationProblem(...)`, `TypedResults.NotFound()`). Exceptions are only for the unexpected.
- **Cancellation:** handlers accept a `CancellationToken` and pass it on.
- **No mediator framework:** no MediatR or CQRS library. An endpoint calling a handler method is enough.
  A library is added only for a clear, concrete need.

## 7. Cross-cutting behaviour

### Configuration and environments

| File | Used in | Contents |
|---|---|---|
| `appsettings.json` | All environments | Defaults: log levels, readable console logs, Swagger off |
| `appsettings.Development.json` | Development | More detailed logs |
| `appsettings.Production.json` | Production | JSON logs, one object per line |

- The environment comes from `ASPNETCORE_ENVIRONMENT` (`Development`, `Staging`, `Production`).
- Environment variables override files. Use `__` for nesting, for example `ApiDocumentation__Enabled=true`.
- **Secrets never go into appsettings files in git.** Locally, use User Secrets (`dotnet user-secrets init`, then `dotnet user-secrets set ...`).
  On servers, use environment variables or a secret store.

### Logging

- Code logs through the built-in `ILogger<T>`.
- Always use message templates with named placeholders, never string interpolation:
  `logger.LogInformation("Document {DocumentId} uploaded to project {ProjectId}", documentId, projectId);`
  Placeholders become searchable fields in the JSON logs.
- Every request is logged once: method, path, status code, and duration.
  Headers, query strings, and bodies are never logged, because they can hold tokens, personal data, or file content.
- Health probes are not logged, so they don't bury real traffic.
- Every log entry carries a `TraceId`. Error responses return the same value as `traceId`,
  so a user's error report can be matched to its logs.
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
  "traceId": "1bdb218431014c6084ad30fd544d910c"
}
```

- `detail` (the exception message) appears only in Development. It can reveal internals.
- Unhandled exceptions are logged exactly once, by `GlobalExceptionHandler`: with a stack trace for server errors (5xx),
  as a short warning for client errors (4xx). The framework's own duplicate log is switched off in `appsettings.json`.

### Health checks

- `GET /health/live`: the process is up. Runs no checks. Use it for liveness probes.
- `GET /health`: runs every registered check and returns 503 if any check is unhealthy. Use it for readiness and monitoring.
  Database and file-storage checks will be added here as those parts are built.
- Both endpoints are anonymous, and stay anonymous after authentication is added.

### API documentation

- Swagger UI is at `/swagger`. The OpenAPI JSON is at `/swagger/v1/swagger.json`.
- It is on automatically in Development. In other environments, set `ApiDocumentation:Enabled` to `true`.
- It uses Swashbuckle because .NET 8 has no built-in OpenAPI document generator (that arrived in .NET 9).
  Only the generator and UI packages are referenced. The meta-package would add a build step that starts the app after every build.

### Security defaults already in place

- No `Server` response header.
- No exception details outside Development.
- No headers, query strings, or bodies in request logs.
- No exception details in health responses.

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
6. Run `dotnet build` and `dotnet test`, then try the endpoint in Swagger.

## 10. Decisions intentionally left open

These are decided phase by phase, when the requirements are provided.

| Topic | When | Current plan |
|---|---|---|
| Database and ORM | First feature that stores data | One DbContext per module, each in its own database schema, with its own migrations. |
| Authentication and users | Authentication phase | Likely a Users/Identity module. The `/api` group gets `RequireAuthorization()`, so endpoints are protected by default. |
| Project-level authorization | After authentication | The Projects module answers "may user X access project Y?" through `Projects.Contracts`. |
| Validation approach | First feature with input | Built-in validation with an endpoint filter, or FluentValidation. |
| File storage | Archive upload phase | Local disk or cloud storage; size limits; file type checks. |
| HTTPS, reverse proxy, CORS | When the hosting target is known | HTTPS redirection, HSTS, forwarded headers, and CORS for the frontend depend on where the API runs. |

## 11. Build, run, and test

```bash
dotnet build AlMotahedaERP.slnx
dotnet test AlMotahedaERP.slnx
dotnet run --project src/Host/AlMotaheda.Api --launch-profile http
# then open http://localhost:5180/swagger and http://localhost:5180/health
```

Requirements:

- .NET SDK 9.0.200 or newer to use the `.slnx` solution file from the command line (any newer SDK works).
  A recent Visual Studio, Rider, or VS Code with C# Dev Kit also opens it.
- The projects target .NET 8, so running the app and the tests needs the **ASP.NET Core 8 runtime**.

About the integration tests: `TestApplication` starts the real host on a free local port, using the same `ApiHost`
methods as `Program.cs`, and tests call it over real HTTP. This needs no extra test package.
