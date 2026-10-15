using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Tests.Unit.Planning;

/// <summary>The budget, daily report and report request tables, as the model produces them, with no database.</summary>
public sealed class PlanningSchemaTests
{
    private static ProjectsDbContext CreateContext() => new ProjectsDbContextDesignTimeFactory().CreateDbContext([]);

    [Theory]
    [InlineData("Budget")]
    [InlineData("BudgetLine")]
    [InlineData("DailyReport")]
    [InlineData("DailyReportLine")]
    [InlineData("DailyReportMaterial")]
    [InlineData("DailyReportEquipment")]
    [InlineData("ReportRequest")]
    public void Planning_tables_live_in_the_projects_schema(string table)
    {
        using var db = CreateContext();

        Assert.Contains($"CREATE TABLE [projects].[{table}]", db.Database.GenerateCreateScript());
    }

    [Theory]
    [InlineData("CREATE UNIQUE INDEX [UX_Budget_Project_Version] ON [projects].[Budget] ([ProjectId], [Version])")]
    [InlineData("CREATE UNIQUE INDEX [UX_Budget_Draft] ON [projects].[Budget] ([ProjectId]) WHERE [Status] = 'Draft'")]
    [InlineData("CREATE UNIQUE INDEX [UX_Budget_Approved] ON [projects].[Budget] ([ProjectId]) WHERE [Status] = 'Approved'")]
    [InlineData("CREATE UNIQUE INDEX [UX_BudgetLine_Budget_LineNo] ON [projects].[BudgetLine] ([BudgetId], [LineNo])")]
    [InlineData("CREATE UNIQUE INDEX [UX_DailyReport_Project_Date_Author] ON [projects].[DailyReport] ([ProjectId], [ReportDate], [AuthorUserId])")]
    [InlineData("CREATE UNIQUE INDEX [UX_DailyReportLine_Report_LineNo] ON [projects].[DailyReportLine] ([ReportId], [LineNo])")]
    [InlineData("CREATE INDEX [IX_ReportRequest_Project_Status] ON [projects].[ReportRequest] ([ProjectId], [Status])")]
    [InlineData("CONSTRAINT [CK_Budget_Status] CHECK ([Status] IN ('Draft', 'Approved', 'Superseded'))")]
    [InlineData("CONSTRAINT [CK_BudgetLine_CostGroup] CHECK ([CostGroup] IN ('Labor', 'Subcontractors', 'Materials', 'Equipment', 'SiteExpenses', 'Other'))")]
    [InlineData("CONSTRAINT [CK_BudgetLine_Amount] CHECK ([Amount] > 0)")]
    [InlineData("CONSTRAINT [CK_DailyReportLine_Quantity] CHECK ([Quantity] > 0)")]
    [InlineData("CONSTRAINT [CK_DailyReportMaterial_Quantity] CHECK ([Quantity] > 0)")]
    [InlineData("CONSTRAINT [CK_ReportRequest_Parties] CHECK ([RequesterUserId] <> [RecipientUserId])")]
    [InlineData("CONSTRAINT [FK_Budget_Project_ProjectId] FOREIGN KEY ([ProjectId]) REFERENCES [projects].[Project] ([Id])")]
    [InlineData("CONSTRAINT [FK_BudgetLine_Budget_BudgetId] FOREIGN KEY ([BudgetId]) REFERENCES [projects].[Budget] ([Id])")]
    [InlineData("CONSTRAINT [FK_BudgetLine_ProjectItem_ProjectItemId] FOREIGN KEY ([ProjectItemId]) REFERENCES [projects].[ProjectItem] ([Id])")]
    [InlineData("CONSTRAINT [FK_DailyReport_ReportRequest_ReportRequestId] FOREIGN KEY ([ReportRequestId]) REFERENCES [projects].[ReportRequest] ([Id])")]
    [InlineData("CONSTRAINT [FK_DailyReportLine_ProjectItem_ProjectItemId] FOREIGN KEY ([ProjectItemId]) REFERENCES [projects].[ProjectItem] ([Id])")]
    [InlineData("[Quantity] decimal(18,3) NULL")]
    [InlineData("[UnitPrice] decimal(18,2) NULL")]
    [InlineData("[Amount] decimal(18,2) NOT NULL")]
    [InlineData("[ReportDate] date NOT NULL")]
    [InlineData("[Hours] decimal(5,1) NULL")]
    [InlineData("[Description] nvarchar(300) NOT NULL")]
    [InlineData("[ResponseText] nvarchar(4000) NULL")]
    [InlineData("[CostGroup] varchar(30) NOT NULL")]
    public void Script_contains_the_designed_constraints_and_indexes(string expected)
    {
        using var db = CreateContext();

        Assert.Contains(expected, db.Database.GenerateCreateScript());
    }

