using AlMotaheda.BuildingBlocks.Validation;
using AlMotaheda.Modules.Office.Data;
using AlMotaheda.Modules.Office.Features.Expenses;
using AlMotaheda.Modules.Office.Storage;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Office.Tests.Unit;

/// <summary>The office rules that need no database: totals, the approval policy, status flows, file types, the tables.</summary>
public sealed class OfficeRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 20);

    private static OfficeDbContext CreateContext() => new OfficeDbContextDesignTimeFactory().CreateDbContext([]);

    [Theory]
    [InlineData(3, 33.335, 0, 0, 100.01)]
    [InlineData(2, 500, 50, 70, 1020)]
    [InlineData(1, 1200, 0, 168, 1368)]
    public void The_server_computes_the_total(decimal quantity, decimal unitPrice, decimal discount, decimal tax, decimal expected) =>
        Assert.Equal(expected, ExpenseFields.Total(quantity, unitPrice, discount, tax));

    [Fact]
    public void A_form_with_bad_numbers_and_dates_lists_every_field()
    {
        var errors = new ValidationErrors();
        var fields = ExpenseFields.Validate(
            new OfficeExpenseRequest(1, "Food", null, "", null, null, null, 0m, "", -1m, -5m, 0.001m, "2026-10-30", null, null, null, Guid.NewGuid(), null),
            Today, 60, errors);

        Assert.Null(fields);
        Assert.Equal(["category", "description", "discount", "expenseDate", "quantity", "tax", "unit", "unitPrice"], errors.ToDictionary().Keys.Order());
    }

    [Fact]
    public void A_discount_above_the_gross_amount_is_refused()
    {
        var errors = new ValidationErrors();
        ExpenseFields.Validate(new OfficeExpenseRequest(1, "Stationery", null, "Pens", null, null, null, 1m, "Box", 10m, 11m, 0m, "2026-10-19", null, null, null,
            Guid.NewGuid(), null), Today, 60, errors);

        Assert.True(errors.Has("discount"));
    }

    [Fact]
    public void An_expense_older_than_the_policy_allows_is_refused()
    {
        var errors = new ValidationErrors();
        ExpenseFields.Validate(new OfficeExpenseRequest(1, "Stationery", null, "Pens", null, null, null, 1m, "Box", 10m, null, null, "2026-07-01", null, null, null,
            Guid.NewGuid(), null), Today, 60, errors);

        Assert.True(errors.Has("expenseDate"));
    }

    [Theory]
    [InlineData(new string[0], 1)]
    [InlineData(new[] { OfficeReviewReasons.NoReference }, 1)]
    [InlineData(new[] { OfficeReviewReasons.AboveLimit }, 2)]
    [InlineData(new[] { OfficeReviewReasons.AboveRange }, 2)]
    [InlineData(new[] { OfficeReviewReasons.UnitMismatch }, 2)]
    [InlineData(new[] { OfficeReviewReasons.DuplicateRisk }, 2)]
    [InlineData(new[] { OfficeReviewReasons.Repair }, 2)]
    [InlineData(new[] { OfficeReviewReasons.Emergency }, 2)]
    [InlineData(new[] { OfficeReviewReasons.AboveEstimate }, 2)]
    [InlineData(new[] { OfficeReviewReasons.AboveAvailable }, 2)]
    public void High_risk_expenses_need_two_approvals(string[] reasons, int expected) =>
        Assert.Equal(expected, ExpensePolicy.RequiredApprovals(reasons));

    [Fact]
    public void A_funding_request_moves_only_forward()
    {
        var funding = new OfficeFundingRequest { Reason = "Cash" };
        Assert.False(funding.CanMoveTo(OfficeFundingStatus.Received));
        Assert.False(funding.CanMoveTo(OfficeFundingStatus.Disbursed));
        funding.MoveTo(OfficeFundingStatus.Approved);
        Assert.False(funding.CanMoveTo(OfficeFundingStatus.Received));
        funding.MoveTo(OfficeFundingStatus.Disbursed);
        Assert.False(funding.CanMoveTo(OfficeFundingStatus.Cancelled));
        funding.MoveTo(OfficeFundingStatus.Received);
        Assert.Throws<InvalidOperationException>(() => funding.MoveTo(OfficeFundingStatus.Received));
    }

    [Fact]
    public void A_posted_expense_can_not_be_returned_or_resubmitted_while_approved()
    {
        var expense = new OfficeExpense { Category = "Other", Description = "x", Unit = "Piece" };
        expense.MoveTo(OfficeExpenseStatus.Approved);
        Assert.False(expense.CanMoveTo(OfficeExpenseStatus.Returned));
        Assert.False(expense.CanMoveTo(OfficeExpenseStatus.Submitted));
        Assert.True(expense.CanMoveTo(OfficeExpenseStatus.Rejected));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 }, "JPEG")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "PNG")]
    [InlineData(new byte[] { 0x25, 0x50, 0x44, 0x46, 0x2D, 0x31 }, "PDF")]
    [InlineData(new byte[] { 0, 0, 0, 0x18, 0x66, 0x74, 0x79, 0x70, 0x68, 0x65, 0x69, 0x63 }, "HEIC")]
    [InlineData(new byte[] { 0x4D, 0x5A, 0x90, 0x00 }, null)]
    [InlineData(new byte[] { 0x3C, 0x68, 0x74, 0x6D, 0x6C, 0x3E }, null)]
    public void File_types_come_from_the_first_bytes(byte[] header, string? expected) =>
        Assert.Equal(expected, OfficeFileTypes.Detect(header)?.Name);

    [Fact]
    public void Receipts_and_repair_photos_must_be_photos()
    {
        Assert.False(OfficeFileTypes.Allows(OfficeAttachmentPurpose.Receipt, OfficeFileTypes.Pdf));
        Assert.False(OfficeFileTypes.Allows(OfficeAttachmentPurpose.AfterRepair, OfficeFileTypes.Pdf));
        Assert.True(OfficeFileTypes.Allows(OfficeAttachmentPurpose.Quotation, OfficeFileTypes.Pdf));
        Assert.True(OfficeFileTypes.Allows(OfficeAttachmentPurpose.Receipt, OfficeFileTypes.Jpeg));
    }

    [Fact]
    public void Committed_migrations_match_the_model()
    {
        using var db = CreateContext();

        Assert.False(db.Database.HasPendingModelChanges(), "The model changed without a migration. Run dotnet ef migrations add.");
        Assert.Equal(["InitialOffice", "FinanceCustodyLinks"], db.Database.GetMigrations().Select(id => id[(id.IndexOf('_') + 1)..]));
    }

    [Theory]
    [InlineData("CREATE UNIQUE INDEX [UX_OfficeLedgerEntry_Source] ON [office].[OfficeLedgerEntry] ([SourceType], [SourceId])")]
    [InlineData("CREATE UNIQUE INDEX [UX_OfficeLedgerEntry_Reverses] ON [office].[OfficeLedgerEntry] ([ReversesEntryId]) WHERE [ReversesEntryId] IS NOT NULL")]
    [InlineData("CREATE UNIQUE INDEX [UX_OfficeExpenseApproval_Once] ON [office].[OfficeExpenseApproval] ([ExpenseId], [Round], [ApproverUserId])")]
    [InlineData("CREATE UNIQUE INDEX [UX_OfficeExpense_SpendRequest] ON [office].[OfficeExpense] ([SpendRequestId]) WHERE [SpendRequestId] IS NOT NULL AND [Status] <> 'Rejected'")]
    [InlineData("CREATE UNIQUE INDEX [UX_OfficeExpense_IdempotencyKey] ON [office].[OfficeExpense] ([IdempotencyKey])")]
    [InlineData("CONSTRAINT [CK_OfficeExpense_PostedApproved] CHECK ([PostingStatus] = 'NotPosted' OR [Status] = 'Approved')")]
    [InlineData("CONSTRAINT [CK_OfficeFundingRequest_Received] CHECK ([ReceivedAmount] IS NULL OR ([ReceivedAmount] > 0 AND [ReceivedAmount] <= [DisbursedAmount]))")]
    [InlineData("CONSTRAINT [CK_OfficeCashCount_Difference] CHECK ([Difference] = [CountedAmount] - [RecordedBalance])")]
    [InlineData("[Quantity] decimal(18,3) NOT NULL")]
    public void The_create_script_contains(string expected)
    {
        using var db = CreateContext();

        Assert.Contains(expected, db.Database.GenerateCreateScript());
    }

    [Fact]
    public void There_is_no_balance_column_and_no_cascade_delete()
    {
        using var db = CreateContext();
        var script = db.Database.GenerateCreateScript();

        Assert.DoesNotContain("ON DELETE CASCADE", script);
        Assert.DoesNotContain("[Balance]", script);
    }
}
