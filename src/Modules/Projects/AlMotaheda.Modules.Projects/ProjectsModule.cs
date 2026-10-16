using AlMotaheda.BuildingBlocks.Modules;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Procurement.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features;
using AlMotaheda.Modules.Projects.Features.Inbox;
using AlMotaheda.Modules.Projects.Features.Members;
using AlMotaheda.Modules.Projects.Features.Projects;
using AlMotaheda.Modules.Projects.Integration;
using AlMotaheda.Modules.Projects.Security;
using AlMotaheda.Modules.Projects.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Projects;

/// <summary>
/// Entry point of the Projects module: construction projects, who is on each project, and who may open which
/// project. This is the only public type in the module.
/// </summary>
/// <remarks>
/// <para>It uses the Access module only through <c>AlMotaheda.Modules.Access.Contracts</c>: user names and roles
/// (<c>IUserDirectory</c>) and the database check that a caller is still an active Executive (<c>IExecutiveCheck</c>).</para>
/// <para>Other modules use it only through <c>AlMotaheda.Modules.Projects.Contracts</c>. Labor and subcontract
/// figures on the project items screens come back the same way, from the modules that own those records.</para>
/// </remarks>
public sealed class ProjectsModule : IModule
{
    public string Name => "Projects";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ProjectsDbContext>(configuration, ProjectsDatabase.Configure);

        services.AddOptions<ProjectsOptions>().Bind(configuration.GetSection(ProjectsOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ProjectsOptions>, ProjectsOptionsValidator>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<BusinessCalendar>();
        services.AddSingleton<ProjectsLog>();
        services.AddScoped<ProjectAccess>();
        services.AddScoped<ExecutiveGate>();

        // The project workspace: shared pieces first, then each feature area's own services.
        services.AddCompanyClock();
        services.AddScoped<WorkspaceAccess>();
        services.AddScoped<IProjectAccess, ProjectAccessContract>();
        services.AddScoped<IVisibleProjects, VisibleProjectsContract>();
        services.AddScoped<IPaymentSource, SupplierPaymentSource>();
        services.AddScoped<IProjectItems, ProjectItemsContract>();
        services.AddScoped<ISupplierDirectory, SupplierDirectoryContract>();
        services.AddScoped<IProjectCatalog, ProjectCatalogContract>();
        services.AddScoped<IInvoicePosting, InvoicePostingContract>();
        services.AddScoped<AlMotaheda.Modules.Reports.Contracts.IReportSource, ProjectReports>();
        services.AddScoped<IProjectRecordOwnership, ProjectRecordOwnership>();
        CoreFeatures.AddServices(services);
        PlanningFeatures.AddServices(services);
        ReportingFeatures.AddServices(services);
        services.AddScoped<IWorkInboxSource, ProjectsInboxSource>();

        services.AddHostedService<ProjectsStartup>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Every projects endpoint needs a signed-in user, whatever the host adds.
        var projects = endpoints.MapGroup("/projects").WithTags(Name).RequireAuthorization();

        ListProjects.Map(projects);
        GetProject.Map(projects);
        CreateProject.Map(projects);
        UpdateProject.Map(projects);

        ListProjectMembers.Map(projects);
        SetProjectManager.Map(projects);
        AddProjectMember.Map(projects);
        EndProjectMember.Map(projects);
        ListUserProjects.Map(projects);
        ListAssignableUsers.Map(projects);

        // The project workspace. Company-wide routes (such as /suppliers) hang off the shared /api group.
        var api = endpoints.MapGroup("").WithTags(Name).RequireAuthorization();
        CoreFeatures.Map(api, projects);
        PlanningFeatures.Map(api, projects);
        ReportingFeatures.Map(projects);
        GetWorkInbox.Map(api);
    }
}