    [Fact]
    public void A_budget_line_is_a_lump_sum_or_quantity_times_price_and_nulls_are_tested_explicitly()
    {
        using var db = CreateContext();

        // A CHECK passes when its result is unknown, so each nullable column is tested with IS (NOT) NULL first.
        Assert.Contains(
            "CONSTRAINT [CK_BudgetLine_Pricing] CHECK (([Quantity] IS NULL AND [UnitPrice] IS NULL) "
            + "OR ([Quantity] IS NOT NULL AND [UnitPrice] IS NOT NULL AND [Quantity] > 0 AND [UnitPrice] > 0 "
            + "AND ABS([Amount] - ROUND([Quantity] * [UnitPrice], 2)) < 0.01))",
            db.Database.GenerateCreateScript());
    }

    [Fact]
    public void An_approved_budget_always_says_who_approved_it()
    {
        using var db = CreateContext();

        Assert.Contains(
            "CONSTRAINT [CK_Budget_Approval] CHECK (([Status] = 'Draft' AND [ApprovedAt] IS NULL AND [ApprovedBy] IS NULL) "
            + "OR ([Status] <> 'Draft' AND [ApprovedAt] IS NOT NULL AND [ApprovedBy] IS NOT NULL))",
            db.Database.GenerateCreateScript());
    }

    [Fact]
    public void A_subcontractor_line_names_its_subcontractor_and_an_internal_line_does_not()
    {
        using var db = CreateContext();

        Assert.Contains(
            "CONSTRAINT [CK_DailyReportLine_Subcontractor] CHECK (([PerformedBy] = 'Internal' AND [SubcontractorName] IS NULL AND [SubContractId] IS NULL) "
            + "OR ([PerformedBy] = 'Subcontractor' AND ([SubcontractorName] IS NOT NULL OR [SubContractId] IS NOT NULL)))",
            db.Database.GenerateCreateScript());
    }

    [Fact]
    public void Daily_reports_hold_no_money_column()
    {
        using var db = CreateContext();

        var columns = db.Model.GetEntityTypes()
            .Where(entity => entity.ClrType == typeof(DailyReport) || entity.ClrType == typeof(DailyReportLine)
                || entity.ClrType == typeof(DailyReportMaterial) || entity.ClrType == typeof(DailyReportEquipment))
            .SelectMany(entity => entity.GetProperties())
            .Select(property => property.Name);

        Assert.DoesNotContain(columns, name => name.Contains("Amount", StringComparison.Ordinal)
            || name.Contains("Price", StringComparison.Ordinal) || name.Contains("Cost", StringComparison.Ordinal));
    }

    [Fact]
    public void Subcontract_and_equipment_references_have_no_foreign_key_into_other_modules()
    {
        using var db = CreateContext();
        var script = db.Database.GenerateCreateScript();

        Assert.DoesNotContain("REFERENCES [sub]", script);
        Assert.DoesNotContain("REFERENCES [fleet]", script);
        Assert.Contains("[SubContractId] int NULL", script);
        Assert.Contains("[EquipmentId] int NULL", script);
    }

    [Fact]
    public void Planning_foreign_keys_never_cascade()
    {
        using var db = CreateContext();
        Type[] planning =
        [
            typeof(Budget), typeof(BudgetLine), typeof(DailyReport), typeof(DailyReportLine), typeof(DailyReportMaterial),
            typeof(DailyReportEquipment), typeof(ReportRequest),
        ];

        var cascading = db.Model.GetEntityTypes()
            .Where(entity => planning.Contains(entity.ClrType))
            .SelectMany(entity => entity.GetForeignKeys())
            .Where(foreignKey => foreignKey.DeleteBehavior != DeleteBehavior.NoAction)
            .Select(foreignKey => foreignKey.ToString());

        Assert.Empty(cascading);
    }
}
