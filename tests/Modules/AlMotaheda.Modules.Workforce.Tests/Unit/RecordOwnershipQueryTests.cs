using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Integration;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Workforce.Tests.Unit;

/// <summary>"Is this labor sheet on this project?" translates to SQL and filters by the project. No database needed.</summary>
public sealed class RecordOwnershipQueryTests
{
    [Fact]
    public void A_labor_sheet_is_on_its_own_project_only()
    {
        using var db = new WorkforceDbContextDesignTimeFactory().CreateDbContext([]);

        var sql = WorkforceRecordOwnership.Query(db, 3, ProjectRecordKinds.LaborSheet, [5, 6])!.ToQueryString();

        Assert.Contains("[workforce].[DailySheet]", sql);
        Assert.Contains("[ProjectId] = @", sql);
        Assert.Null(WorkforceRecordOwnership.Query(db, 3, ProjectRecordKinds.Expense, [5]));
    }
}
