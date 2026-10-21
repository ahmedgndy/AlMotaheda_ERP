using AlMotaheda.Modules.HR.Data;
using AlMotaheda.Modules.HR.Features.Employees;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.HR.Tests.Unit;

/// <summary>The table design and the query translations, with no database.</summary>
public sealed class DatabaseSchemaTests
{
    private static HrDbContext CreateContext() => new HrDbContextDesignTimeFactory().CreateDbContext([]);

    [Fact]
    public void Committed_migrations_match_the_model()
    {
        using var db = CreateContext();

        Assert.False(db.Database.HasPendingModelChanges(), "The model changed without a migration. Run dotnet ef migrations add.");
        Assert.Equal(["InitialHr", "AssignmentsAndReferrals", "HrRequests"], db.Database.GetMigrations().Select(id => id[(id.IndexOf('_') + 1)..]));
    }

    [Theory]
    [InlineData("[EmployeeNo] AS CONCAT('EMP-', REPLICATE('0', 6 - LEN([Id])), [Id]) PERSISTED")]
    [InlineData("CREATE UNIQUE INDEX [UX_Employee_NationalId] ON [hr].[Employee] ([NationalId])")]
    [InlineData("CREATE UNIQUE INDEX [UX_Employee_UserId] ON [hr].[Employee] ([UserId]) WHERE [UserId] IS NOT NULL")]
    [InlineData("CREATE UNIQUE INDEX [UX_SalarySheet_Month] ON [hr].[SalarySheet] ([Month])")]
    [InlineData("CREATE UNIQUE INDEX [UX_SalaryStructure_Employee_EffectiveFrom] ON [hr].[SalaryStructure] ([EmployeeId], [EffectiveFrom])")]
    [InlineData("CONSTRAINT [CK_SalarySheetLine_Sum] CHECK ([NetSalary] = [BasicSalary] + [FixedAllowance] + [Bonuses] - [Deductions] - [AdvanceRecoveries])")]
    [InlineData("CONSTRAINT [CK_SalarySheet_Paid] CHECK ([Status] <> 'Paid' OR ([PaidOn] IS NOT NULL AND [PaymentRequestId] IS NOT NULL))")]
    [InlineData("CONSTRAINT [CK_Employee_NationalId] CHECK (LEN([NationalId]) = 14 AND [NationalId] NOT LIKE '%[^0-9]%')")]
    [InlineData("[BasicSalary] decimal(18,2) NOT NULL")]
    // Employees on projects over time, and the cases other modules refer to HR.
    [InlineData("CREATE TABLE [hr].[EmployeeAssignment]")]
    [InlineData("CREATE UNIQUE INDEX [UX_EmployeeAssignment_Open] ON [hr].[EmployeeAssignment] ([EmployeeId], [ProjectId]) WHERE [EndDate] IS NULL")]
    [InlineData("CONSTRAINT [CK_EmployeeAssignment_Role] CHECK ([Role] IN ('SiteEngineer', 'Driver', 'Supervisor', 'Administrator', 'Guard', 'Other'))")]
    [InlineData("CREATE TABLE [hr].[HrReferral]")]
    [InlineData("CREATE UNIQUE INDEX [UX_HrReferral_Source] ON [hr].[HrReferral] ([SourceType], [SourceId])")]
    [InlineData("CONSTRAINT [CK_HrReferral_SourceType] CHECK ([SourceType] IN ('VehicleExpense', 'VehicleViolation'))")]
    [InlineData("CONSTRAINT [CK_HrReferral_Deduction] CHECK (([Outcome] = 'DeductionApplied' AND [DeductionInputId] IS NOT NULL) OR ([Outcome] IS NULL OR [Outcome] <> 'DeductionApplied') AND [DeductionInputId] IS NULL)")]
    [InlineData("[Number] AS CONCAT('HRR-', REPLICATE('0', 6 - LEN([Id])), [Id]) PERSISTED")]
    // An employee's requests to HR, their thread, their evidence, and who was told.
    [InlineData("CREATE TABLE [hr].[HrRequest]")]
    [InlineData("CREATE TABLE [hr].[HrRequestMessage]")]
    [InlineData("CREATE TABLE [hr].[HrRequestAttachment]")]
    [InlineData("CREATE TABLE [hr].[HrRequestAlert]")]
    [InlineData("[Number] AS CONCAT('HRQ-', REPLICATE('0', 6 - LEN([Id])), [Id]) PERSISTED")]
    [InlineData("CONSTRAINT [CK_HrRequest_Status] CHECK ([Status] IN ('Submitted', 'UnderReview', 'NeedsInfo', 'Approved', 'Rejected', 'Closed'))")]
    [InlineData("CONSTRAINT [CK_HrRequest_CaseResponse] CHECK (([Type] = 'CaseResponse' AND [ReferralId] IS NOT NULL) OR ([Type] <> 'CaseResponse' AND [ReferralId] IS NULL))")]
    [InlineData("CREATE UNIQUE INDEX [UX_HrRequest_IdempotencyKey] ON [hr].[HrRequest] ([IdempotencyKey])")]
    [InlineData("CREATE UNIQUE INDEX [UX_HrRequestAttachment_StorageKey] ON [hr].[HrRequestAttachment] ([StorageKey])")]
    public void The_create_script_contains(string expected)
    {
        using var db = CreateContext();

        Assert.Contains(expected, db.Database.GenerateCreateScript());
    }

    [Fact]
    public void The_migration_adds_the_project_foreign_key_of_an_assignment()
    {
        using var db = CreateContext();
        var script = Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(db).GenerateScript();

        Assert.Contains("ALTER TABLE [hr].[EmployeeAssignment] ADD CONSTRAINT [FK_EmployeeAssignment_Project_ProjectId] FOREIGN KEY ([ProjectId]) REFERENCES [projects].[Project] ([Id])", script);
        Assert.Contains("ALTER TABLE [hr].[HrRequestAlert] ADD CONSTRAINT [FK_HrRequestAlert_User_RecipientUserId] FOREIGN KEY ([RecipientUserId]) REFERENCES [access].[User] ([Id])", script);
    }

    [Fact]
    public void No_foreign_key_deletes_in_the_database()
    {
        using var db = CreateContext();

        Assert.DoesNotContain("ON DELETE CASCADE", db.Database.GenerateCreateScript());
    }

    [Fact]
    public void The_employee_list_filters_and_orders_in_sql()
    {
        using var db = CreateContext();

        var sql = EmployeeQueries.Search(db, "Ali", 3, EmployeeKind.Engineer, EmployeeStatus.Active).Skip(25).Take(25).ToQueryString();

        Assert.Contains("[e].[NationalId] LIKE", sql);
        Assert.Contains("[e].[Kind] = @", sql);
        Assert.Contains("ORDER BY [e].[NameEn]", sql);
        Assert.Contains("INNER JOIN [hr].[Department]", sql);
    }
}
