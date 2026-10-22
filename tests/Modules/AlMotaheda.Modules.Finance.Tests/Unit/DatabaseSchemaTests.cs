using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Finance.Features.Ledger;
using AlMotaheda.Modules.Finance.Requests;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AlMotaheda.Modules.Finance.Tests.Unit;

/// <summary>The table design and the query translations, with no database.</summary>
public sealed class DatabaseSchemaTests
{
    private static FinanceDbContext CreateContext() => new FinanceDbContextDesignTimeFactory().CreateDbContext([]);

    [Fact]
    public void Committed_migrations_match_the_model()
    {
        using var db = CreateContext();

        Assert.False(db.Database.HasPendingModelChanges(), "The model changed without a migration. Run dotnet ef migrations add.");
        Assert.Equal(["InitialFinance", "ImportExistingPayments", "SalaryPayments", "CustodyFunding", "OfficeCustodyFunding", "TreasuryAndCustody", "CustodyAdjustmentDisputes", "OfficeAndTreasuryCodes", "TreasuryDeposits"], db.Database.GetMigrations().Select(id => id[(id.IndexOf('_') + 1)..]));
    }

    [Theory]
    [InlineData("[Number] AS CONCAT('PAY-', REPLICATE('0', 6 - LEN([Id])), [Id]) PERSISTED")]
    [InlineData("CONSTRAINT [CK_PaymentRequest_Amount] CHECK ([Amount] > 0)")]
    [InlineData("CONSTRAINT [CK_PaymentRequest_Paid] CHECK ([Status] NOT IN ('Paid', 'Reversed') OR ([PaidOn] IS NOT NULL AND [SourcePaymentId] IS NOT NULL))")]
    [InlineData("CONSTRAINT [CK_LedgerEntry_Amount] CHECK ([Amount] <> 0)")]
    [InlineData("CREATE UNIQUE INDEX [UX_PaymentRequest_IdempotencyKey] ON [finance].[PaymentRequest] ([IdempotencyKey])")]
    [InlineData("CREATE UNIQUE INDEX [UX_PaymentRequest_SourcePayment] ON [finance].[PaymentRequest] ([SourceType], [SourcePaymentId]) WHERE [SourcePaymentId] IS NOT NULL AND [Status] = 'Paid'")]
    [InlineData("CREATE UNIQUE INDEX [UX_LedgerEntry_Reverses] ON [finance].[LedgerEntry] ([ReversesEntryId]) WHERE [ReversesEntryId] IS NOT NULL")]
    // The treasury and the custody hierarchy.
    [InlineData("CREATE TABLE [finance].[TreasuryAccount]")]
    [InlineData("CREATE TABLE [finance].[CustodyAccount]")]
    [InlineData("CREATE TABLE [finance].[CustodyMovement]")]
    [InlineData("CREATE TABLE [finance].[CustodyTransfer]")]
    [InlineData("CREATE TABLE [finance].[CustodyTransferEvent]")]
    [InlineData("CREATE TABLE [finance].[PayrollBatch]")]
    [InlineData("CREATE TABLE [finance].[CustodyCashCount]")]
    [InlineData("CREATE TABLE [finance].[CustodyAdjustment]")]
    [InlineData("CREATE TABLE [finance].[CustodyPeriodClose]")]
    [InlineData("[PaymentRequestId] int NULL")]
    [InlineData("CONSTRAINT [CK_LedgerEntry_Origin] CHECK ([PaymentRequestId] IS NOT NULL OR [CustodyTransferId] IS NOT NULL OR [TreasuryDepositId] IS NOT NULL)")]
    [InlineData("CONSTRAINT [CK_TreasuryDeposit_Amount] CHECK ([Amount] > 0)")]
    [InlineData("CONSTRAINT [CK_TreasuryDeposit_Account] CHECK ([Account] IN ('Bank', 'Cash'))")]
    [InlineData("CONSTRAINT [CK_LedgerEntry_CustodyAccount] CHECK (([Account] = 'Custody' AND [CustodyAccountId] IS NOT NULL) OR ([Account] <> 'Custody' AND [CustodyAccountId] IS NULL))")]
    [InlineData("CONSTRAINT [CK_LedgerEntry_Account] CHECK ([Account] IN ('Bank', 'Cash', 'Custody'))")]
    [InlineData("CONSTRAINT [CK_PaymentRequest_CustodySource] CHECK ([SourceCustodyAccountId] IS NULL OR [Method] = 'Cash')")]
    [InlineData("CONSTRAINT [CK_PaymentRequest_Purpose] CHECK ([Purpose] IN ('SupplierInvoices', 'WorkerWages', 'SubcontractWork', 'SubcontractAdvance', 'EquipmentCost', 'Salaries', 'CustodyAdvance', 'CustodyReturn', 'OfficeCustodyAdvance'))")]
    [InlineData("CONSTRAINT [CK_TreasuryAccount_Account] CHECK ([Account] IN ('Bank', 'Cash'))")]
    [InlineData("CREATE UNIQUE INDEX [UX_TreasuryAccount_Account] ON [finance].[TreasuryAccount] ([Account])")]
    [InlineData("CONSTRAINT [CK_CustodyAccount_Kind] CHECK ([Kind] IN ('Accountant', 'PayrollBatch', 'EngineerProject', 'Vehicle', 'Office'))")]
    [InlineData("CREATE UNIQUE INDEX [UX_CustodyAccount_Active] ON [finance].[CustodyAccount] ([Kind], [HolderUserId], [ReferenceId]) WHERE [Status] = 'Active'")]
    [InlineData("[Number] AS CONCAT('CA-', REPLICATE('0', 6 - LEN([Id])), [Id]) PERSISTED")]
    [InlineData("CONSTRAINT [CK_CustodyMovement_Amount] CHECK ([Amount] <> 0)")]
    [InlineData("CONSTRAINT [CK_CustodyMovement_Pending] CHECK ([Status] = 'Posted' OR [Amount] < 0)")]
    [InlineData("CREATE UNIQUE INDEX [UX_CustodyMovement_Account_Source] ON [finance].[CustodyMovement] ([AccountId], [SourceType], [SourceId])")]
    [InlineData("CREATE UNIQUE INDEX [UX_CustodyMovement_Reverses] ON [finance].[CustodyMovement] ([ReversesMovementId]) WHERE [ReversesMovementId] IS NOT NULL")]
    [InlineData("CONSTRAINT [CK_CustodyTransfer_Status] CHECK ([Status] IN ('Requested', 'Approved', 'Disbursed', 'Received', 'Rejected', 'Cancelled', 'Reversed'))")]
    [InlineData("CONSTRAINT [CK_CustodyTransfer_Purpose] CHECK ([Purpose] IN ('CustodyAdvance', 'CustodyReturn'))")]
    [InlineData("CONSTRAINT [CK_CustodyTransfer_Amount] CHECK ([Amount] > 0)")]
    [InlineData("CREATE UNIQUE INDEX [UX_CustodyTransfer_IdempotencyKey] ON [finance].[CustodyTransfer] ([IdempotencyKey])")]
    [InlineData("[Number] AS CONCAT('CT-', REPLICATE('0', 6 - LEN([Id])), [Id]) PERSISTED")]
    [InlineData("CONSTRAINT [CK_PayrollBatch_Kind] CHECK ([Kind] IN ('DailyLabor', 'Salaries'))")]
    [InlineData("CREATE UNIQUE INDEX [UX_PayrollBatchAllocation_Batch_Project] ON [finance].[PayrollBatchAllocation] ([BatchId], [ProjectId])")]
    [InlineData("CONSTRAINT [CK_CustodyCashCount_Difference] CHECK ([Difference] = [CountedAmount] - [BookBalance])")]
    [InlineData("CONSTRAINT [CK_CustodyAdjustment_Amount] CHECK ([Amount] <> 0)")]
    [InlineData("CONSTRAINT [CK_CustodyAdjustment_Status] CHECK ([Status] IN ('Posted', 'Disputed', 'Upheld', 'Reversed'))")]
    [InlineData("CONSTRAINT [CK_CustodyAdjustment_Shortfall] CHECK ([Shortfall] >= 0)")]
    [InlineData("CREATE UNIQUE INDEX [UX_CustodyAdjustment_Reverses] ON [finance].[CustodyAdjustment] ([ReversesAdjustmentId]) WHERE [ReversesAdjustmentId] IS NOT NULL")]
    [InlineData("CREATE UNIQUE INDEX [UX_CustodyPeriodClose_Account_PeriodEnd] ON [finance].[CustodyPeriodClose] ([AccountId], [PeriodEnd])")]
    [InlineData("[OpeningBalance] decimal(18,2) NOT NULL")]
    public void The_create_script_contains(string expected)
    {
        using var db = CreateContext();

        Assert.Contains(expected, db.Database.GenerateCreateScript());
    }

