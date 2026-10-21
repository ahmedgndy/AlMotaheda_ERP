using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Office.Data.Configurations;

/// <summary>Text lengths shared by the forms and the columns.</summary>
internal static class OfficeLengths
{
    public const int Name = 150;
    public const int Description = 500;
    public const int Note = 1000;
    public const int Vendor = 200;
    public const int InvoiceNo = 60;
    public const int Unit = 30;
    public const int Reference = 100;
    public const int Code = 40;
    public const int ReviewReasons = 300;
    public const int DuplicateOf = 200;
    public const int FileName = 200;
    public const int ContentType = 100;
    public const int StorageKey = 200;
    public const int Device = 100;
}

internal sealed class OfficeCustodyConfiguration : IEntityTypeConfiguration<OfficeCustody>
{
    public const string NameArIndex = "UX_OfficeCustody_NameAr";
    public const string NameEnIndex = "UX_OfficeCustody_NameEn";

    public void Configure(EntityTypeBuilder<OfficeCustody> builder)
    {
        builder.ToTable(nameof(OfficeCustody), table =>
        {
            table.HasCheckConstraint("CK_OfficeCustody_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<OfficeCustodyStatus>())})");
            table.HasCheckConstraint("CK_OfficeCustody_Threshold", "[LowBalanceThreshold] >= 0");
            table.HasCheckConstraint("CK_OfficeCustody_MonthlyAllocation", "[MonthlyAllocation] >= 0");
        });

        builder.Property(custody => custody.NameAr).HasMaxLength(OfficeLengths.Name).IsUnicode();
        builder.Property(custody => custody.NameEn).HasMaxLength(OfficeLengths.Name).IsUnicode();
        builder.Property(custody => custody.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(custody => custody.Notes).HasMaxLength(OfficeLengths.Note).IsUnicode();
        builder.Property(custody => custody.StatusReason).HasMaxLength(OfficeLengths.Note).IsUnicode();
        builder.Property(custody => custody.RowVersion).IsRowVersion();
        builder.Ignore(custody => custody.IsActive);

        builder.HasIndex(custody => custody.NameAr).IsUnique().HasDatabaseName(NameArIndex);
        builder.HasIndex(custody => custody.NameEn).IsUnique().HasDatabaseName(NameEnIndex);
        builder.HasIndex(custody => new { custody.HolderUserId, custody.Status }, "IX_OfficeCustody_Holder_Status");
    }
}

internal sealed class OfficeCustodyPeriodConfiguration : IEntityTypeConfiguration<OfficeCustodyPeriod>
{
    /// <summary>One row per custody and month: a month is opened once, whatever the races.</summary>
    public const string MonthIndex = "UX_OfficeCustodyPeriod_Month";

