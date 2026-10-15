using AlMotaheda.Modules.Workforce.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlMotaheda.Modules.Workforce.Tests.Unit;

/// <summary>
/// Checks the SQL the model produces, with no database: the approved table design, and that the committed
/// migration matches the model (a forgotten migration fails here).
/// </summary>
public sealed class DatabaseSchemaTests
{
    private static WorkforceDbContext CreateContext() => new WorkforceDbContextDesignTimeFactory().CreateDbContext([]);

    [Fact]
    public void Committed_migrations_match_the_model()
    {
        using var db = CreateContext();

        Assert.False(db.Database.HasPendingModelChanges(), "The model changed without a migration. Run dotnet ef migrations add.");
        Assert.Equal(["InitialWorkforce"], db.Database.GetMigrations().Select(id => id[(id.IndexOf('_') + 1)..]));
    }

    [Theory]
    [InlineData("Trade")]
    [InlineData("Worker")]
    [InlineData("WorkerAssignment")]
    [InlineData("DailySheet")]
    [InlineData("AttendanceLine")]
    [InlineData("LineAdjustment")]
    [InlineData("LaborAllocation")]
    [InlineData("WorkerPayment")]
    [InlineData("WorkerPaymentAllocation")]
    public void Tables_live_in_the_workforce_schema(string table)
    {
        using var db = CreateContext();

        Assert.Contains($"CREATE TABLE [workforce].[{table}]", db.Database.GenerateCreateScript());
    }

    [Theory]
    // One live record per worker, day and shift; one live sheet per engineer, project, day and shift (W1).
    [InlineData("CREATE UNIQUE INDEX [UX_AttendanceLine_Worker_Shift] ON [workforce].[AttendanceLine] ([WorkerId], [WorkDate], [Shift]) WHERE [IsVoid] = 0")]
    [InlineData("CREATE UNIQUE INDEX [UX_DailySheet_Recorder_Day] ON [workforce].[DailySheet] ([ProjectId], [WorkDate], [Shift], [RecordedBy]) WHERE [Status] <> 'Rejected'")]
    [InlineData("CREATE UNIQUE INDEX [UX_WorkerAssignment_Open] ON [workforce].[WorkerAssignment] ([WorkerId], [ProjectId]) WHERE [EndDate] IS NULL")]
    [InlineData("CREATE UNIQUE INDEX [UX_Worker_NationalId] ON [workforce].[Worker] ([NationalId]) WHERE [NationalId] IS NOT NULL")]
    [InlineData("CREATE UNIQUE INDEX [UX_Trade_Code] ON [workforce].[Trade] ([Code])")]
    [InlineData("CREATE UNIQUE INDEX [UX_LaborAllocation_Line_Item] ON [workforce].[LaborAllocation] ([LineId], [ProjectItemId])")]
    [InlineData("CREATE INDEX [IX_LaborAllocation_ProjectItemId] ON [workforce].[LaborAllocation] ([ProjectItemId])")]
    [InlineData("CREATE UNIQUE INDEX [UX_WorkerPayment_IdempotencyKey] ON [workforce].[WorkerPayment] ([IdempotencyKey])")]
    [InlineData("CREATE UNIQUE INDEX [UX_WorkerPaymentAllocation_Payment_Line] ON [workforce].[WorkerPaymentAllocation] ([PaymentId], [LineId])")]
    [InlineData("CREATE INDEX [IX_WorkerPaymentAllocation_LineId] ON [workforce].[WorkerPaymentAllocation] ([LineId])")]
    // A day is 1 or 0.5; the portions of a day are in (0, 1]; nothing negative is ever paid or allocated.
    [InlineData("CONSTRAINT [CK_AttendanceLine_Days] CHECK ([Days] IN (0.5, 1))")]
    [InlineData("CONSTRAINT [CK_AttendanceLine_OvertimeHours] CHECK ([OvertimeHours] >= 0 AND [OvertimeHours] <= 12)")]
    [InlineData("CONSTRAINT [CK_LaborAllocation_Portion] CHECK ([Portion] > 0 AND [Portion] <= 1)")]
    [InlineData("CONSTRAINT [CK_LaborAllocation_Amount] CHECK ([Amount] IS NULL OR [Amount] >= 0)")]
    [InlineData("CONSTRAINT [CK_WorkerPayment_Amount] CHECK ([Amount] > 0)")]
    [InlineData("CONSTRAINT [CK_WorkerPaymentAllocation_Amount] CHECK ([Amount] > 0)")]
    [InlineData("CONSTRAINT [CK_LineAdjustment_Amount] CHECK ([Amount] > 0)")]
    [InlineData("CONSTRAINT [CK_Trade_DefaultDailyRate] CHECK ([DefaultDailyRate] > 0)")]
    [InlineData("CONSTRAINT [CK_DailySheet_Status] CHECK ([Status] IN ('Draft', 'Submitted', 'Returned', 'Approved', 'Rejected'))")]
    [InlineData("CONSTRAINT [CK_DailySheet_Shift] CHECK ([Shift] IN ('Day', 'Night'))")]
    [InlineData("CONSTRAINT [CK_WorkerPayment_Method] CHECK ([Method] IN ('Cash', 'BankTransfer'))")]
    [InlineData("[Days] decimal(2,1) NOT NULL")]
    [InlineData("[Portion] decimal(5,4) NOT NULL")]
    [InlineData("[NationalId] varchar(14) NULL")]
    [InlineData("[Status] varchar(30) NOT NULL")]
    [InlineData("[Shift] varchar(30) NOT NULL")]
    [InlineData("[NetAmount] decimal(18,2) NULL")]
    [InlineData("[IdempotencyKey] uniqueidentifier NOT NULL")]
    [InlineData("[WorkDate] date NOT NULL")]
    [InlineData("[RowVersion] rowversion NOT NULL")]
    public void Script_contains_the_approved_constraints_and_indexes(string expected)
    {
        using var db = CreateContext();

        Assert.Contains(expected, db.Database.GenerateCreateScript());
    }

