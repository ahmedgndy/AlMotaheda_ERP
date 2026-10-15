using System.Globalization;
using System.Net;
using System.Security.Claims;
using AlMotaheda.Api.Tests;
using AlMotaheda.Modules.Access.Tests;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static AlMotaheda.Api.Tests.ApiCalls;

namespace AlMotaheda.Modules.Projects.Tests.Api.Planning;

/// <summary>
/// A daily report may only point to a subcontract and a machine of its own project. The ids come from the client,
/// so the owning modules are asked (through <see cref="IProjectRecordOwnership"/>), with no SQL Server.
/// </summary>
public sealed class DailyReportReferenceTests
{
    private static readonly (string, string)[] AllSite = [("site", "VCEAF"), ("projects", "VCEAF")];

    [Fact]
    public async Task A_subcontract_or_machine_of_another_project_is_refused_on_create()
    {
        var owners = new RecordedOwnership();
        var (app, database) = await StartAsync(owners);
        await using var _ = app;
        var token = TestTokens.Create(userId: 7, role: "manager", permissions: AllSite);

        var response = await Send(app, "POST", "/api/projects/1/daily-reports", token, new
        {
            reportDate = Today(),
            lines = new object[]
            {
                new { workType = WorkTypes.All[0], description = "Block walls", unit = MeasurementUnits.All[0], quantity = 10, performedBy = "Subcontractor", subContractId = 77 },
            },
            equipment = new object[] { new { equipmentId = 55, equipmentName = "Loader", hours = 6 } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", await CodeOf(response));
        Assert.Equal(["equipment[0].equipmentId", "lines[0].subContractId"], (await ErrorFields(response)).Order(StringComparer.Ordinal));

        // The owners were asked about project 1 only, and nothing was written.
        Assert.Contains((1, ProjectRecordKinds.SubContract, 77), owners.Asked);
        Assert.Contains((1, ProjectRecordKinds.Equipment, 55), owners.Asked);
        Assert.DoesNotContain(database.Commands, command => command.Contains("INSERT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_report_without_subcontract_or_machine_ids_asks_no_owner()
    {
        var owners = new RecordedOwnership();
        var (app, _) = await StartAsync(owners);
        await using var __ = app;
        var token = TestTokens.Create(userId: 7, role: "manager", permissions: AllSite);

        // The reference checks pass; the stand-in database then can't save, which is not what this test is about.
        await Send(app, "POST", "/api/projects/1/daily-reports", token, new { reportDate = Today(), issues = "Rain all day" });

        Assert.Empty(owners.Asked);
    }

    private static string Today() => DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<(TestApplication App, StandInDatabase Database)> StartAsync(RecordedOwnership owners)
    {
        var database = new StandInDatabase();
        var app = await TestApplication.StartAsync(configureBuilder: builder =>
        {
            database.AddTo(builder);
            builder.Services.AddScoped<IProjectAccess>(_ => new VisibleProject());
            builder.Services.RemoveAll<IProjectRecordOwnership>();
            builder.Services.AddSingleton<IProjectRecordOwnership>(owners);
        });
        return (app, database);
    }

    /// <summary>Says no record is on any project, and remembers what it was asked.</summary>
    private sealed class RecordedOwnership : IProjectRecordOwnership
    {
        public List<(int ProjectId, string Kind, int Id)> Asked { get; } = [];

        public IReadOnlyCollection<string> Kinds { get; } = [ProjectRecordKinds.SubContract, ProjectRecordKinds.Equipment];

        public Task<IReadOnlySet<int>> FindOnProjectAsync(int projectId, string kind, IReadOnlyCollection<int> ids, CancellationToken cancellationToken)
        {
            lock (Asked)
            {
                Asked.AddRange(ids.Select(id => (projectId, kind, id)));
            }

            return Task.FromResult<IReadOnlySet<int>>(new HashSet<int>());
        }
    }

    private sealed class VisibleProject : IProjectAccess
    {
        public Task<ProjectAccessInfo?> FindVisibleAsync(ClaimsPrincipal caller, int projectId, CancellationToken cancellationToken) =>
            Task.FromResult<ProjectAccessInfo?>(new ProjectAccessInfo(projectId, "P-1", "مشروع", "Project", false, "ProjectManager", false));
    }
}
