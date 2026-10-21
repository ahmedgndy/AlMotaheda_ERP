using AlMotaheda.Modules.Finance.Requests;

namespace AlMotaheda.Modules.Finance.Data;

/// <summary>
/// A request to pay one payee, for one purpose. Every payment goes through one, from creation to the Executive's
/// decision, the bank and "Paid", and only then does the source module write its payment row
/// (docs/modernization/07-finance-design.md).
/// </summary>
/// <remarks>
/// <para>The amount, payee, project, purpose and lines are fixed when the request is created and never change; to
/// change them, the request is cancelled and a new one made. So an approval always covers exactly what is paid.</para>
/// <para>The status changes only through the methods below, which ask <see cref="PaymentRequestRules"/> first and
/// record each change in <see cref="Events"/>.</para>
/// </remarks>
internal sealed class PaymentRequest
{
    public int Id { get; private set; }

    /// <summary><c>PAY-000123</c>, computed by the database from the id.</summary>
    public string Number { get; private set; } = "";

    /// <summary>One of <c>PaymentSourceTypes</c>: which module writes the payment once it is paid.</summary>
    public string SourceType { get; private init; } = "";

    /// <summary>One of <c>PayeeTypes</c>.</summary>
    public string PayeeType { get; private init; } = "";

    public int PayeeId { get; private init; }

    /// <summary>The project, or <c>null</c> when the money spans projects (the lines then say which).</summary>
    public int? ProjectId { get; private init; }

    /// <summary>One of <c>PaymentPurposes</c>.</summary>
    public string Purpose { get; private init; } = "";

    public decimal Amount { get; private init; }

    /// <summary>One of <c>PaymentMethods</c>.</summary>
    public string Method { get; private init; } = "";

    /// <summary>The date the requester asked to pay on.</summary>
    public DateOnly RequestedDate { get; private init; }

    public string? Reference { get; private init; }
    public string? Notes { get; private init; }

    /// <summary>Source-specific details the source needs to write its payment (JSON), for example a subcontract purpose.</summary>
    public string? Payload { get; private init; }

    /// <summary>Made by the client for each payment form. The same key twice returns the same request.</summary>
    public Guid IdempotencyKey { get; private init; }

    /// <summary>The custody account the cash comes from, when paid out of money already drawn from the treasury (method Cash only).</summary>
    public int? SourceCustodyAccountId { get; private init; }

    public PaymentRequestStatus Status { get; private set; } = PaymentRequestStatus.Submitted;

    /// <summary><c>Request</c> for requests made in Finance; <c>Imported</c> for payments recorded before it existed.</summary>
    public RequestOrigin Origin { get; private init; } = RequestOrigin.Request;

    public DateTime CreatedAt { get; private init; }
    public int CreatedBy { get; private init; }

    public DateTime? DecidedAt { get; private set; }
    public int? DecidedBy { get; private set; }
    public string? DecisionNote { get; private set; }

    public DateTime? SentToBankAt { get; private set; }
    public string? BankReference { get; private set; }

    public string? FailureReason { get; private set; }

    /// <summary>The date the money left, set when the payment is confirmed.</summary>
    public DateOnly? PaidOn { get; private set; }
    public DateTime? ConfirmedAt { get; private set; }
    public int? ConfirmedBy { get; private set; }

    /// <summary>The cash receipt or bank confirmation reference given when the payment was confirmed.</summary>
    public string? PaymentReference { get; private set; }

    /// <summary>The payment row the source module wrote when the request was paid.</summary>
    public int? SourcePaymentId { get; private set; }

    public DateTime? ClosedAt { get; private set; }
    public int? ClosedBy { get; private set; }

