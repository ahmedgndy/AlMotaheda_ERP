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
        Assert.Equal(["InitialFinance", "ImportExistingPayments", "SalaryPayments", "CustodyFunding"], db.Database.GetMigrations().Select(id => id[(id.IndexOf('_') + 1)..]));
    }

    [Theory]
    [InlineData("[Number] AS CONCAT('PAY-', REPLICATE('0', 6 - LEN([Id])), [Id]) PERSISTED")]
    [InlineData("CONSTRAINT [CK_PaymentRequest_Amount] CHECK ([Amount] > 0)")]
    [InlineData("CONSTRAINT [CK_PaymentRequest_Paid] CHECK ([Status] NOT IN ('Paid', 'Reversed') OR ([PaidOn] IS NOT NULL AND [SourcePaymentId] IS NOT NULL))")]
    [InlineData("CONSTRAINT [CK_LedgerEntry_Amount] CHECK ([Amount] <> 0)")]
    [InlineData("CREATE UNIQUE INDEX [UX_PaymentRequest_IdempotencyKey] ON [finance].[PaymentRequest] ([IdempotencyKey])")]
    [InlineData("CREATE UNIQUE INDEX [UX_PaymentRequest_SourcePayment] ON [finance].[PaymentRequest] ([SourceType], [SourcePaymentId]) WHERE [SourcePaymentId] IS NOT NULL AND [Status] = 'Paid'")]
    [InlineData("CREATE UNIQUE INDEX [UX_LedgerEntry_Reverses] ON [finance].[LedgerEntry] ([ReversesEntryId]) WHERE [ReversesEntryId] IS NOT NULL")]
    public void The_create_script_contains(string expected)
    {
        using var db = CreateContext();

        Assert.Contains(expected, db.Database.GenerateCreateScript());
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