    public void Configure(EntityTypeBuilder<OfficeCustodyPeriod> builder)
    {
        builder.ToTable(nameof(OfficeCustodyPeriod), table =>
        {
            table.HasCheckConstraint("CK_OfficeCustodyPeriod_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<OfficePeriodStatus>())})");
            table.HasCheckConstraint("CK_OfficeCustodyPeriod_Settlement",
                $"[Settlement] IS NULL OR [Settlement] IN ({SqlList.Of(Enum.GetNames<OfficePeriodSettlement>())})");
            table.HasCheckConstraint("CK_OfficeCustodyPeriod_Month", "[Month] BETWEEN 1 AND 12 AND [Year] BETWEEN 2000 AND 2200 AND [EndsOn] >= [StartsOn]");
            table.HasCheckConstraint("CK_OfficeCustodyPeriod_Planned", "[PlannedAllocation] >= 0");
            // A closed month always says how; an open one never does.
            table.HasCheckConstraint("CK_OfficeCustodyPeriod_Closed",
                "([Status] = 'Closed' AND [Settlement] IS NOT NULL AND [ClosedAt] IS NOT NULL) OR ([Status] <> 'Closed' AND [Settlement] IS NULL)");
        });

        builder.Property(period => period.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(period => period.Settlement).HasConversion<string>().HasMaxLength(20);
        builder.Property(period => period.ClosingNote).HasMaxLength(OfficeLengths.Note).IsUnicode();
        builder.Property(period => period.RowVersion).IsRowVersion();
        builder.Ignore(period => period.AcceptsRecords);

        builder.HasIndex(period => new { period.CustodyId, period.Year, period.Month }).IsUnique().HasDatabaseName(MonthIndex);
        builder.HasIndex(period => new { period.Status, period.CustodyId }, "IX_OfficeCustodyPeriod_Status");
        builder.HasOne<OfficeCustody>().WithMany().HasForeignKey(period => period.CustodyId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class OfficeLedgerEntryConfiguration : IEntityTypeConfiguration<OfficeLedgerEntry>
{
    /// <summary>One movement per source record: the same expense, funding, count or return is never posted twice.</summary>
    public const string SourceIndex = "UX_OfficeLedgerEntry_Source";

    /// <summary>An entry is reversed at most once.</summary>
    public const string ReversesIndex = "UX_OfficeLedgerEntry_Reverses";

    public void Configure(EntityTypeBuilder<OfficeLedgerEntry> builder)
    {
        builder.ToTable(nameof(OfficeLedgerEntry), table =>
        {
            table.HasCheckConstraint("CK_OfficeLedgerEntry_Kind", $"[Kind] IN ({SqlList.Of(Enum.GetNames<OfficeLedgerKind>())})");
            table.HasCheckConstraint("CK_OfficeLedgerEntry_Source", $"[SourceType] IN ({SqlList.Of(OfficeLedgerSources.All)})");
            table.HasCheckConstraint("CK_OfficeLedgerEntry_Amount", "[Amount] <> 0");
            // The sign follows the kind: allocations, funding, carried-in money and reversals bring money in;
            // expenses, returns and carried-out money take it out.
            table.HasCheckConstraint("CK_OfficeLedgerEntry_Sign",
                "([Kind] IN ('Allocation', 'Funding', 'CarryForwardIn', 'Reversal') AND [Amount] > 0) "
                + "OR ([Kind] IN ('Expense', 'Return', 'CarryForwardOut') AND [Amount] < 0) OR [Kind] = 'Adjustment'");
        });

        builder.Property(entry => entry.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(entry => entry.SourceType).HasMaxLength(OfficeLengths.Code);
        builder.Property(entry => entry.Reason).HasMaxLength(OfficeLengths.Note).IsUnicode();
        builder.Property(entry => entry.Reference).HasMaxLength(OfficeLengths.Reference).IsUnicode();

        builder.HasIndex(entry => new { entry.SourceType, entry.SourceId }).IsUnique().HasDatabaseName(SourceIndex);
        builder.HasIndex(entry => entry.ReversesEntryId).IsUnique().HasFilter("[ReversesEntryId] IS NOT NULL").HasDatabaseName(ReversesIndex);
        builder.HasIndex(entry => new { entry.CustodyId, entry.EntryDate, entry.Id }, "IX_OfficeLedgerEntry_Custody_Date");
        builder.HasIndex(entry => new { entry.PeriodId, entry.Kind }, "IX_OfficeLedgerEntry_Period_Kind");

        builder.HasOne<OfficeCustody>().WithMany().HasForeignKey(entry => entry.CustodyId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<OfficeCustodyPeriod>().WithMany().HasForeignKey(entry => entry.PeriodId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<OfficeLedgerEntry>().WithMany().HasForeignKey(entry => entry.ReversesEntryId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class OfficeCashReturnConfiguration : IEntityTypeConfiguration<OfficeCashReturn>
{
    public const string KeyIndex = "UX_OfficeCashReturn_IdempotencyKey";

    public void Configure(EntityTypeBuilder<OfficeCashReturn> builder)
    {
        builder.ToTable(nameof(OfficeCashReturn), table => table.HasCheckConstraint("CK_OfficeCashReturn_Amount", "[Amount] > 0"));

        builder.Property(cashReturn => cashReturn.Reason).HasMaxLength(OfficeLengths.Note).IsUnicode();
        builder.Property(cashReturn => cashReturn.Reference).HasMaxLength(OfficeLengths.Reference).IsUnicode();

        builder.HasIndex(cashReturn => cashReturn.IdempotencyKey).IsUnique().HasDatabaseName(KeyIndex);
        builder.HasIndex(cashReturn => cashReturn.CustodyId, "IX_OfficeCashReturn_Custody");
        builder.HasOne<OfficeCustody>().WithMany().HasForeignKey(cashReturn => cashReturn.CustodyId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<OfficeCustodyPeriod>().WithMany().HasForeignKey(cashReturn => cashReturn.PeriodId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class OfficeFundingRequestConfiguration : IEntityTypeConfiguration<OfficeFundingRequest>
{
    public const string KeyIndex = "UX_OfficeFundingRequest_IdempotencyKey";

    /// <summary>A month has at most one live allocation: it is never asked from Finance twice.</summary>
    public const string AllocationIndex = "UX_OfficeFundingRequest_Allocation";

    public void Configure(EntityTypeBuilder<OfficeFundingRequest> builder)
    {
        builder.ToTable(nameof(OfficeFundingRequest), table =>
        {
            table.HasCheckConstraint("CK_OfficeFundingRequest_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<OfficeFundingStatus>())})");
            table.HasCheckConstraint("CK_OfficeFundingRequest_Requested", "[RequestedAmount] > 0");
            // Partial approval only: never more than asked, never more received than paid out.
            table.HasCheckConstraint("CK_OfficeFundingRequest_Approved", "[ApprovedAmount] IS NULL OR ([ApprovedAmount] > 0 AND [ApprovedAmount] <= [RequestedAmount])");
            table.HasCheckConstraint("CK_OfficeFundingRequest_Received", "[ReceivedAmount] IS NULL OR ([ReceivedAmount] > 0 AND [ReceivedAmount] <= [DisbursedAmount])");
            table.HasCheckConstraint("CK_OfficeFundingRequest_Kind", $"[Kind] IN ({SqlList.Of(Enum.GetNames<OfficeFundingKind>())})");
            // An allocation always names its month; a replenishment counts in the month it is received.
            table.HasCheckConstraint("CK_OfficeFundingRequest_Period",
                "([Kind] = 'MonthlyAllocation' AND [PeriodId] IS NOT NULL) OR ([Kind] = 'Replenishment' AND [PeriodId] IS NULL)");
        });

        builder.Property(funding => funding.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(funding => funding.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(funding => funding.Reason).HasMaxLength(OfficeLengths.Note).IsUnicode();
        builder.Property(funding => funding.ReviewNote).HasMaxLength(OfficeLengths.Note).IsUnicode();
        builder.Property(funding => funding.ReceiptNote).HasMaxLength(OfficeLengths.Note).IsUnicode();
        builder.Property(funding => funding.PaymentRequestNumber).HasMaxLength(30);
        builder.Property(funding => funding.PaymentMethod).HasMaxLength(30);
        builder.Property(funding => funding.DisbursementReference).HasMaxLength(OfficeLengths.Reference).IsUnicode();
        builder.Property(funding => funding.RowVersion).IsRowVersion();

        builder.HasIndex(funding => funding.IdempotencyKey).IsUnique().HasDatabaseName(KeyIndex);
        builder.HasIndex(funding => new { funding.CustodyId, funding.Status }, "IX_OfficeFundingRequest_Custody_Status");
        builder.HasIndex(funding => funding.PeriodId).IsUnique()
            .HasFilter("[PeriodId] IS NOT NULL AND [Status] <> 'Rejected' AND [Status] <> 'Cancelled'").HasDatabaseName(AllocationIndex);
        builder.HasOne<OfficeCustody>().WithMany().HasForeignKey(funding => funding.CustodyId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<OfficeCustodyPeriod>().WithMany().HasForeignKey(funding => funding.PeriodId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class OfficeExpenseConfiguration : IEntityTypeConfiguration<OfficeExpense>
{
    public const string KeyIndex = "UX_OfficeExpense_IdempotencyKey";

    /// <summary>A repair or purchase request has one live expense: its cost is never deducted twice.</summary>
    public const string SpendIndex = "UX_OfficeExpense_SpendRequest";

    public void Configure(EntityTypeBuilder<OfficeExpense> builder)
    {
        builder.ToTable(nameof(OfficeExpense), table =>
        {
            table.HasCheckConstraint("CK_OfficeExpense_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<OfficeExpenseStatus>())})");
            table.HasCheckConstraint("CK_OfficeExpense_Posting", $"[PostingStatus] IN ({SqlList.Of(Enum.GetNames<OfficePostingStatus>())})");
            table.HasCheckConstraint("CK_OfficeExpense_Category", $"[Category] IN ({SqlList.Of(OfficeExpenseCategories.All)})");
            table.HasCheckConstraint("CK_OfficeExpense_Numbers",
                "[Quantity] > 0 AND [UnitPrice] >= 0 AND [Discount] >= 0 AND [Tax] >= 0 AND [Amount] > 0");
            table.HasCheckConstraint("CK_OfficeExpense_Approvals", "[RequiredApprovals] IN (1, 2) AND [Round] >= 1");
            // Only an approved expense is ever posted.
            table.HasCheckConstraint("CK_OfficeExpense_PostedApproved", "[PostingStatus] = 'NotPosted' OR [Status] = 'Approved'");
        });

        builder.Property(expense => expense.Category).HasMaxLength(OfficeLengths.Code);
        builder.Property(expense => expense.Description).HasMaxLength(OfficeLengths.Description).IsUnicode();
        builder.Property(expense => expense.Purpose).HasMaxLength(OfficeLengths.Description).IsUnicode();
        builder.Property(expense => expense.Vendor).HasMaxLength(OfficeLengths.Vendor).IsUnicode();
        builder.Property(expense => expense.InvoiceNo).HasMaxLength(OfficeLengths.InvoiceNo).IsUnicode();
        builder.Property(expense => expense.Quantity).HasPrecision(18, 3);
        builder.Property(expense => expense.Unit).HasMaxLength(OfficeLengths.Unit).IsUnicode();
        builder.Property(expense => expense.PriceCheck).HasMaxLength(30);
        builder.Property(expense => expense.VariancePercent).HasPrecision(9, 2);
        builder.Property(expense => expense.PriceJustification).HasMaxLength(OfficeLengths.Note).IsUnicode();
        builder.Property(expense => expense.ReviewReasons).HasMaxLength(OfficeLengths.ReviewReasons);
        builder.Property(expense => expense.DuplicateOf).HasMaxLength(OfficeLengths.DuplicateOf);
        builder.Property(expense => expense.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(expense => expense.PostingStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(expense => expense.DecisionNote).HasMaxLength(OfficeLengths.Note).IsUnicode();
        builder.Property(expense => expense.ReversalReason).HasMaxLength(OfficeLengths.Note).IsUnicode();
        builder.Property(expense => expense.RowVersion).IsRowVersion();
        builder.Ignore(expense => expense.InReview);
        builder.Ignore(expense => expense.IsPendingCommitment);

        builder.HasIndex(expense => expense.IdempotencyKey).IsUnique().HasDatabaseName(KeyIndex);
        builder.HasIndex(expense => expense.SpendRequestId).IsUnique()
            .HasFilter("[SpendRequestId] IS NOT NULL AND [Status] <> 'Rejected'").HasDatabaseName(SpendIndex);
        builder.HasIndex(expense => new { expense.CustodyId, expense.Status, expense.ExpenseDate }, "IX_OfficeExpense_Custody_Status_Date");
        builder.HasIndex(expense => new { expense.Vendor, expense.InvoiceNo }, "IX_OfficeExpense_Vendor_Invoice");
        builder.HasIndex(expense => new { expense.ExpenseDate, expense.Amount }, "IX_OfficeExpense_Date_Amount");
        builder.HasIndex(expense => expense.PriceExceptionId, "IX_OfficeExpense_PriceException");
        builder.HasIndex(expense => new { expense.PeriodId, expense.Status }, "IX_OfficeExpense_Period_Status");

        builder.HasOne<OfficeCustody>().WithMany().HasForeignKey(expense => expense.CustodyId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<OfficeCustodyPeriod>().WithMany().HasForeignKey(expense => expense.PeriodId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<OfficeSpendRequest>().WithMany().HasForeignKey(expense => expense.SpendRequestId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<OfficeLedgerEntry>().WithMany().HasForeignKey(expense => expense.LedgerEntryId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<OfficeLedgerEntry>().WithMany().HasForeignKey(expense => expense.ReversalEntryId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class OfficeExpenseApprovalConfiguration : IEntityTypeConfiguration<OfficeExpenseApproval>
{
    /// <summary>One approval per person per round: a double tap, or the same person twice, never counts as two.</summary>
    public const string OnceIndex = "UX_OfficeExpenseApproval_Once";

    public void Configure(EntityTypeBuilder<OfficeExpenseApproval> builder)
    {
        builder.ToTable(nameof(OfficeExpenseApproval));

        builder.Property(approval => approval.ApproverRole).HasMaxLength(30);
        builder.Property(approval => approval.Note).HasMaxLength(OfficeLengths.Note).IsUnicode();

        builder.HasIndex(approval => new { approval.ExpenseId, approval.Round, approval.ApproverUserId }).IsUnique().HasDatabaseName(OnceIndex);
        builder.HasOne<OfficeExpense>().WithMany().HasForeignKey(approval => approval.ExpenseId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class OfficeSpendRequestConfiguration : IEntityTypeConfiguration<OfficeSpendRequest>
{
    public const string KeyIndex = "UX_OfficeSpendRequest_IdempotencyKey";

    public void Configure(EntityTypeBuilder<OfficeSpendRequest> builder)
    {
        builder.ToTable(nameof(OfficeSpendRequest), table =>
        {
            table.HasCheckConstraint("CK_OfficeSpendRequest_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<OfficeSpendStatus>())})");
            table.HasCheckConstraint("CK_OfficeSpendRequest_Kind", $"[Kind] IN ({SqlList.Of(Enum.GetNames<OfficeSpendKind>())})");
            table.HasCheckConstraint("CK_OfficeSpendRequest_Estimate", "[EstimatedAmount] IS NULL OR [EstimatedAmount] > 0");
            table.HasCheckConstraint("CK_OfficeSpendRequest_Emergency", "[IsEmergency] = 0 OR [EmergencyReason] IS NOT NULL");
        });

        builder.Property(spend => spend.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(spend => spend.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(spend => spend.Title).HasMaxLength(OfficeLengths.Name).IsUnicode();
        builder.Property(spend => spend.Description).HasMaxLength(OfficeLengths.Note).IsUnicode();
        builder.Property(spend => spend.Location).HasMaxLength(OfficeLengths.Name).IsUnicode();
        builder.Property(spend => spend.Asset).HasMaxLength(OfficeLengths.Name).IsUnicode();
        builder.Property(spend => spend.Supplier).HasMaxLength(OfficeLengths.Vendor).IsUnicode();
        builder.Property(spend => spend.EmergencyReason).HasMaxLength(OfficeLengths.Note).IsUnicode();
        builder.Property(spend => spend.ReviewNote).HasMaxLength(OfficeLengths.Note).IsUnicode();
        builder.Property(spend => spend.AfterEvidenceWaivedReason).HasMaxLength(OfficeLengths.Note).IsUnicode();
        builder.Property(spend => spend.RowVersion).IsRowVersion();

        builder.HasIndex(spend => spend.IdempotencyKey).IsUnique().HasDatabaseName(KeyIndex);
        builder.HasIndex(spend => new { spend.CustodyId, spend.Status }, "IX_OfficeSpendRequest_Custody_Status");
        builder.HasOne<OfficeCustody>().WithMany().HasForeignKey(spend => spend.CustodyId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class OfficeAttachmentConfiguration : IEntityTypeConfiguration<OfficeAttachment>
{
    public const string KeyIndex = "UX_OfficeAttachment_IdempotencyKey";

    public void Configure(EntityTypeBuilder<OfficeAttachment> builder)
    {
        builder.ToTable(nameof(OfficeAttachment), table =>
        {
            table.HasCheckConstraint("CK_OfficeAttachment_Purpose", $"[Purpose] IN ({SqlList.Of(Enum.GetNames<OfficeAttachmentPurpose>())})");
            table.HasCheckConstraint("CK_OfficeAttachment_Owner",
                $"([OwnerType] IS NULL AND [OwnerId] IS NULL) OR ([OwnerType] IN ({SqlList.Of(OfficeAttachmentOwners.All)}) AND [OwnerId] IS NOT NULL)");
        });

        builder.Property(attachment => attachment.Purpose).HasConversion<string>().HasMaxLength(20);
        builder.Property(attachment => attachment.OwnerType).HasMaxLength(OfficeLengths.Code);
        builder.Property(attachment => attachment.StorageKey).HasMaxLength(OfficeLengths.StorageKey);
        builder.Property(attachment => attachment.FileName).HasMaxLength(OfficeLengths.FileName).IsUnicode();
        builder.Property(attachment => attachment.ContentType).HasMaxLength(OfficeLengths.ContentType);
        builder.Property(attachment => attachment.Sha256).HasMaxLength(64).IsFixedLength();
        builder.Property(attachment => attachment.CaptureSource).HasMaxLength(20);
        builder.Property(attachment => attachment.DeviceModel).HasMaxLength(OfficeLengths.Device).IsUnicode();
        builder.Property(attachment => attachment.QualityNote).HasMaxLength(OfficeLengths.Description).IsUnicode();

        builder.HasIndex(attachment => attachment.IdempotencyKey).IsUnique().HasDatabaseName(KeyIndex);
        builder.HasIndex(attachment => new { attachment.OwnerType, attachment.OwnerId }, "IX_OfficeAttachment_Owner");
        builder.HasIndex(attachment => attachment.Sha256, "IX_OfficeAttachment_Sha256");
    }
}

internal sealed class OfficeCashCountConfiguration : IEntityTypeConfiguration<OfficeCashCount>
{
    public const string KeyIndex = "UX_OfficeCashCount_IdempotencyKey";

    public void Configure(EntityTypeBuilder<OfficeCashCount> builder)
    {
        builder.ToTable(nameof(OfficeCashCount), table =>
        {
            table.HasCheckConstraint("CK_OfficeCashCount_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<OfficeCashCountStatus>())})");
            table.HasCheckConstraint("CK_OfficeCashCount_Counted", "[CountedAmount] >= 0");
            table.HasCheckConstraint("CK_OfficeCashCount_Difference", "[Difference] = [CountedAmount] - [RecordedBalance]");
        });

        builder.Property(count => count.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(count => count.Explanation).HasMaxLength(OfficeLengths.Note).IsUnicode();
        builder.Property(count => count.ReviewNote).HasMaxLength(OfficeLengths.Note).IsUnicode();
        builder.Property(count => count.RowVersion).IsRowVersion();

        builder.HasIndex(count => count.IdempotencyKey).IsUnique().HasDatabaseName(KeyIndex);
        builder.HasIndex(count => new { count.CustodyId, count.CountedOn }, "IX_OfficeCashCount_Custody_Date");
        builder.HasOne<OfficeCustody>().WithMany().HasForeignKey(count => count.CustodyId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class OfficeAlertConfiguration : IEntityTypeConfiguration<OfficeAlert>
{
    public const string KindColumn = "Kind";

    private static readonly (Type Type, string Kind)[] Kinds =
    [
        (typeof(OfficeExpenseSubmittedAlert), "ExpenseSubmitted"),
        (typeof(OfficeExpensePostedAlert), "ExpensePosted"),
        (typeof(OfficeFundingRequestedAlert), "FundingRequested"),
        (typeof(OfficeFundsDisbursedAlert), "FundsDisbursed"),
        (typeof(OfficeLowBalanceAlert), "LowBalance"),
        (typeof(OfficeDiscrepancyAlert), "Discrepancy"),
        (typeof(OfficeSpendRequestedAlert), "SpendRequested"),
        (typeof(OfficePeriodOpenedAlert), "PeriodOpened"),
        (typeof(OfficePeriodEndedAlert), "PeriodEnded"),
    ];

    public void Configure(EntityTypeBuilder<OfficeAlert> builder)
    {
        builder.ToTable(nameof(OfficeAlert), table =>
            table.HasCheckConstraint("CK_OfficeAlert_Kind", $"[Kind] IN ({SqlList.Of(Kinds.Select(kind => kind.Kind))})"));

        var discriminator = builder.HasDiscriminator<string>(KindColumn);
        foreach (var (type, kind) in Kinds)
        {
            discriminator.HasValue(type, kind);
        }

        builder.Property<string>(KindColumn).HasMaxLength(30).IsUnicode(false);
        builder.Property(alert => alert.SourceType).HasMaxLength(OfficeLengths.Code);

        // Not unique: a resubmitted expense alerts its reviewers again. Duplicates are prevented where alerts are
        // written (inside the record's own transaction, which idempotency keys make run once).
        builder.HasIndex([nameof(OfficeAlert.SourceType), nameof(OfficeAlert.SourceId), KindColumn], "IX_OfficeAlert_Source");
        builder.HasIndex(alert => new { alert.RecipientUserId, alert.CreatedAt }, "IX_OfficeAlert_Recipient");
    }
}
