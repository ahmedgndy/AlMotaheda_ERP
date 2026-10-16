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
        Assert.Equal(["InitialHr"], db.Database.GetMigrations().Select(id => id[(id.IndexOf('_') + 1)..]));
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
    public void The_create_script_contains(string expected)
    {
        using var db = CreateContext();

        Assert.Contains(expected, db.Database.GenerateCreateScript());
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