    [Theory]
    // A CHECK passes when its result is unknown (NULL), so each nullable column is tested with IS NULL / IS NOT NULL.
    [InlineData("CONSTRAINT [CK_AttendanceLine_Pay] CHECK (([AppliedDailyRate] IS NULL AND [BaseAmount] IS NULL AND [OvertimeAmount] IS NULL AND [AllowancesAmount] IS NULL AND [DeductionsAmount] IS NULL AND [NetAmount] IS NULL) OR ([AppliedDailyRate] IS NOT NULL")]
    [InlineData("[NetAmount] >= 0 AND [NetAmount] = [BaseAmount] + [OvertimeAmount] + [AllowancesAmount] - [DeductionsAmount]))")]
    [InlineData("CONSTRAINT [CK_LineAdjustment_DeductionReason] CHECK ([Kind] = 'Allowance' OR [Reason] IS NOT NULL)")]
    [InlineData("CONSTRAINT [CK_WorkerPayment_Void] CHECK (([VoidedAt] IS NULL AND [VoidedBy] IS NULL AND [VoidReason] IS NULL) OR ([VoidedAt] IS NOT NULL AND [VoidedBy] IS NOT NULL AND [VoidReason] IS NOT NULL))")]
    [InlineData("CONSTRAINT [CK_WorkerAssignment_Ending] CHECK (([EndDate] IS NULL AND [EndedAt] IS NULL AND [EndedBy] IS NULL) OR ([EndDate] IS NOT NULL AND [EndedAt] IS NOT NULL AND [EndedBy] IS NOT NULL))")]
    [InlineData("CONSTRAINT [CK_Worker_DailyRate] CHECK ([DailyRate] IS NULL OR [DailyRate] > 0)")]
    [InlineData("CONSTRAINT [CK_Worker_NationalId] CHECK ([NationalId] IS NULL OR (LEN([NationalId]) = 14 AND [NationalId] NOT LIKE '%[^0-9]%'))")]
    [InlineData("CONSTRAINT [CK_DailySheet_Submitted] CHECK ([Status] = 'Draft' OR [SubmittedAt] IS NOT NULL)")]
    public void Checks_on_nullable_columns_test_null_explicitly(string expected)
    {
        using var db = CreateContext();

        Assert.Contains(expected, db.Database.GenerateCreateScript());
    }

    [Fact]
    public void The_migration_seeds_the_sixteen_trades()
    {
        using var db = CreateContext();

        var script = db.GetService<IMigrator>().GenerateScript();

        Assert.Contains("INSERT INTO [workforce].[Trade]", script);
        Assert.Contains("'plumber'", script);
        Assert.Equal(16, TradeSeed.All.Select(trade => trade.Id).Distinct().Count());
    }

    [Theory]
    [InlineData("ALTER TABLE [workforce].[WorkerAssignment] ADD CONSTRAINT [FK_WorkerAssignment_Project_ProjectId] FOREIGN KEY ([ProjectId]) REFERENCES [projects].[Project] ([Id])")]
    [InlineData("ALTER TABLE [workforce].[DailySheet] ADD CONSTRAINT [FK_DailySheet_Project_ProjectId] FOREIGN KEY ([ProjectId]) REFERENCES [projects].[Project] ([Id])")]
    [InlineData("ALTER TABLE [workforce].[LaborAllocation] ADD CONSTRAINT [FK_LaborAllocation_ProjectItem_ProjectItemId] FOREIGN KEY ([ProjectItemId]) REFERENCES [projects].[ProjectItem] ([Id])")]
    [InlineData("[workforce].[__EFMigrationsHistory]")]
    public void Migration_adds_the_foreign_keys_to_the_projects_tables(string expected)
    {
        using var db = CreateContext();

        Assert.Contains(expected, db.GetService<IMigrator>().GenerateScript());
    }

    [Fact]
    public void Foreign_keys_never_cascade()
    {
        using var db = CreateContext();

        var cascading = db.Model.GetEntityTypes()
            .SelectMany(entity => entity.GetForeignKeys())
            .Where(foreignKey => foreignKey.DeleteBehavior != DeleteBehavior.NoAction)
            .Select(foreignKey => foreignKey.ToString());

        Assert.Empty(cascading);
    }
}
