using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;

namespace AlMotaheda.Modules.Finance.Requests;

/// <summary>What can be done to a payment request.</summary>
internal enum RequestAction
{
    Approve,
    Reject,
    ApproveAndPay,
    SendToBank,
    ConfirmPayment,
    RecordFailure,
    Cancel,
    Reverse,
}

/// <summary>The answer to "may this be done now?", with the reason when not.</summary>
internal enum RequestDecision
{
    Allowed,

    /// <summary>The request is not in a status that allows this (409 <c>invalid_status</c>).</summary>
    WrongStatus,

    /// <summary>The action doesn't fit the payment method, for example sending cash to the bank (409 <c>wrong_method</c>).</summary>
    WrongMethod,

    /// <summary>The caller created the request, so someone else must decide it (409 <c>own_record</c>).</summary>
    OwnRequest,
}

/// <summary>
/// The payment request flow (docs/modernization/07-finance-design.md, section 4), with no database access. The
/// entity's methods ask here first, so a status can only move along these lines.
/// </summary>
internal static class PaymentRequestRules
{
    /// <summary>Open requests hold (reserve) the money they plan to pay.</summary>
    public static readonly IReadOnlyList<PaymentRequestStatus> Open =
        [PaymentRequestStatus.Submitted, PaymentRequestStatus.Approved, PaymentRequestStatus.SentToBank, PaymentRequestStatus.Failed];

    public static RequestDecision Check(RequestAction action, PaymentRequestStatus status, string method, int createdBy, int callerUserId)
    {
        var bank = PaymentMethods.GoesThroughBank(method);

        switch (action)
        {
            case RequestAction.Approve or RequestAction.Reject or RequestAction.ApproveAndPay:
                if (createdBy == callerUserId)
                {
                    return RequestDecision.OwnRequest;
                }

                return status == PaymentRequestStatus.Submitted ? RequestDecision.Allowed : RequestDecision.WrongStatus;

            case RequestAction.SendToBank:
                if (status is not (PaymentRequestStatus.Approved or PaymentRequestStatus.Failed))
                {
                    return RequestDecision.WrongStatus;
                }

                return bank ? RequestDecision.Allowed : RequestDecision.WrongMethod;

            case RequestAction.ConfirmPayment:
                // Cash is confirmed after approval; bank transfers and cheques once the bank has them.
                return (status, bank) switch
                {
                    (PaymentRequestStatus.SentToBank, true) => RequestDecision.Allowed,
                    (PaymentRequestStatus.Approved, false) => RequestDecision.Allowed,
                    (PaymentRequestStatus.Approved, true) => RequestDecision.WrongMethod,
                    _ => RequestDecision.WrongStatus,
                };

            case RequestAction.RecordFailure:
                return status == PaymentRequestStatus.SentToBank ? RequestDecision.Allowed : RequestDecision.WrongStatus;

            case RequestAction.Cancel:
                return status is PaymentRequestStatus.Submitted or PaymentRequestStatus.Approved or PaymentRequestStatus.Failed
                    ? RequestDecision.Allowed
                    : RequestDecision.WrongStatus;

            case RequestAction.Reverse:
                return status == PaymentRequestStatus.Paid ? RequestDecision.Allowed : RequestDecision.WrongStatus;

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown action.");
        }
    }
}
