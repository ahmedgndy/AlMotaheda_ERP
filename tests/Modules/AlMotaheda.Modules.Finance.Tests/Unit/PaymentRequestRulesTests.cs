using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Finance.Requests;

namespace AlMotaheda.Modules.Finance.Tests.Unit;

/// <summary>The payment request flow, with no database: which step is allowed from which status.</summary>
public sealed class PaymentRequestRulesTests
{
    private const int Creator = 10;
    private const int Executive = 20;
    private static readonly DateTime Now = new(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc);

    private static PaymentRequest New(string method = PaymentMethods.BankTransfer) =>
        PaymentRequest.Submit(
            PaymentSourceTypes.SupplierPayment, PayeeTypes.Supplier, 5, 3, PaymentPurposes.SupplierInvoices, 1_000m, method,
            new DateOnly(2026, 10, 15), "INV-7", null, null, Guid.NewGuid(), Creator, Now,
            [new PaymentRequestLine { RecordType = PaidRecordTypes.ProjectExpense, RecordId = 40, ProjectId = 3, Amount = 1_000m }]);

    [Fact]
    public void A_new_request_is_submitted_with_its_first_history_step()
    {
        var request = New();

        Assert.Equal(PaymentRequestStatus.Submitted, request.Status);
        var step = Assert.Single(request.Events);
        Assert.Equal(("Submit", (PaymentRequestStatus?)null, PaymentRequestStatus.Submitted, Creator), (step.Action, step.FromStatus, step.ToStatus, step.ActorUserId));
    }

    [Fact]
    public void Nobody_decides_their_own_request()
    {
        var request = New();

        Assert.Equal(RequestDecision.OwnRequest, request.Approve(Creator, Now, null));
        Assert.Equal(RequestDecision.OwnRequest, request.Reject(Creator, Now, "no"));
        Assert.Equal(RequestDecision.OwnRequest, request.ApproveAndPay(Creator, Now, null, "BANK-1"));
        Assert.Equal(PaymentRequestStatus.Submitted, request.Status);
    }

    [Fact]
    public void A_bank_payment_goes_submitted_approved_sent_paid_and_records_each_step()
    {
        var request = New();

        Assert.Equal(RequestDecision.Allowed, request.Approve(Executive, Now, "OK"));
        Assert.Equal(RequestDecision.WrongMethod, PaymentRequestRules.Check(RequestAction.ConfirmPayment, request.Status, request.Method, Creator, Creator));
        Assert.Equal(RequestDecision.Allowed, request.SendToBank(Creator, Now, "MOCK-1"));
        Assert.Equal(RequestDecision.Allowed, request.MarkPaid(Creator, Now, new DateOnly(2026, 10, 16), "SWIFT-9", 77));

        Assert.Equal(PaymentRequestStatus.Paid, request.Status);
        Assert.Equal(77, request.SourcePaymentId);
        Assert.Equal(["Submit", "Approve", "SendToBank", "ConfirmPayment"], request.Events.Select(step => step.Action));
        Assert.Equal(Executive, request.DecidedBy);
    }

    [Fact]
    public void Cash_is_confirmed_after_approval_and_never_sent_to_the_bank()
    {
        var request = New(PaymentMethods.Cash);

        Assert.Equal(RequestDecision.Allowed, request.ApproveAndPay(Executive, Now, null, bankReference: null));
        Assert.Equal(PaymentRequestStatus.Approved, request.Status);
        Assert.Equal(RequestDecision.WrongMethod, request.SendToBank(Creator, Now, "MOCK-1"));
        Assert.Equal(RequestDecision.Allowed, request.MarkPaid(Executive, Now, new DateOnly(2026, 10, 16), "RCPT-3", 9));
    }

    [Fact]
    public void A_failed_bank_payment_can_be_sent_again_or_cancelled_but_paid_and_final_requests_cannot_move()
    {
        var failed = New();
        failed.Approve(Executive, Now, null);
        failed.SendToBank(Creator, Now, "MOCK-1");
        Assert.Equal(RequestDecision.Allowed, failed.RecordFailure(Creator, Now, "Account closed"));
        Assert.Equal(RequestDecision.Allowed, failed.SendToBank(Creator, Now, "MOCK-2"));
        Assert.Null(failed.FailureReason);

        var rejected = New();
        Assert.Equal(RequestDecision.Allowed, rejected.Reject(Executive, Now, "Wrong supplier"));
        Assert.Equal(RequestDecision.WrongStatus, rejected.Approve(Executive, Now, null));
        Assert.Equal(RequestDecision.WrongStatus, rejected.Cancel(Creator, Now, "late"));

        var paid = New(PaymentMethods.Cash);
        paid.ApproveAndPay(Executive, Now, null, null);
        paid.MarkPaid(Executive, Now, new DateOnly(2026, 10, 16), null, 1);
        Assert.Equal(RequestDecision.WrongStatus, paid.Cancel(Creator, Now, "late"));
        Assert.Equal(RequestDecision.Allowed, paid.Reverse(Executive, Now, "Paid twice"));
        Assert.Equal(PaymentRequestStatus.Reversed, paid.Status);
        Assert.Equal(RequestDecision.WrongStatus, paid.Reverse(Executive, Now, "again"));
    }

    [Theory]
    [InlineData("Submitted", true)]
    [InlineData("Approved", true)]
    [InlineData("SentToBank", true)]
    [InlineData("Failed", true)]
    [InlineData("Paid", false)]
    [InlineData("Rejected", false)]
    [InlineData("Cancelled", false)]
    [InlineData("Reversed", false)]
    public void Only_open_requests_hold_money(string status, bool holds) =>
        Assert.Equal(holds, PaymentRequestRules.Open.Contains(Enum.Parse<PaymentRequestStatus>(status)));
}
