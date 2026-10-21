using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Finance.Features.Custody;
using AlMotaheda.Modules.Finance.Requests;

namespace AlMotaheda.Modules.Finance.Tests.Unit;

/// <summary>The custody arithmetic and the transfer's status flow, with no database.</summary>
public sealed class CustodyRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Posted_balance_counts_posted_movements_and_pending_outflows_are_exposure_apart()
    {
        var (posted, pending) = CustodyArithmetic.Sum(
        [
            (60_000m, CustodyMovementStatus.Posted),      // received from the treasury
            (-20_000m, CustodyMovementStatus.Posted),     // handed to an engineer
            (-500m, CustodyMovementStatus.Pending),       // a claim waiting for review
            (-300m, CustodyMovementStatus.Released),      // a rejected claim: counts nowhere
        ]);

        Assert.Equal((40_000m, 500m), (posted, pending));
        var figures = CustodyFigures.Of(posted, pending, reserved: 1_000m, inTransit: 250m);
        Assert.Equal(38_500m, figures.Available);
    }

    [Fact]
    public void A_transfer_moves_requested_approved_disbursed_received_and_never_backwards()
    {
        var transfer = new CustodyTransfer { Amount = 60_000m, Purpose = PaymentPurposes.CustodyAdvance, RequestedBy = 5, RequestedAt = Now };

        Assert.True(transfer.ReservesSource);
        transfer.Approve(1, Now, "Fund the payroll");
        Assert.True(transfer.ReservesSource);
        transfer.Disburse(7, Now, new DateOnly(2026, 10, 20), "CASH-1");
        Assert.True(transfer.InTransit);
        Assert.False(transfer.ReservesSource);
        transfer.Receive(5, Now, new DateOnly(2026, 10, 20), null);
        Assert.Equal(CustodyTransferStatus.Received, transfer.Status);
        Assert.False(transfer.CanMoveTo(CustodyTransferStatus.Approved));
        Assert.False(transfer.CanMoveTo(CustodyTransferStatus.Cancelled));
        Assert.True(transfer.CanMoveTo(CustodyTransferStatus.Reversed));
        Assert.Equal(["Approve", "Disburse", "Receive"], transfer.Events.Select(entry => entry.Action));

        Assert.Throws<InvalidOperationException>(() => transfer.Approve(1, Now, null));
    }

    [Fact]
    public void A_rejected_or_cancelled_transfer_is_closed_with_its_reason_and_reserves_nothing()
    {
        var rejected = new CustodyTransfer { Amount = 10m, RequestedBy = 5 };
        rejected.Reject(1, Now, "No cash this week");
        Assert.Equal((CustodyTransferStatus.Rejected, "No cash this week", false), (rejected.Status, rejected.CloseReason, rejected.ReservesSource));

        var cancelled = new CustodyTransfer { Amount = 10m, RequestedBy = 5 };
        cancelled.Approve(1, Now, null);
        cancelled.Cancel(5, Now, "Asked by mistake");
        Assert.Equal(CustodyTransferStatus.Cancelled, cancelled.Status);
        Assert.False(cancelled.CanMoveTo(CustodyTransferStatus.Disbursed));
    }

    [Fact]
    public void The_treasury_side_of_a_transfer_is_an_internal_movement_never_a_payment()
    {
        var transfer = new CustodyTransfer { Id = 9, Amount = 60_000m, Purpose = PaymentPurposes.CustodyAdvance, ProjectId = null };

        var entry = LedgerPosting.Transfer(transfer, LedgerAccount.Cash, moneyOut: true, counterpartyAccountId: 3, new DateOnly(2026, 10, 20), Now, 7);

        Assert.Equal((LedgerAccount.Cash, -60_000m, PayeeTypes.CustodyAccount, 3, PaymentPurposes.CustodyAdvance), (entry.Account, entry.Amount, entry.CounterpartyType, entry.CounterpartyId, entry.Purpose));
        Assert.Null(entry.PaymentRequestId);
        Assert.Equal(9, entry.CustodyTransferId);
        Assert.True(PaymentPurposes.IsInternalTransfer(entry.Purpose));
        Assert.False(PaymentPurposes.IsInternalTransfer(PaymentPurposes.SupplierInvoices));

        entry.Id = 44;
        var reversal = LedgerPosting.Reversal([entry], new DateOnly(2026, 10, 21), Now, 7);
        Assert.Equal((60_000m, 44L, 9), (reversal[0].Amount, reversal[0].ReversesEntryId!.Value, reversal[0].CustodyTransferId!.Value));
    }

    [Fact]
    public void A_payment_out_of_a_custody_is_tagged_custody_so_the_treasury_is_unchanged()
    {
        var request = PaymentRequest.Submit(
            PaymentSourceTypes.SupplierPayment, PayeeTypes.Supplier, 8, 3, PaymentPurposes.SupplierInvoices, 1_000m, PaymentMethods.Cash,
            new DateOnly(2026, 10, 15), null, null, null, Guid.NewGuid(), 1, Now, [], sourceCustodyAccountId: 12);
        request.ApproveAndPay(2, Now, null, null);
        request.MarkPaid(3, Now, new DateOnly(2026, 10, 16), null, 55);

        var entry = Assert.Single(LedgerPosting.Paid(request, Now, 3));

        Assert.Equal((LedgerAccount.Custody, 12, -1_000m), (entry.Account, entry.CustodyAccountId!.Value, entry.Amount));
    }

    [Fact]
    public void Custody_kinds_say_who_keeps_their_ledger()
    {
        Assert.True(CustodyAccountKinds.IsLedgeredByFinance(CustodyAccountKinds.Accountant));
        Assert.True(CustodyAccountKinds.IsLedgeredByFinance(CustodyAccountKinds.PayrollBatch));
        Assert.False(CustodyAccountKinds.IsLedgeredByFinance(CustodyAccountKinds.EngineerProject));
        Assert.False(CustodyAccountKinds.IsLedgeredByFinance(CustodyAccountKinds.Vehicle));
        Assert.Equal("Projects", CustodyAccountViews.LedgerOwnerOf(CustodyAccountKinds.EngineerProject));
        Assert.Equal("Fleet", CustodyAccountViews.LedgerOwnerOf(CustodyAccountKinds.Vehicle));
    }
}
