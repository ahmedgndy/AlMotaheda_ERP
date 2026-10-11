using AlMotaheda.BuildingBlocks.Modules;
using AlMotaheda.Modules.Archive;
using AlMotaheda.Modules.Projects;

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
    public static IReadOnlyList<IModule> All =>
    [
        new ProjectsModule(),
        new ArchiveModule(),
    ];
}
