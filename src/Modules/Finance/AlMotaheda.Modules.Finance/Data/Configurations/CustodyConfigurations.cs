using AlMotaheda.Modules.Finance.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Finance.Data.Configurations;

internal sealed class TreasuryAccountConfiguration : IEntityTypeConfiguration<TreasuryAccount>
{
    public const string AccountIndex = "UX_TreasuryAccount_Account";
    public const int NameMaxLength = 100;
    public const int NotesMaxLength = 500;

    /// <summary>The two accounts exist from the start, with no opening balance until an Executive sets one.</summary>
    public static readonly DateOnly SeedOpeningDate = new(2026, 1, 1);

    public void Configure(EntityTypeBuilder<TreasuryAccount> builder)
    {
        builder.ToTable(nameof(TreasuryAccount), table =>
        {
            table.HasCheckConstraint("CK_TreasuryAccount_Account", "[Account] IN ('Bank', 'Cash')");
            table.HasCheckConstraint("CK_TreasuryAccount_Opening",
                "([OpeningSetAt] IS NULL AND [OpeningSetBy] IS NULL) OR ([OpeningSetAt] IS NOT NULL AND [OpeningSetBy] IS NOT NULL)");
        });

        builder.Property(account => account.Account).HasConversion<string>().HasMaxLength(30);
        builder.Property(account => account.NameAr).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(account => account.NameEn).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(account => account.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(account => account.RowVersion).IsRowVersion();

        builder.HasIndex(account => account.Account).IsUnique().HasDatabaseName(AccountIndex);

        builder.HasData(
            new TreasuryAccount { Id = 1, Account = LedgerAccount.Bank, NameAr = "البنك", NameEn = "Bank", OpeningBalance = 0, OpeningDate = SeedOpeningDate },
            new TreasuryAccount { Id = 2, Account = LedgerAccount.Cash, NameAr = "الخزينة", NameEn = "Cash", OpeningBalance = 0, OpeningDate = SeedOpeningDate });
    }
}

internal sealed class CustodyAccountConfiguration : IEntityTypeConfiguration<CustodyAccount>
{
    /// <summary>One active account per kind, holder and reference (409 <c>custody_account_exists</c>).</summary>
    public const string ActiveIndex = "UX_CustodyAccount_Active";

    public const string HolderForeignKey = "FK_CustodyAccount_User_HolderUserId";

    public const int NameMaxLength = 200;
    public const int NotesMaxLength = 1000;
    public const int ReasonMaxLength = 500;