    [Theory]
    [InlineData("ALTER TABLE [finance].[CustodyAccount] ADD CONSTRAINT [FK_CustodyAccount_User_HolderUserId] FOREIGN KEY ([HolderUserId]) REFERENCES [access].[User] ([Id])")]
    [InlineData("ALTER TABLE [finance].[PayrollBatch] ADD CONSTRAINT [FK_PayrollBatch_User_HolderUserId] FOREIGN KEY ([HolderUserId]) REFERENCES [access].[User] ([Id])")]
    [InlineData("ALTER TABLE [finance].[CustodyTransferAlert] ADD CONSTRAINT [FK_CustodyTransferAlert_User_RecipientUserId] FOREIGN KEY ([RecipientUserId]) REFERENCES [access].[User] ([Id])")]
    [InlineData("ALTER TABLE [finance].[PayrollBatchAllocation] ADD CONSTRAINT [FK_PayrollBatchAllocation_Project_ProjectId] FOREIGN KEY ([ProjectId]) REFERENCES [projects].[Project] ([Id])")]
    public void The_migration_adds_the_foreign_keys_to_other_modules(string expected)
    {
        using var db = CreateContext();

        Assert.Contains(expected, db.GetService<IMigrator>().GenerateScript());
    }

    [Fact]
    public void Foreign_keys_never_cascade()
    {
        using var db = CreateContext();

        Assert.DoesNotContain("CASCADE", db.GetService<IMigrator>().GenerateScript());
    }

