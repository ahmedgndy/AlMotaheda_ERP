using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Finance.Requests;

namespace AlMotaheda.Modules.Finance.Tests.Unit;

/// <summary>The ledger entries of a paid request and of its reversal, to the piastre.</summary>
public sealed class LedgerPostingTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc);

    private static PaymentRequest Paid(string method, int? projectId, decimal amount, params (int ProjectId, decimal Amount)[] lines)
    {
        var request = PaymentRequest.Submit(
            PaymentSourceTypes.WorkerPayment, PayeeTypes.Worker, 8, projectId, PaymentPurposes.WorkerWages, amount, method,
            new DateOnly(2026, 10, 15), null, null, null, Guid.NewGuid(), 1, Now,
            lines.Select((line, index) => new PaymentRequestLine
            {
                RecordType = PaidRecordTypes.AttendanceLine, RecordId = 100 + index, ProjectId = line.ProjectId, Amount = line.Amount,
            }));
        request.ApproveAndPay(2, Now, null, PaymentMethods.GoesThroughBank(method) ? "MOCK" : null);
        request.MarkPaid(3, Now, new DateOnly(2026, 10, 16), null, 55);
        return request;
    }

    [Fact]
    public void A_payment_spanning_projects_gives_one_negative_entry_per_project()
    {
        var request = Paid(PaymentMethods.Cash, null, 600m, (3, 250.50m), (3, 250.50m), (7, 99m));

        var entries = LedgerPosting.Paid(request, Now, 3);

        Assert.Equal([(3, -501m), (7, -99m)], entries.Select(entry => (entry.ProjectId!.Value, entry.Amount)));
        Assert.All(entries, entry =>
        {
            Assert.Equal(LedgerAccount.Cash, entry.Account);
            Assert.Equal(new DateOnly(2026, 10, 16), entry.EntryDate);
            Assert.Equal((PayeeTypes.Worker, 8, 55), (entry.CounterpartyType, entry.CounterpartyId, entry.SourcePaymentId!.Value));
        });
        Assert.Equal(-600m, entries.Sum(entry => entry.Amount));
    }

    [Fact]
    public void Money_no_line_covers_goes_to_the_requests_project_on_the_bank_account()
    {
        // A subcontract advance: one contract line of 0 work; here, a request of 1,000 with a 400 line.
        var request = Paid(PaymentMethods.BankTransfer, 3, 1_000m, (3, 400m));

        var entry = Assert.Single(LedgerPosting.Paid(request, Now, 3));

        Assert.Equal((LedgerAccount.Bank, 3, -1_000m), (entry.Account, entry.ProjectId!.Value, entry.Amount));
    }

    [Fact]
    public void A_reversal_cancels_each_entry_once_and_never_reverses_a_reversal()
    {
        var originals = new[]
        {
            new LedgerEntry { Id = 1, Amount = -501m, ProjectId = 3, Account = LedgerAccount.Cash },
            new LedgerEntry { Id = 2, Amount = -99m, ProjectId = 7, Account = LedgerAccount.Cash },
            new LedgerEntry { Id = 3, Amount = 50m, ProjectId = 7, ReversesEntryId = 9 },
        };

        var reversal = LedgerPosting.Reversal(originals, new DateOnly(2026, 10, 20), Now, 4);

        Assert.Equal([(1L, 501m), (2L, 99m)], reversal.Select(entry => (entry.ReversesEntryId!.Value, entry.Amount)));
        Assert.Equal(0m, originals.Take(2).Sum(entry => entry.Amount) + reversal.Sum(entry => entry.Amount));
    }
}