    public void Configure(EntityTypeBuilder<CustodyAccount> builder)
    {
        builder.ToTable(nameof(CustodyAccount), table =>
        {
            table.HasCheckConstraint("CK_CustodyAccount_Kind", $"[Kind] IN ({SqlList.Of(CustodyAccountKinds.All)})");
            table.HasCheckConstraint("CK_CustodyAccount_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<CustodyAccountStatus>())})");

            // What each kind must name: an accountant's custody has a holder and no reference; an engineer's project
            // custody and a payroll batch have both; a vehicle's has only the vehicle (its driver is in Fleet).
            table.HasCheckConstraint(
                "CK_CustodyAccount_KindFields",
                "([Kind] = 'Accountant' AND [HolderUserId] IS NOT NULL AND [ReferenceId] IS NULL) "
                + "OR ([Kind] IN ('EngineerProject', 'PayrollBatch', 'Office') AND [HolderUserId] IS NOT NULL AND [ReferenceId] IS NOT NULL) "
                + "OR ([Kind] = 'Vehicle' AND [HolderUserId] IS NULL AND [ReferenceId] IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_CustodyAccount_Closed",
                "([Status] = 'Active' AND [ClosedAt] IS NULL AND [ClosedBy] IS NULL) OR ([Status] = 'Closed' AND [ClosedAt] IS NOT NULL AND [ClosedBy] IS NOT NULL)");
        });

        builder.Property(account => account.Number)
            .HasMaxLength(20)
            .HasComputedColumnSql("CONCAT('CA-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true);
        builder.HasIndex(account => account.Number).IsUnique().HasDatabaseName("UX_CustodyAccount_Number");

        builder.Property(account => account.Kind).HasMaxLength(30);
        builder.Property(account => account.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(account => account.NameAr).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(account => account.NameEn).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(account => account.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(account => account.CloseReason).HasMaxLength(ReasonMaxLength).IsUnicode();
        builder.Property(account => account.RowVersion).IsRowVersion();

        builder.HasIndex(account => new { account.Kind, account.HolderUserId, account.ReferenceId })
            .IsUnique()
            .HasFilter("[Status] = 'Active'")
            .HasDatabaseName(ActiveIndex);
        builder.HasIndex(account => account.HolderUserId, "IX_CustodyAccount_HolderUserId");
        builder.HasIndex(account => account.ParentAccountId, "IX_CustodyAccount_ParentAccountId");

        builder.HasOne<CustodyAccount>().WithMany().HasForeignKey(account => account.ParentAccountId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class CustodyMovementConfiguration : IEntityTypeConfiguration<CustodyMovement>
{
    /// <summary>One movement per record per account: the same transfer or payment never moves the money twice.</summary>
    public const string SourceIndex = "UX_CustodyMovement_Account_Source";

    public const string ReversesIndex = "UX_CustodyMovement_Reverses";

    public const int ReasonMaxLength = 500;
    public const int ReferenceMaxLength = 100;

    public void Configure(EntityTypeBuilder<CustodyMovement> builder)
    {
        builder.ToTable(nameof(CustodyMovement), table =>
        {
            table.HasCheckConstraint("CK_CustodyMovement_Kind", $"[Kind] IN ({SqlList.Of(Enum.GetNames<CustodyMovementKind>())})");
            table.HasCheckConstraint("CK_CustodyMovement_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<CustodyMovementStatus>())})");
            table.HasCheckConstraint("CK_CustodyMovement_SourceType", $"[SourceType] IN ({SqlList.Of(CustodyMovementSources.All)})");
            table.HasCheckConstraint("CK_CustodyMovement_Amount", "[Amount] <> 0");

            // Money in is always final; only an outflow can wait for its record's review.
            table.HasCheckConstraint("CK_CustodyMovement_Pending", "[Status] = 'Posted' OR [Amount] < 0");
        });

        builder.Property(movement => movement.Kind).HasConversion<string>().HasMaxLength(30);
        builder.Property(movement => movement.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(movement => movement.SourceType).HasMaxLength(30);
        builder.Property(movement => movement.Reason).HasMaxLength(ReasonMaxLength).IsUnicode();
        builder.Property(movement => movement.Reference).HasMaxLength(ReferenceMaxLength).IsUnicode();

        builder.HasIndex(movement => new { movement.AccountId, movement.SourceType, movement.SourceId }).IsUnique().HasDatabaseName(SourceIndex);
        builder.HasIndex(movement => movement.ReversesMovementId).IsUnique().HasFilter("[ReversesMovementId] IS NOT NULL").HasDatabaseName(ReversesIndex);
        builder.HasIndex(movement => new { movement.AccountId, movement.EntryDate }, "IX_CustodyMovement_Account_Date");
        builder.HasIndex(movement => movement.TransferId, "IX_CustodyMovement_TransferId");
        builder.HasIndex(movement => movement.PaymentRequestId, "IX_CustodyMovement_PaymentRequestId");

        builder.HasOne<CustodyAccount>().WithMany().HasForeignKey(movement => movement.AccountId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<CustodyTransfer>().WithMany().HasForeignKey(movement => movement.TransferId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<PaymentRequest>().WithMany().HasForeignKey(movement => movement.PaymentRequestId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<CustodyMovement>().WithMany().HasForeignKey(movement => movement.ReversesMovementId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class CustodyTransferConfiguration : IEntityTypeConfiguration<CustodyTransfer>
{
    public const string IdempotencyKeyIndex = "UX_CustodyTransfer_IdempotencyKey";

    public const int ReferenceMaxLength = 100;
    public const int NotesMaxLength = 1000;
    public const int NoteMaxLength = 1000;
    public const int EvidenceMaxLength = 200;

    public void Configure(EntityTypeBuilder<CustodyTransfer> builder)
    {
        builder.ToTable(nameof(CustodyTransfer), table =>
        {
            table.HasCheckConstraint("CK_CustodyTransfer_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<CustodyTransferStatus>())})");
            table.HasCheckConstraint("CK_CustodyTransfer_Purpose", "[Purpose] IN ('CustodyAdvance', 'CustodyReturn')");
            table.HasCheckConstraint("CK_CustodyTransfer_Amount", "[Amount] > 0");

            // Each end is exactly one thing: a treasury account or a custody account.
            table.HasCheckConstraint(
                "CK_CustodyTransfer_Source",
                "([SourceKind] = 'Treasury' AND [SourceTreasuryAccount] IN ('Bank', 'Cash') AND [SourceAccountId] IS NULL) "
                + "OR ([SourceKind] = 'Custody' AND [SourceTreasuryAccount] IS NULL AND [SourceAccountId] IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_CustodyTransfer_Destination",
                "([DestinationKind] = 'Treasury' AND [DestinationTreasuryAccount] IN ('Bank', 'Cash') AND [DestinationAccountId] IS NULL) "
                + "OR ([DestinationKind] = 'Custody' AND [DestinationTreasuryAccount] IS NULL AND [DestinationAccountId] IS NOT NULL)");
            table.HasCheckConstraint("CK_CustodyTransfer_Ends", "[SourceKind] = 'Treasury' OR [DestinationKind] = 'Treasury' OR [SourceAccountId] <> [DestinationAccountId]");

            // A disbursed or received transfer always knows when and by whom.
            table.HasCheckConstraint(
                "CK_CustodyTransfer_Disbursed",
                "[Status] NOT IN ('Disbursed', 'Received', 'Reversed') OR ([DisbursedAt] IS NOT NULL AND [DisbursedBy] IS NOT NULL AND [DisbursedOn] IS NOT NULL)");
            table.HasCheckConstraint(
                "CK_CustodyTransfer_Received",
                "[Status] <> 'Received' OR ([ReceivedAt] IS NOT NULL AND [ReceivedBy] IS NOT NULL AND [ReceivedOn] IS NOT NULL)");
        });

        builder.Property(transfer => transfer.Number)
            .HasMaxLength(20)
            .HasComputedColumnSql("CONCAT('CT-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true);
        builder.HasIndex(transfer => transfer.Number).IsUnique().HasDatabaseName("UX_CustodyTransfer_Number");

        builder.Property(transfer => transfer.SourceKind).HasConversion<string>().HasMaxLength(30);
        builder.Property(transfer => transfer.SourceTreasuryAccount).HasConversion<string>().HasMaxLength(30);
        builder.Property(transfer => transfer.DestinationKind).HasConversion<string>().HasMaxLength(30);
        builder.Property(transfer => transfer.DestinationTreasuryAccount).HasConversion<string>().HasMaxLength(30);
        builder.Property(transfer => transfer.Purpose).HasMaxLength(30);
        builder.Property(transfer => transfer.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(transfer => transfer.Reference).HasMaxLength(ReferenceMaxLength).IsUnicode();
        builder.Property(transfer => transfer.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(transfer => transfer.EvidenceReference).HasMaxLength(EvidenceMaxLength).IsUnicode();
        builder.Property(transfer => transfer.DecisionNote).HasMaxLength(NoteMaxLength).IsUnicode();
        builder.Property(transfer => transfer.DisbursementReference).HasMaxLength(ReferenceMaxLength).IsUnicode();
        builder.Property(transfer => transfer.CloseReason).HasMaxLength(NoteMaxLength).IsUnicode();
        builder.Property(transfer => transfer.RowVersion).IsRowVersion();

        builder.HasIndex(transfer => transfer.IdempotencyKey).IsUnique().HasDatabaseName(IdempotencyKeyIndex);
        builder.HasIndex(transfer => new { transfer.Status, transfer.Id }, "IX_CustodyTransfer_Status");
        builder.HasIndex(transfer => new { transfer.SourceAccountId, transfer.Status }, "IX_CustodyTransfer_Source");
        builder.HasIndex(transfer => new { transfer.DestinationAccountId, transfer.Status }, "IX_CustodyTransfer_Destination");
        builder.HasIndex(transfer => transfer.PayrollBatchId, "IX_CustodyTransfer_PayrollBatchId");
        builder.HasIndex(transfer => transfer.ProjectId, "IX_CustodyTransfer_ProjectId");

        builder.HasOne<CustodyAccount>().WithMany().HasForeignKey(transfer => transfer.SourceAccountId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<CustodyAccount>().WithMany().HasForeignKey(transfer => transfer.DestinationAccountId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<PayrollBatch>().WithMany().HasForeignKey(transfer => transfer.PayrollBatchId).OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(transfer => transfer.Events).WithOne().HasForeignKey(entry => entry.TransferId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class CustodyTransferEventConfiguration : IEntityTypeConfiguration<CustodyTransferEvent>
{
    public void Configure(EntityTypeBuilder<CustodyTransferEvent> builder)
    {
        builder.ToTable(nameof(CustodyTransferEvent));

        builder.Property(entry => entry.Action).HasMaxLength(30);
        builder.Property(entry => entry.FromStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(entry => entry.ToStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(entry => entry.Note).HasMaxLength(CustodyTransferConfiguration.NoteMaxLength).IsUnicode();

        builder.HasIndex(entry => entry.TransferId, "IX_CustodyTransferEvent_TransferId");
    }
}

internal sealed class CustodyTransferAlertConfiguration : IEntityTypeConfiguration<CustodyTransferAlert>
{
    public const string OnceIndex = "UX_CustodyTransferAlert_Transfer_Recipient_Stage";
    public const string RecipientForeignKey = "FK_CustodyTransferAlert_User_RecipientUserId";

    public void Configure(EntityTypeBuilder<CustodyTransferAlert> builder)
    {
        builder.ToTable(nameof(CustodyTransferAlert), table =>
            table.HasCheckConstraint("CK_CustodyTransferAlert_Stage", "[Stage] IN ('Requested', 'Disbursed')"));

        builder.Property(alert => alert.Stage).HasMaxLength(30);
        builder.HasIndex(alert => new { alert.TransferId, alert.RecipientUserId, alert.Stage }).IsUnique().HasDatabaseName(OnceIndex);

        builder.HasOne(alert => alert.Transfer).WithMany().HasForeignKey(alert => alert.TransferId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class PayrollBatchConfiguration : IEntityTypeConfiguration<PayrollBatch>
{
    public const string AccountIndex = "UX_PayrollBatch_AccountId";
    public const string SalarySheetIndex = "UX_PayrollBatch_SalarySheetId";
    public const string HolderForeignKey = "FK_PayrollBatch_User_HolderUserId";

    public const int NameMaxLength = 200;
    public const int NotesMaxLength = 1000;

    public void Configure(EntityTypeBuilder<PayrollBatch> builder)
    {
        builder.ToTable(nameof(PayrollBatch), table =>
        {
            table.HasCheckConstraint("CK_PayrollBatch_Kind", $"[Kind] IN ({SqlList.Of(PayrollBatchKinds.All)})");
            table.HasCheckConstraint("CK_PayrollBatch_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<PayrollBatchStatus>())})");
            table.HasCheckConstraint("CK_PayrollBatch_Period", "[PeriodTo] >= [PeriodFrom]");
            table.HasCheckConstraint("CK_PayrollBatch_Sheet", "([Kind] = 'Salaries' AND [SalarySheetId] IS NOT NULL) OR ([Kind] <> 'Salaries' AND [SalarySheetId] IS NULL)");
        });

        builder.Property(batch => batch.Number)
            .HasMaxLength(20)
            .HasComputedColumnSql("CONCAT('PB-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true);
        builder.HasIndex(batch => batch.Number).IsUnique().HasDatabaseName("UX_PayrollBatch_Number");

        builder.Property(batch => batch.Kind).HasMaxLength(30);
        builder.Property(batch => batch.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(batch => batch.NameAr).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(batch => batch.NameEn).HasMaxLength(NameMaxLength).IsUnicode();
        builder.Property(batch => batch.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(batch => batch.CloseNote).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(batch => batch.RowVersion).IsRowVersion();

        builder.HasIndex(batch => batch.AccountId).IsUnique().HasDatabaseName(AccountIndex);
        builder.HasIndex(batch => batch.SalarySheetId).IsUnique().HasFilter("[SalarySheetId] IS NOT NULL").HasDatabaseName(SalarySheetIndex);
        builder.HasIndex(batch => new { batch.Status, batch.PeriodFrom }, "IX_PayrollBatch_Status_Period");

        builder.HasOne<CustodyAccount>().WithMany().HasForeignKey(batch => batch.AccountId).OnDelete(DeleteBehavior.NoAction);
        builder.HasMany(batch => batch.Allocations).WithOne().HasForeignKey(allocation => allocation.BatchId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class PayrollBatchAllocationConfiguration : IEntityTypeConfiguration<PayrollBatchAllocation>
{
    public const string ProjectIndex = "UX_PayrollBatchAllocation_Batch_Project";
    public const string ProjectForeignKey = "FK_PayrollBatchAllocation_Project_ProjectId";

    public void Configure(EntityTypeBuilder<PayrollBatchAllocation> builder)
    {
        builder.ToTable(nameof(PayrollBatchAllocation), table =>
        {
            table.HasCheckConstraint("CK_PayrollBatchAllocation_Amount", "[Amount] > 0");
            table.HasCheckConstraint("CK_PayrollBatchAllocation_Entitlement", "[EntitlementAtAllocation] >= 0");
        });

        builder.HasIndex(allocation => new { allocation.BatchId, allocation.ProjectId }).IsUnique().HasDatabaseName(ProjectIndex);
    }
}

internal sealed class CustodyCashCountConfiguration : IEntityTypeConfiguration<CustodyCashCount>
{
    public const string IdempotencyKeyIndex = "UX_CustodyCashCount_IdempotencyKey";
    public const int NotesMaxLength = 1000;
    public const int EvidenceMaxLength = 200;

    public void Configure(EntityTypeBuilder<CustodyCashCount> builder)
    {
        builder.ToTable(nameof(CustodyCashCount), table =>
        {
            table.HasCheckConstraint("CK_CustodyCashCount_Counted", "[CountedAmount] >= 0");
            table.HasCheckConstraint("CK_CustodyCashCount_Difference", "[Difference] = [CountedAmount] - [BookBalance]");
        });

        builder.Property(count => count.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(count => count.EvidenceReference).HasMaxLength(EvidenceMaxLength).IsUnicode();

        builder.HasIndex(count => count.IdempotencyKey).IsUnique().HasDatabaseName(IdempotencyKeyIndex);
        builder.HasIndex(count => new { count.AccountId, count.CountDate }, "IX_CustodyCashCount_Account_Date");

        builder.HasOne<CustodyAccount>().WithMany().HasForeignKey(count => count.AccountId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class CustodyAdjustmentConfiguration : IEntityTypeConfiguration<CustodyAdjustment>
{
    public const string IdempotencyKeyIndex = "UX_CustodyAdjustment_IdempotencyKey";
    public const int ReasonMaxLength = 500;
    public const int EvidenceMaxLength = 200;

    public void Configure(EntityTypeBuilder<CustodyAdjustment> builder)
    {
        builder.ToTable(nameof(CustodyAdjustment), table =>
            table.HasCheckConstraint("CK_CustodyAdjustment_Amount", "[Amount] <> 0"));

        builder.Property(adjustment => adjustment.Reason).HasMaxLength(ReasonMaxLength).IsUnicode();
        builder.Property(adjustment => adjustment.EvidenceReference).HasMaxLength(EvidenceMaxLength).IsUnicode();

        builder.HasIndex(adjustment => adjustment.IdempotencyKey).IsUnique().HasDatabaseName(IdempotencyKeyIndex);
        builder.HasIndex(adjustment => new { adjustment.AccountId, adjustment.EntryDate }, "IX_CustodyAdjustment_Account_Date");

        builder.HasOne<CustodyAccount>().WithMany().HasForeignKey(adjustment => adjustment.AccountId).OnDelete(DeleteBehavior.NoAction);
        builder.HasOne<CustodyCashCount>().WithMany().HasForeignKey(adjustment => adjustment.CashCountId).OnDelete(DeleteBehavior.NoAction);
    }
}

internal sealed class CustodyPeriodCloseConfiguration : IEntityTypeConfiguration<CustodyPeriodClose>
{
    public const string PeriodIndex = "UX_CustodyPeriodClose_Account_PeriodEnd";
    public const int NotesMaxLength = 1000;

    public void Configure(EntityTypeBuilder<CustodyPeriodClose> builder)
    {
        builder.ToTable(nameof(CustodyPeriodClose));

        builder.Property(close => close.Notes).HasMaxLength(NotesMaxLength).IsUnicode();

        builder.HasIndex(close => new { close.AccountId, close.PeriodEnd }).IsUnique().HasDatabaseName(PeriodIndex);

        builder.HasOne<CustodyAccount>().WithMany().HasForeignKey(close => close.AccountId).OnDelete(DeleteBehavior.NoAction);
    }
}
