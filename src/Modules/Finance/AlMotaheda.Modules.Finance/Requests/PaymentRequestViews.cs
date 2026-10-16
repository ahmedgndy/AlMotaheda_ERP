using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Finance.Requests;

/// <summary>A user as the request screens show them.</summary>
internal sealed record PersonView(int Id, string? NameAr, string? NameEn);

/// <summary>One row of the payment request list. <see cref="Amount"/> is <c>null</c> without payments See money.</summary>
internal sealed record PaymentRequestRow(
    int Id,
    string Number,
    string Status,
    string SourceType,
    string PayeeType,
    int PayeeId,
    string? PayeeNameAr,
    string? PayeeNameEn,
    int? ProjectId,
    string Purpose,
    decimal? Amount,
    string Method,
    DateOnly RequestedDate,
    DateTime CreatedAt,
    PersonView CreatedBy,
    DateOnly? PaidOn,
    string Origin);

internal sealed record PaymentLineView(string RecordType, int RecordId, int ProjectId, decimal? Amount);

internal sealed record PaymentEventView(string Action, string? FromStatus, string ToStatus, PersonView Actor, DateTime At, string? Note);

/// <summary>A request with its lines and history (the approval trail).</summary>
internal sealed record PaymentRequestDetails(
    PaymentRequestRow Request,
    string? Reference,
    string? Notes,
    DateTime? DecidedAt,
    PersonView? DecidedBy,
    string? DecisionNote,
    DateTime? SentToBankAt,
    string? BankReference,
    string? FailureReason,
    DateTime? ConfirmedAt,
    PersonView? ConfirmedBy,
    string? PaymentReference,
    int? SourcePaymentId,
    DateTime? ClosedAt,
    PersonView? ClosedBy,
    string? CloseReason,
    IReadOnlyList<PaymentLineView> Lines,
    IReadOnlyList<PaymentEventView> Events,
    string RowVersion);

/// <summary>Filters of the payment request list.</summary>
internal sealed record RequestFilter(
    PaymentRequestStatus? Status, string? PayeeType, int? PayeeId, int? ProjectId, string? SourceType, IReadOnlySet<int>? VisibleProjects);

/// <summary>Builds the request views, with payee and user names loaded once per page.</summary>
internal sealed class PaymentRequestViews(FinanceDbContext db, IEnumerable<IPaymentSource> sources, IUserDirectory users)
{
    /// <summary>The filtered requests, newest first. A separate method so a test can check its SQL translation.</summary>
    public static IQueryable<PaymentRequest> Query(FinanceDbContext db, RequestFilter filter)
    {
        var query = db.PaymentRequests.AsNoTracking();

        if (filter.VisibleProjects is { } visible)
        {
            // Requests that span projects (ProjectId null) are only for company-wide roles.
            query = query.Where(request => request.ProjectId != null && visible.Contains(request.ProjectId.Value));
        }

        if (filter.Status is { } status)
        {
            query = query.Where(request => request.Status == status);
        }

        if (filter.PayeeType is { } payeeType)
        {
            query = query.Where(request => request.PayeeType == payeeType);
        }

        if (filter.PayeeId is { } payeeId)
        {
            query = query.Where(request => request.PayeeId == payeeId);
        }

        if (filter.ProjectId is { } projectId)
        {
            query = query.Where(request => request.ProjectId == projectId);
        }

        if (filter.SourceType is { } sourceType)
        {
            query = query.Where(request => request.SourceType == sourceType);
        }

        return query.OrderByDescending(request => request.Id);
    }

    public async Task<IReadOnlyList<PaymentRequestRow>> RowsAsync(
        IReadOnlyList<PaymentRequest> requests, bool seesAmounts, CancellationToken cancellationToken)
    {
        var payees = await PayeeNamesAsync(requests, cancellationToken);
        var people = await users.FindAsync(requests.Select(request => request.CreatedBy).Distinct().ToList(), cancellationToken);
        return requests.Select(request => Row(request, payees, people, seesAmounts)).ToList();
    }

