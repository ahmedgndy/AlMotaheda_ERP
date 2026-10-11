using System.Reflection;
using System.Runtime.Loader;
using AlMotaheda.BuildingBlocks.Modules;

namespace AlMotaheda.Api.Tests.Architecture;

/// <summary>
/// Enforces the dependency rules described in docs/architecture.md.
/// </summary>
/// <remarks>
/// Module assemblies are discovered from the test output folder, so every new module is checked
/// automatically as soon as the API host references it. Reference checks see the assemblies a module
/// actually uses in compiled code.
/// </remarks>
public sealed class ModuleBoundaryTests
{
    private const string ModulePrefix = "AlMotaheda.Modules.";
    private const string ContractsSuffix = ".Contracts";
    private const string HostAssemblyName = "AlMotaheda.Api";

    private static readonly Assembly[] ModuleAssemblies = LoadModuleAssemblies();

    [Fact]
    public void Module_assemblies_are_discovered()
    {
        // Guards the other tests: if discovery silently found nothing, they would all pass vacuously.
        Assert.NotEmpty(ModuleAssemblies);
    }

    [Fact]
    public void Modules_do_not_depend_on_other_modules_except_their_contracts()
    {
        var violations = ModuleAssemblies
            .SelectMany(module => ReferencedNames(module)
                .Where(name => name.StartsWith(ModulePrefix, StringComparison.Ordinal)
                               && !name.EndsWith(ContractsSuffix, StringComparison.Ordinal)
                               && name != NameOf(module))
                .Select(name => $"{NameOf(module)} -> {name}"))
            .ToList();

        Assert.True(violations.Count == 0,
            "A module may only use another module through its .Contracts project. Violations: "
            + string.Join(", ", violations));
    }

    [Fact]
    public void Modules_do_not_depend_on_the_api_host()
    {
        var violations = ModuleAssemblies
            .Where(module => ReferencedNames(module).Contains(HostAssemblyName))
            .Select(NameOf)
            .ToList();

        Assert.True(violations.Count == 0,
            "Modules must not reference the API host. Violations: " + string.Join(", ", violations));
    }

    [Fact]
    public void BuildingBlocks_does_not_depend_on_modules_or_the_api_host()
    {
        var buildingBlocks = typeof(IModule).Assembly;

        var violations = ReferencedNames(buildingBlocks)
            .Where(name => name.StartsWith(ModulePrefix, StringComparison.Ordinal) || name == HostAssemblyName)
            .ToList();

        Assert.True(violations.Count == 0,
            "BuildingBlocks must stay independent of modules and the host. Violations: " + string.Join(", ", violations));
    }

    [Fact]
    public void Each_module_exposes_only_its_module_class()
    {
        // Everything else (endpoints, handlers, models, data access) must be internal,
        // so other code cannot couple to a module's implementation details.
        var violations = ModuleAssemblies
            .SelectMany(module => module.GetExportedTypes()
                .Where(type => !typeof(IModule).IsAssignableFrom(type))
                .Select(type => type.FullName))
            .ToList();

        Assert.True(violations.Count == 0,
            "Module types must be internal (shared types belong in a .Contracts project). Public types found: "
            + string.Join(", ", violations));
    }

    [Fact]
    public void Each_module_has_one_module_class_and_it_is_registered_in_the_catalog()
    {
        var catalogTypes = ModuleCatalog.All.Select(module => module.GetType()).ToHashSet();

        foreach (var module in ModuleAssemblies)
        {
            var moduleTypes = module.GetTypes()
                .Where(type => typeof(IModule).IsAssignableFrom(type) && type is { IsClass: true, IsAbstract: false })
                .ToList();

            var moduleType = Assert.Single(moduleTypes);
            Assert.True(catalogTypes.Contains(moduleType),
                $"{moduleType.FullName} is referenced by the host but not listed in ModuleCatalog.");
        }
    }

    private static Assembly[] LoadModuleAssemblies() =>
        Directory.GetFiles(AppContext.BaseDirectory, $"{ModulePrefix}*.dll")
            .Select(AssemblyName.GetAssemblyName)
            .Where(name => name.Name is not null && !name.Name.EndsWith(ContractsSuffix, StringComparison.Ordinal))
            .Select(AssemblyLoadContext.Default.LoadFromAssemblyName)
            .ToArray();

    private static string NameOf(Assembly assembly) => assembly.GetName().Name!;

    private static HashSet<string> ReferencedNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
}
