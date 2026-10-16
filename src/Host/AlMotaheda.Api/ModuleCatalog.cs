using AlMotaheda.BuildingBlocks.Modules;
using AlMotaheda.Modules.Access;
using AlMotaheda.Modules.Archive;
using AlMotaheda.Modules.Fleet;
using AlMotaheda.Modules.Platform;
using AlMotaheda.Modules.Projects;
using AlMotaheda.Modules.Subcontractors;
using AlMotaheda.Modules.Workforce;

namespace AlMotaheda.Api;

/// <summary>
/// The list of business modules that make up the application.
/// </summary>
/// <remarks>
/// To add a module: reference its project from AlMotaheda.Api.csproj and add one line here.
/// An architecture test fails if a module project is referenced but not listed here.
/// </remarks>
internal static class ModuleCatalog
{
    /// <summary>Creates a new instance of every module. Called once at startup.</summary>
    /// <remarks>
    /// The order is the migration order: Platform first (every save of every module writes its audit and outbox
    /// tables), then Access, then Projects, then the modules whose tables point to <c>projects</c> tables.
    /// </remarks>
    public static IReadOnlyList<IModule> CreateAll() =>
    [
        new PlatformModule(),
        new AccessModule(),
        new ProjectsModule(),
        new WorkforceModule(),
        new SubcontractorsModule(),
        new FleetModule(),
        new ArchiveModule(),
    ];
}
