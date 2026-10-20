using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Integration;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Tests.Unit.Core;

/// <summary>
/// "Is this record on this project?" for the Projects module's own records translates to SQL and always filters by
/// the project. No database needed.
/// </summary>
public sealed class RecordOwnershipQueryTests
{
    [Theory]
    [InlineData(ProjectRecordKinds.Expense, "[projects].[ProjectExpense]")]
    [InlineData(ProjectRecordKinds.SupplierPayment, "[projects].[SupplierPayment]")]
    [InlineData(ProjectRecordKinds.DailyReport, "[projects].[DailyReport]")]
    [InlineData(ProjectRecordKinds.ReportRequest, "[projects].[ReportRequest]")]
    [InlineData(ProjectRecordKinds.Budget, "[projects].[Budget]")]
    [InlineData(ProjectRecordKinds.CustodyEntry, "[projects].[CustodyEntry]")]
    [InlineData(ProjectRecordKinds.SupplyAgreement, "[projects].[SupplyAgreement]")]
    public void Each_record_kind_is_found_on_its_own_project_only(string kind, string table)
    {
        using var db = new ProjectsDbContextDesignTimeFactory().CreateDbContext([]);

        var sql = ProjectRecordOwnership.Query(db, 3, kind, [5, 6])!.ToQueryString();

        Assert.Contains(table, sql);
        Assert.Contains("[ProjectId] = @", sql);
    }

    [Fact]
    public void Kinds_of_other_modules_are_not_answered_here()
    {
        using var db = new ProjectsDbContextDesignTimeFactory().CreateDbContext([]);

        Assert.Null(ProjectRecordOwnership.Query(db, 3, ProjectRecordKinds.SubContract, [5]));
        Assert.Equal(7, new ProjectRecordOwnership(db).Kinds.Count);
    }
}
