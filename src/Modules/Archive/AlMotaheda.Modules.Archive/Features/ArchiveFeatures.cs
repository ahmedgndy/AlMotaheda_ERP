using AlMotaheda.Modules.Archive.Common;
using AlMotaheda.Modules.Archive.Features.Documents;
using AlMotaheda.Modules.Archive.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Archive.Features;

/// <summary>
/// Wires the Archive module's features: one line per use case. See docs/project-workspace/06-archive.md.
/// </summary>
internal static class ArchiveFeatures
{
    public static void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ArchiveStorageOptions>().Bind(configuration.GetSection(ArchiveStorageOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IPostConfigureOptions<ArchiveStorageOptions>, ArchiveStorageOptionsSetup>();
        services.AddSingleton<IValidateOptions<ArchiveStorageOptions>, ArchiveStorageOptionsValidator>();

        services.AddOptions<ArchiveLimitsOptions>().Bind(configuration.GetSection(ArchiveLimitsOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ArchiveLimitsOptions>, ArchiveLimitsOptionsValidator>();

        // Stateless (a folder path and the clock), so one instance serves every request.
        services.AddSingleton<IFileStore, LocalDiskFileStore>();
        services.AddSingleton<ArchiveLog>();
        services.AddSingleton<ArchiveCalendar>();

        services.AddHealthChecks()
            .AddCheck<ArchiveStorageHealthCheck>(ArchiveStorageHealthCheck.Name, timeout: ArchiveStorageHealthCheck.Timeout);
    }

    /// <param name="api">The shared <c>/api</c> group. Routes under one project start with <c>/projects/{projectId:int}</c>.</param>
    public static void Map(IEndpointRouteBuilder api)
    {
        var projects = api.MapGroup("/projects");

        ListDocuments.Map(projects);
        GetDocument.Map(projects);
        GetDocumentContent.Map(projects);
        UploadDocument.Map(projects);
        UpdateDocument.Map(projects);
        ReplaceDocumentLinks.Map(projects);
        DeleteDocument.Map(projects);

        // The supplier profile's documents: links only, never a copy (docs/cost-flow §2.5).
        ListSupplierDocuments.Map(api);
    }
}
