# AlMotaheda_ERP

Backend for a construction company's digital archive. Every construction project gets its own secure archive:
payment certificates, meeting minutes, site photos and videos, progress reports, contracts, invoices, technical drawings, and more.

## Status

**Architecture foundation only.** The solution structure, host, and infrastructure are in place.
No business features exist yet. They will be added one phase at a time.

## Technology

- ASP.NET Core Web API on .NET 8
- Modular monolith, with vertical slices inside each module
- Built-in structured logging, ProblemDetails error responses, health checks, Swagger
- xUnit tests, including architecture tests that enforce module boundaries

## Quick start

```bash
dotnet build AlMotahedaERP.slnx
dotnet test AlMotahedaERP.slnx
dotnet run --project src/Host/AlMotaheda.Api --launch-profile http
```

Then open:

- Swagger UI: http://localhost:5180/swagger
- Health: http://localhost:5180/health

Requires .NET SDK 9.0.200 or newer (for the `.slnx` file) and the ASP.NET Core 8 runtime.

## Documentation

- [Backend architecture](docs/architecture.md): structure, dependency rules, and how to add modules and features
- [Projects module](src/Modules/Projects/AlMotaheda.Modules.Projects/README.md)
- [Archive module](src/Modules/Archive/AlMotaheda.Modules.Archive/README.md)