    /// <summary>Why it was cancelled or reversed.</summary>
    public string? CloseReason { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public List<PaymentRequestLine> Lines { get; } = [];

    public List<PaymentRequestEvent> Events { get; } = [];

    /// <summary>A new request, Submitted.</summary>
    public static PaymentRequest Submit(
        string sourceType, string payeeType, int payeeId, int? projectId, string purpose, decimal amount, string method,
        DateOnly requestedDate, string? reference, string? notes, string? payload, Guid idempotencyKey, int createdBy, DateTime now,
        IEnumerable<PaymentRequestLine> lines, int? sourceCustodyAccountId = null)
    {
        var request = new PaymentRequest
        {
            SourceCustodyAccountId = sourceCustodyAccountId,
            SourceType = sourceType,
            PayeeType = payeeType,
            PayeeId = payeeId,
            ProjectId = projectId,
            Purpose = purpose,
            Amount = amount,
            Method = method,
            RequestedDate = requestedDate,
            Reference = reference,
            Notes = notes,
            Payload = payload,
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
            CreatedBy = createdBy,
        };
        request.Lines.AddRange(lines);
        request.Record("Submit", null, createdBy, now, null);
        return request;
    }

    public RequestDecision Approve(int userId, DateTime now, string? note) =>
        Move(RequestAction.Approve, userId, now, note, PaymentRequestStatus.Approved, () => Decided(userId, now, note));

    public RequestDecision Reject(int userId, DateTime now, string note) =>
        Move(RequestAction.Reject, userId, now, note, PaymentRequestStatus.Rejected, () =>
        {
            Decided(userId, now, note);
            Closed(userId, now, note);
        });

    /// <summary>Approve and pay at once: cash is paid now (<see cref="MarkPaid"/> follows), bank orders go to the bank.</summary>
    public RequestDecision ApproveAndPay(int userId, DateTime now, string? note, string? bankReference)
    {
        var decision = PaymentRequestRules.Check(RequestAction.ApproveAndPay, Status, Method, CreatedBy, userId);
        if (decision != RequestDecision.Allowed)
        {
            return decision;
        }

        Decided(userId, now, note);
        var from = Status;
        Status = PaymentRequestStatus.Approved;
        Record("ApproveAndPay", from, userId, now, note);

        if (bankReference is not null)
        {
            SentToBank(userId, now, bankReference);
        }

        return RequestDecision.Allowed;
    }

    public RequestDecision SendToBank(int userId, DateTime now, string bankReference) =>
        Move(RequestAction.SendToBank, userId, now, null, PaymentRequestStatus.SentToBank, () =>
        {
            SentToBankAt = now;
            BankReference = bankReference;
            FailureReason = null;
        });

    public RequestDecision RecordFailure(int userId, DateTime now, string reason) =>
        Move(RequestAction.RecordFailure, userId, now, reason, PaymentRequestStatus.Failed, () => FailureReason = reason);

    /// <summary>The money left: the source module has written its payment <paramref name="sourcePaymentId"/>.</summary>
    public RequestDecision MarkPaid(int userId, DateTime now, DateOnly paidOn, string? paymentReference, int sourcePaymentId) =>
        Move(RequestAction.ConfirmPayment, userId, now, paymentReference, PaymentRequestStatus.Paid, () =>
        {
            PaidOn = paidOn;
            ConfirmedAt = now;
            ConfirmedBy = userId;
            PaymentReference = paymentReference;
            SourcePaymentId = sourcePaymentId;
        });

    public RequestDecision Cancel(int userId, DateTime now, string reason) =>
        Move(RequestAction.Cancel, userId, now, reason, PaymentRequestStatus.Cancelled, () => Closed(userId, now, reason));

    public RequestDecision Reverse(int userId, DateTime now, string reason) =>
        Move(RequestAction.Reverse, userId, now, reason, PaymentRequestStatus.Reversed, () => Closed(userId, now, reason));

    private RequestDecision Move(RequestAction action, int userId, DateTime now, string? note, PaymentRequestStatus target, Action apply)
    {
        var decision = PaymentRequestRules.Check(action, Status, Method, CreatedBy, userId);
        if (decision != RequestDecision.Allowed)
        {
            return decision;
        }

        var from = Status;
        apply();
        Status = target;
        Record(action.ToString(), from, userId, now, note);
        return RequestDecision.Allowed;
    }

    private void SentToBank(int userId, DateTime now, string bankReference)
    {
        var from = Status;
        SentToBankAt = now;
        BankReference = bankReference;
        Status = PaymentRequestStatus.SentToBank;
        Record(nameof(RequestAction.SendToBank), from, userId, now, null);
    }

    private void Decided(int userId, DateTime now, string? note)
    {
        DecidedAt = now;
        DecidedBy = userId;
        DecisionNote = note;
    }

    private void Closed(int userId, DateTime now, string reason)
    {
        ClosedAt = now;
        ClosedBy = userId;
        CloseReason = reason;
    }

    private void Record(string action, PaymentRequestStatus? from, int userId, DateTime now, string? note) =>
        Events.Add(new PaymentRequestEvent
        {
            Action = action,
            FromStatus = from,
            ToStatus = Status,
            ActorUserId = userId,
            At = now,
            Note = note,
        });
}

/// <summary>Stored by name. See <c>PaymentRequestStatuses</c> for the names the client sees.</summary>
internal enum PaymentRequestStatus
{
    Submitted,
    Approved,
    SentToBank,
    Paid,
    Rejected,
    Failed,
    Cancelled,
    Reversed,
}

/// <summary>Stored by name.</summary>
internal enum RequestOrigin
{
    Request,
    Imported,
}

/// <summary>What part of the money pays for one record (an invoice, a worker's day, a contract, an equipment cost).</summary>
internal sealed class PaymentRequestLine
{
    public int Id { get; set; }

    public int RequestId { get; set; }

    /// <summary>One of <c>PaidRecordTypes</c>.</summary>
    public string RecordType { get; set; } = "";

    public int RecordId { get; set; }

    public int ProjectId { get; set; }

    public decimal Amount { get; set; }
}

/// <summary>One step of a request's history: who moved it, when, from which status to which, and why.</summary>
internal sealed class PaymentRequestEvent
{
    public long Id { get; set; }

    public int RequestId { get; set; }

    /// <summary>The action, for example <c>Approve</c>.</summary>
    public string Action { get; set; } = "";

    public PaymentRequestStatus? FromStatus { get; set; }
    public PaymentRequestStatus ToStatus { get; set; }

    public int ActorUserId { get; set; }

    public DateTime At { get; set; }

    public string? Note { get; set; }
}
