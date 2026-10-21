using System.Net;
using System.Reflection;
using AlMotaheda.BuildingBlocks.Modules;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Api.Tests.Modules;

public sealed class ModuleRegistrationTests
{
    [Fact]
    public async Task Host_registers_every_module_in_the_catalog()
    {
        await using var app = await TestApplication.StartAsync();

        var registered = app.Services.GetServices<IModule>().Select(module => module.Name);

        // The order matters: each module's startup applies its migrations in this order. Every save writes the
        // Platform audit and outbox tables, the Projects tables point to access.User, Fleet's vehicle tables point to
        // hr.Employee, the other modules' tables point to projects tables, and Finance copies the existing payments of
        // those modules, so: Platform, Access, Projects, HR, the rest (Office after Archive), Finance.
        Assert.Equal(new[] { "Platform", "Access", "Projects", "HR", "Workforce", "Subcontractors", "Fleet", "Archive", "Office", "Procurement", "Reports", "Finance" }, registered);
    }

    [Fact]
    public async Task Every_project_record_kind_is_answered_by_exactly_one_module()
    {
        // A document link or a daily report row is only stored after the owning module confirms the record is on the
        // project. A kind nobody owns would be refused everywhere; a kind owned twice would be ambiguous.
        var database = new StandInDatabase();
        await using var app = await TestApplication.StartAsync(configureBuilder: database.AddTo);
        using var scope = app.Services.CreateScope();

        var owned = scope.ServiceProvider.GetServices<IProjectRecordOwnership>().SelectMany(owner => owner.Kinds).ToList();
        var kinds = typeof(ProjectRecordKinds).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();

        Assert.Equal(14, kinds.Count);
        Assert.Equal(kinds.Order(StringComparer.Ordinal), owned.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Module_services_and_endpoints_are_wired_into_the_host_under_the_api_prefix()
    {
        await using var app = await TestApplication.StartAsync(configureBuilder: builder =>
            builder.Services.AddModules(builder.Configuration, [new ProbeModule()]));

        var response = await app.Client.GetAsync("/api/probe");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ProbeService.Message, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Module_endpoints_require_a_signed_in_user_unless_they_allow_anonymous_access()
    {
        await using var app = await TestApplication.StartAsync(configureBuilder: builder =>
            builder.Services.AddModules(builder.Configuration, [new ProbeModule()]));

        var response = await app.Client.GetAsync("/api/probe/protected");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Registering_two_modules_with_the_same_name_fails_at_startup()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            services.AddModules(configuration, [new ProbeModule(), new ProbeModule()]));

        Assert.Contains("Probe", error.Message);
    }

    /// <summary>A test-only module that proves both registration hooks are called.</summary>
    private sealed class ProbeModule : IModule
    {
        public string Name => "Probe";

        public void RegisterServices(IServiceCollection services, IConfiguration configuration) =>
            services.AddSingleton<ProbeService>();

        public void MapEndpoints(IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/probe", (ProbeService service) => Results.Text(service.GetMessage())).AllowAnonymous();
            endpoints.MapGet("/probe/protected", (ProbeService service) => Results.Text(service.GetMessage()));
        }
    }

    private sealed class ProbeService
    {
        public const string Message = "probe module is wired";

        public string GetMessage() => Message;
    }
}