    public async Task<PaymentRequestDetails?> DetailsAsync(int requestId, bool seesAmounts, CancellationToken cancellationToken)
    {
        var request = await db.PaymentRequests.AsNoTracking()
            .Include(candidate => candidate.Lines)
            .Include(candidate => candidate.Events)
            .AsSplitQuery()
            .SingleOrDefaultAsync(candidate => candidate.Id == requestId, cancellationToken);
        if (request is null)
        {
            return null;
        }

        var payees = await PayeeNamesAsync([request], cancellationToken);
        var ids = new[] { request.CreatedBy, request.DecidedBy, request.ConfirmedBy, request.ClosedBy }
            .Where(id => id is not null)
            .Select(id => id!.Value)
            .Concat(request.Events.Select(entry => entry.ActorUserId))
            .Distinct()
            .ToList();
        var people = await users.FindAsync(ids, cancellationToken);

        return new PaymentRequestDetails(
            Row(request, payees, people, seesAmounts),
            request.Reference,
            request.Notes,
            request.DecidedAt,
            Person(request.DecidedBy, people),
            request.DecisionNote,
            request.SentToBankAt,
            request.BankReference,
            request.FailureReason,
            request.ConfirmedAt,
            Person(request.ConfirmedBy, people),
            request.PaymentReference,
            request.SourcePaymentId,
            request.ClosedAt,
            Person(request.ClosedBy, people),
            request.CloseReason,
            request.Lines.OrderBy(line => line.Id)
                .Select(line => new PaymentLineView(line.RecordType, line.RecordId, line.ProjectId, seesAmounts ? line.Amount : null))
                .ToList(),
            request.Events.OrderBy(entry => entry.Id)
                .Select(entry => new PaymentEventView(entry.Action, entry.FromStatus?.ToString(), entry.ToStatus.ToString(),
                    Person(entry.ActorUserId, people)!, entry.At, entry.Note))
                .ToList(),
            RowVersions.Format(request.RowVersion));
    }

    private async Task<Dictionary<(string SourceType, int PayeeId), PayeeName>> PayeeNamesAsync(
        IReadOnlyList<PaymentRequest> requests, CancellationToken cancellationToken)
    {
        var names = new Dictionary<(string, int), PayeeName>();
        foreach (var group in requests.GroupBy(request => request.SourceType))
        {
            if (sources.SingleOrDefault(source => source.SourceType == group.Key) is not { } source)
            {
                continue;
            }

            var found = await source.PayeeNamesAsync(group.Select(request => request.PayeeId).Distinct().ToList(), cancellationToken);
            foreach (var (payeeId, name) in found)
            {
                names[(group.Key, payeeId)] = name;
            }
        }

        return names;
    }

    private static PaymentRequestRow Row(
        PaymentRequest request,
        IReadOnlyDictionary<(string, int), PayeeName> payees,
        IReadOnlyDictionary<int, UserSummary> people,
        bool seesAmounts)
    {
        var payee = payees.GetValueOrDefault((request.SourceType, request.PayeeId));
        return new PaymentRequestRow(
            request.Id,
            request.Number,
            request.Status.ToString(),
            request.SourceType,
            request.PayeeType,
            request.PayeeId,
            payee?.NameAr,
            payee?.NameEn,
            request.ProjectId,
            request.Purpose,
            seesAmounts ? request.Amount : null,
            request.Method,
            request.RequestedDate,
            request.CreatedAt,
            Person(request.CreatedBy, people)!,
            request.PaidOn,
            request.Origin.ToString());
    }

    private static PersonView? Person(int? userId, IReadOnlyDictionary<int, UserSummary> people) =>
        userId is { } id
            ? people.TryGetValue(id, out var user) ? new PersonView(id, user.NameAr, user.NameEn) : new PersonView(id, null, null)
            : null;
}
