using System.Globalization;
using System.Net;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;

namespace AlMotaheda.Modules.Fleet.Tests.Database;

/// <summary>
/// Other modules point to Fleet records only when Fleet confirms they are on the project: a daily report row names a
/// machine that has been on its own site, never one that only worked on another project.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ProjectRecordOwnershipTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task A_daily_report_names_only_machines_that_have_been_on_its_project()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var onA = await api.RegisterAsync(cast.Executive, FleetApi.EquipmentBody("EX-301"));
        var onB = await api.RegisterAsync(cast.Executive, FleetApi.EquipmentBody("EX-302"));
        await api.AssignedAsync(cast.Manager, cast.ProjectA, onA.Id(), "2026-01-05");
        await api.AssignedAsync(cast.OtherManager, cast.ProjectB, onB.Id(), "2026-01-05");
        var today = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var withMachineOfB = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectA}/daily-reports", cast.Manager, new
        {
            reportDate = today,
            issues = "Excavation",
            equipment = new object[] { new { equipmentId = onB.Id(), equipmentName = "Excavator", hours = 6 } },
        });
        Assert.Equal(HttpStatusCode.BadRequest, withMachineOfB.StatusCode);
        Assert.Equal("validation_failed", await CodeOf(withMachineOfB));

        var withOwnMachine = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectA}/daily-reports", cast.Manager, new
        {
            reportDate = today,
            issues = "Excavation",
            equipment = new object[] { new { equipmentId = onA.Id(), equipmentName = "Excavator", hours = 6 } },
        });
        await EnsureStatus(withOwnMachine, HttpStatusCode.Created);
    }
}
