using AlMotaheda.Modules.Finance.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AlMotaheda.Modules.Finance.Data.Configurations;

internal sealed class PaymentRequestConfiguration : IEntityTypeConfiguration<PaymentRequest>
{
    public const string IdempotencyKeyIndex = PaymentRequestIndexes.IdempotencyKey;
    public const string SourcePaymentIndex = "UX_PaymentRequest_SourcePayment";

    public const int ReferenceMaxLength = 100;
    public const int NotesMaxLength = 1000;
    public const int NoteMaxLength = 1000;
    public const int PayloadMaxLength = 1000;

    public void Configure(EntityTypeBuilder<PaymentRequest> builder)
    {
        builder.ToTable(nameof(PaymentRequest), table =>
        {
            table.HasCheckConstraint("CK_PaymentRequest_Status", $"[Status] IN ({SqlList.Of(Enum.GetNames<PaymentRequestStatus>())})");
            table.HasCheckConstraint("CK_PaymentRequest_Origin", $"[Origin] IN ({SqlList.Of(Enum.GetNames<RequestOrigin>())})");
            table.HasCheckConstraint("CK_PaymentRequest_SourceType", $"[SourceType] IN ({SqlList.Of(PaymentSourceTypes.All)})");
            table.HasCheckConstraint("CK_PaymentRequest_PayeeType", $"[PayeeType] IN ({SqlList.Of(PayeeTypes.All)})");
            table.HasCheckConstraint("CK_PaymentRequest_Purpose", $"[Purpose] IN ({SqlList.Of(PaymentPurposes.All)})");
            table.HasCheckConstraint("CK_PaymentRequest_Method", $"[Method] IN ({SqlList.Of(PaymentMethods.All)})");
            table.HasCheckConstraint("CK_PaymentRequest_Amount", "[Amount] > 0");
            // Cash out of a custody is cash: a bank transfer or cheque never names a custody account.
            table.HasCheckConstraint("CK_PaymentRequest_CustodySource", "[SourceCustodyAccountId] IS NULL OR [Method] = 'Cash'");
            // Paid and Reversed requests always know when the money left and which payment row it became.
            table.HasCheckConstraint("CK_PaymentRequest_Paid",
                "[Status] NOT IN ('Paid', 'Reversed') OR ([PaidOn] IS NOT NULL AND [SourcePaymentId] IS NOT NULL)");
        });

        // PAY-000123: deterministic, so it is stored and indexed; more than 999999 requests simply get more digits.
        builder.Property(request => request.Number)
            .HasMaxLength(20)
            .HasComputedColumnSql("CONCAT('PAY-', REPLICATE('0', 6 - LEN([Id])), [Id])", stored: true);
        builder.HasIndex(request => request.Number).IsUnique().HasDatabaseName("UX_PaymentRequest_Number");

        builder.Property(request => request.SourceType).HasMaxLength(30);
        builder.Property(request => request.PayeeType).HasMaxLength(30);
        builder.Property(request => request.Purpose).HasMaxLength(30);
        builder.Property(request => request.Method).HasMaxLength(30);
        builder.Property(request => request.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(request => request.Origin).HasConversion<string>().HasMaxLength(30);
        builder.Property(request => request.Reference).HasMaxLength(ReferenceMaxLength).IsUnicode();
        builder.Property(request => request.Notes).HasMaxLength(NotesMaxLength).IsUnicode();
        builder.Property(request => request.Payload).HasMaxLength(PayloadMaxLength);
        builder.Property(request => request.DecisionNote).HasMaxLength(NoteMaxLength).IsUnicode();
        builder.Property(request => request.BankReference).HasMaxLength(ReferenceMaxLength);
        builder.Property(request => request.FailureReason).HasMaxLength(NoteMaxLength).IsUnicode();
        builder.Property(request => request.PaymentReference).HasMaxLength(ReferenceMaxLength).IsUnicode();
        builder.Property(request => request.CloseReason).HasMaxLength(NoteMaxLength).IsUnicode();
        builder.Property(request => request.RowVersion).IsRowVersion();

        builder.HasIndex(request => request.IdempotencyKey).IsUnique().HasDatabaseName(IdempotencyKeyIndex);

        // A paid source payment belongs to one request, so it can't be settled twice. Only Paid requests count: an
        // equipment cost whose payment was voided (its request Reversed) can be paid again under a new request.
        builder.HasIndex(request => new { request.SourceType, request.SourcePaymentId })
            .IsUnique()
            .HasFilter("[SourcePaymentId] IS NOT NULL AND [Status] = 'Paid'")
            .HasDatabaseName(SourcePaymentIndex);

        // The lists: by status (the Executive's queue), by payee, by project.
        builder.HasIndex(request => new { request.Status, request.Id }, "IX_PaymentRequest_Status");
        builder.HasIndex(request => new { request.PayeeType, request.PayeeId }, "IX_PaymentRequest_Payee");
        builder.HasIndex(request => new { request.ProjectId, request.Id }, "IX_PaymentRequest_Project");
        builder.HasIndex(request => new { request.SourceCustodyAccountId, request.Status }, "IX_PaymentRequest_SourceCustody");

        builder.HasOne<CustodyAccount>().WithMany().HasForeignKey(request => request.SourceCustodyAccountId).OnDelete(DeleteBehavior.NoAction);

        builder.HasMany(request => request.Lines).WithOne().HasForeignKey(line => line.RequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasMany(request => request.Events).WithOne().HasForeignKey(entry => entry.RequestId).OnDelete(DeleteBehavior.Restrict);
    }
}

internal sealed class PaymentRequestLineConfiguration : IEntityTypeConfiguration<PaymentRequestLine>
{
    public void Configure(EntityTypeBuilder<PaymentRequestLine> builder)
    {
        builder.ToTable(nameof(PaymentRequestLine), table =>
            table.HasCheckConstraint("CK_PaymentRequestLine_Amount", "[Amount] > 0"));

        builder.Property(line => line.RecordType).HasMaxLength(30);

        // "What do open requests hold on these records?" is asked before every new request.
        builder.HasIndex(line => new { line.RecordType, line.RecordId }, "IX_PaymentRequestLine_Record")
            .IncludeProperties(line => new { line.RequestId, line.Amount });
        builder.HasIndex(line => new { line.RequestId, line.RecordType, line.RecordId })
            .IsUnique()
            .HasDatabaseName("UX_PaymentRequestLine_Record");
    }
}

internal sealed class PaymentRequestEventConfiguration : IEntityTypeConfiguration<PaymentRequestEvent>
{
    public void Configure(EntityTypeBuilder<PaymentRequestEvent> builder)
    {
        builder.ToTable(nameof(PaymentRequestEvent));

        builder.Property(entry => entry.Action).HasMaxLength(30);
        builder.Property(entry => entry.FromStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(entry => entry.ToStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(entry => entry.Note).HasMaxLength(PaymentRequestConfiguration.NoteMaxLength).IsUnicode();

        builder.HasIndex(entry => entry.RequestId, "IX_PaymentRequestEvent_RequestId");
    }
}

internal sealed class LedgerEntryConfiguration : IEntityTypeConfiguration<LedgerEntry>
{
    public void Configure(EntityTypeBuilder<LedgerEntry> builder)
    {
        builder.ToTable(nameof(LedgerEntry), table =>
        {
            table.HasCheckConstraint("CK_LedgerEntry_Account", $"[Account] IN ({SqlList.Of(Enum.GetNames<LedgerAccount>())})");
            table.HasCheckConstraint("CK_LedgerEntry_Amount", "[Amount] <> 0");

            // Every entry comes from a paid request or from an internal custody transfer; a custody entry names its account.
            table.HasCheckConstraint("CK_LedgerEntry_Origin", "[PaymentRequestId] IS NOT NULL OR [CustodyTransferId] IS NOT NULL");
            table.HasCheckConstraint("CK_LedgerEntry_CustodyAccount", "([Account] = 'Custody' AND [CustodyAccountId] IS NOT NULL) OR ([Account] <> 'Custody' AND [CustodyAccountId] IS NULL)");
        });

        builder.Property(entry => entry.Account).HasConversion<string>().HasMaxLength(30);
        builder.Property(entry => entry.CounterpartyType).HasMaxLength(30);
        builder.Property(entry => entry.Purpose).HasMaxLength(30);
        builder.Property(entry => entry.SourceType).HasMaxLength(30);

        builder.HasOne<PaymentRequest>().WithMany().HasForeignKey(entry => entry.PaymentRequestId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<LedgerEntry>().WithMany().HasForeignKey(entry => entry.ReversesEntryId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CustodyTransfer>().WithMany().HasForeignKey(entry => entry.CustodyTransferId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CustodyAccount>().WithMany().HasForeignKey(entry => entry.CustodyAccountId).OnDelete(DeleteBehavior.Restrict);

        // An entry is reversed at most once.
        builder.HasIndex(entry => entry.ReversesEntryId).IsUnique().HasFilter("[ReversesEntryId] IS NOT NULL")
            .HasDatabaseName("UX_LedgerEntry_Reverses");

        // The reports: by date, by project and date, by counterparty.
        builder.HasIndex(entry => new { entry.EntryDate, entry.Id }, "IX_LedgerEntry_Date");
        builder.HasIndex(entry => new { entry.ProjectId, entry.EntryDate }, "IX_LedgerEntry_Project_Date");
        builder.HasIndex(entry => new { entry.CounterpartyType, entry.CounterpartyId, entry.EntryDate }, "IX_LedgerEntry_Counterparty");
        builder.HasIndex(entry => entry.PaymentRequestId, "IX_LedgerEntry_PaymentRequestId");
        builder.HasIndex(entry => entry.CustodyTransferId, "IX_LedgerEntry_CustodyTransferId");
        builder.HasIndex(entry => new { entry.CustodyAccountId, entry.EntryDate }, "IX_LedgerEntry_CustodyAccount_Date");
        builder.HasIndex(entry => new { entry.Account, entry.EntryDate }, "IX_LedgerEntry_Account_Date");
    }
}