    [Fact]
    public void The_custody_and_treasury_queries_translate()
    {
        using var db = CreateContext();

        var entries = Features.Custody.TreasuryQueries.Entries(db, LedgerAccount.Cash, new DateOnly(2026, 1, 1)).ToQueryString();
        Assert.Contains("[Account] = @account", entries);
        Assert.Contains("[EntryDate] >= @", entries);
        var open = Features.Custody.TreasuryQueries.OpenTransfersOut(db, LedgerAccount.Cash).ToQueryString();
        Assert.Contains("[SourceKind] = N'Treasury'", open);
        Assert.Contains("'Requested'", open);
    }

    [Fact]
    public void The_lists_filter_and_total_in_sql()
    {
        using var db = CreateContext();

        var requests = PaymentRequestViews.Query(db, new RequestFilter(PaymentRequestStatus.Submitted, "Supplier", 5, 3, "SupplierPayment", new HashSet<int> { 3, 4 })).ToQueryString();
        Assert.Contains("[p].[ProjectId] IS NOT NULL", requests);
        Assert.Contains("[p].[Status] = @", requests);
        Assert.Contains("ORDER BY [p].[Id] DESC", requests);

        var ledger = ListLedgerEntries.Query(db, new ListLedgerEntries.Filter(
            new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), 3, LedgerAccount.Bank, "Supplier", 5, null)).ToQueryString();
        Assert.Contains("[l].[EntryDate] >= @", ledger);
        Assert.Contains("[l].[EntryDate] <= @", ledger);
        Assert.Contains("[l].[Account] = @", ledger);
    }
}
