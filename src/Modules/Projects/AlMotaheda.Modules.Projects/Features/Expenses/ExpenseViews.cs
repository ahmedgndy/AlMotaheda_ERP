using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Prices;
using AlMotaheda.Modules.Projects.Features.Projects;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Expenses;

/// <summary>One payment allocation shown on an expense.</summary>
internal sealed record ExpensePaymentView(int PaymentId, DateOnly Date, string Method, string? Reference, decimal Amount)
{
    public static ExpensePaymentView From(ExpensePaymentRef payment) =>
        new(payment.PaymentId, payment.Date, payment.Method.ToString(), payment.Reference, payment.Amount);
}

/// <summary>A correction of the record: <see cref="Amount"/> is signed. Money fields are null without See money, like the record's.</summary>
internal sealed record ExpenseAdjustmentView(
    int Id, decimal Amount, string Reason, string Status, DateTime RequestedAt, PersonRef RequestedBy,
    DateTime? DecidedAt, PersonRef? DecidedBy, string? DecisionNote, string RowVersion)
{
    public static ExpenseAdjustmentView From(ExpenseAdjustmentRef adjustment, IReadOnlyDictionary<int, UserSummary> people) => new(
        adjustment.Id, adjustment.Amount, adjustment.Reason, adjustment.Status.ToString(), adjustment.RequestedAt,
        PersonRef.From(adjustment.RequestedBy, people), adjustment.DecidedAt,
        adjustment.DecidedBy is { } decider ? PersonRef.From(decider, people) : null, adjustment.DecisionNote,
        RowVersions.Format(adjustment.RowVersion));
}

/// <summary>
/// One supply or site expense, as the list, the detail and every write return it. Money figures come from
/// <see cref="ExpenseSettlement"/>: <see cref="Recognized"/> is zero until approved, <see cref="Paid"/> follows the
/// channel, <see cref="PaymentState"/> is empty while nothing is owed.
/// </summary>
/// <remarks>
/// A caller without projects SeeMoney sees only their own records and the amount they typed. How a record was
/// paid is not theirs to see (owner decision, QA P9-02): <see cref="Paid"/>, <see cref="Outstanding"/> and
/// <see cref="PaymentState"/> are <c>null</c> and <see cref="Payments"/> is empty for them.
/// <see cref="PriceCheck"/> is the comparison with the company price list made when the record was recorded; the
/// recorder typed the price, so it is shown to them too.
/// </remarks>
internal sealed record ExpenseView(
    int Id,
    int ProjectId,
    string Kind,
    DateOnly ExpenseDate,
    int? SupplierId,
    string? SupplierNameAr,
    string? SupplierNameEn,
    string? SupplierKind,
    string? ItemName,
    string? Unit,
    decimal? Quantity,
    decimal? UnitPrice,
    string? Category,
    string? Description,
    decimal Amount,
    decimal AdjustedAmount,
    string Channel,
    string? InvoiceNo,
    string? DeliveryNoteNo,
    string? SourceType,
    int? SourceId,
    int? CatalogItemId,
    string? CatalogItemNameAr,
    string? CatalogItemNameEn,
    decimal? CatalogPriceAtRecording,
    int? ProjectItemId,
    string? ProjectItemNameAr,
    string? ProjectItemNameEn,
    DateTime? ItemLinkedAt,
    int? ItemLinkedBy,
    string Status,
    DateTime? ReviewedAt,
    PersonRef? Reviewer,
    string? ReviewNote,
    string? Notes,
    decimal Recognized,
    decimal? Paid,
    decimal? Outstanding,
    string? PaymentState,
    IReadOnlyList<ExpensePaymentView> Payments,
    IReadOnlyList<ExpenseAdjustmentView> Adjustments,
    DateTime CreatedAt,
    PersonRef Recorder,
    DateTime? UpdatedAt,
    int? UpdatedBy,
    string RowVersion,
    PriceCheckView? PriceCheck)
{
    public static ExpenseView From(ExpenseRow row, IReadOnlyDictionary<int, UserSummary> people, bool seesMoney)
    {
        var settlement = row.Settlement;

        return new ExpenseView(
            row.Id,
            row.ProjectId,
            row.Kind.ToString(),
            row.ExpenseDate,
            row.SupplierId,
            row.SupplierNameAr,
            row.SupplierNameEn,
            row.SupplierKind?.ToString(),
            row.ItemName,
            row.Unit,
            row.Quantity,
            row.UnitPrice,
            row.Category?.ToString(),
            row.Description,
            row.Amount,
            row.AdjustedAmount,
            row.Channel.ToString(),
            row.InvoiceNo,
            row.DeliveryNoteNo,
            row.SourceType,
            row.SourceId,
            row.CatalogItemId,
            row.CatalogItemNameAr,
            row.CatalogItemNameEn,
            row.CatalogPriceAtRecording,
            row.ProjectItemId,
            row.ProjectItemNameAr,
            row.ProjectItemNameEn,
            row.ItemLinkedAt,
            row.ItemLinkedBy,
            row.Status.ToString(),
            row.ReviewedAt,
            row.ReviewedBy is { } reviewer ? PersonRef.From(reviewer, people) : null,
            row.ReviewNote,
            row.Notes,
            ExpenseSettlement.Recognized(settlement),
            seesMoney ? ExpenseSettlement.Paid(settlement) : null,
            seesMoney ? ExpenseSettlement.Outstanding(settlement) : null,
            seesMoney ? ExpenseSettlement.PaymentState(settlement) : null,
            seesMoney ? row.Payments.Select(ExpensePaymentView.From).ToList() : [],
            row.Adjustments.Select(adjustment => ExpenseAdjustmentView.From(adjustment, people)).ToList(),
            row.CreatedAt,
            PersonRef.From(row.CreatedBy, people),
            row.UpdatedAt,
            row.UpdatedBy,
            RowVersions.Format(row.RowVersion),
            PriceCheckView.From(row));
    }

    /// <summary>Builds the views of a page with one name lookup for every recorder and reviewer on it.</summary>
    public static async Task<IReadOnlyList<ExpenseView>> FromAsync(
        IReadOnlyList<ExpenseRow> rows, IUserDirectory users, bool seesMoney, CancellationToken cancellationToken)
    {
        var userIds = rows.Select(row => row.CreatedBy)
            .Concat(rows.Where(row => row.ReviewedBy is not null).Select(row => row.ReviewedBy!.Value))
            .Concat(rows.SelectMany(row => row.Adjustments).SelectMany(adjustment => new[] { adjustment.RequestedBy, adjustment.DecidedBy ?? adjustment.RequestedBy }))
            .Distinct()
            .ToList();
        var people = userIds.Count == 0
            ? new Dictionary<int, UserSummary>()
            : await users.FindAsync(userIds, cancellationToken);

        return rows.Select(row => From(row, people, seesMoney)).ToList();
    }

    /// <summary>Reads one record of a project back after a write, with its names and payments.</summary>
    public static async Task<ExpenseView?> LoadAsync(
        ProjectsDbContext db, IUserDirectory users, int projectId, int expenseId, bool seesMoney, CancellationToken cancellationToken)
    {
        var row = await ExpenseQueries.One(db, projectId, expenseId).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : (await FromAsync([row], users, seesMoney, cancellationToken))[0];
    }
}
