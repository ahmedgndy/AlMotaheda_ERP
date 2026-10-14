using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Projects;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Expenses;

/// <summary>One payment allocation shown on an expense.</summary>
internal sealed record ExpensePaymentView(int PaymentId, DateOnly Date, string Method, string? Reference, decimal Amount)
{
    public static ExpensePaymentView From(ExpensePaymentRef payment) =>
        new(payment.PaymentId, payment.Date, payment.Method.ToString(), payment.Reference, payment.Amount);
}

/// <summary>
/// One supply or site expense, as the list, the detail and every write return it. Money figures come from
/// <see cref="ExpenseSettlement"/>: <see cref="Recognized"/> is zero until approved, <see cref="Paid"/> follows the
/// channel, <see cref="PaymentState"/> is empty while nothing is owed.
/// </summary>
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
    string Channel,
    string? InvoiceNo,
    string? DeliveryNoteNo,
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
    decimal Paid,
    decimal Outstanding,
    string? PaymentState,
    IReadOnlyList<ExpensePaymentView> Payments,
    DateTime CreatedAt,
    PersonRef Recorder,
    DateTime? UpdatedAt,
    int? UpdatedBy,
    string RowVersion)
{
    public static ExpenseView From(ExpenseRow row, IReadOnlyDictionary<int, UserSummary> people)
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
            row.Channel.ToString(),
            row.InvoiceNo,
            row.DeliveryNoteNo,
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
            ExpenseSettlement.Paid(settlement),
            ExpenseSettlement.Outstanding(settlement),
            ExpenseSettlement.PaymentState(settlement),
            row.Payments.Select(ExpensePaymentView.From).ToList(),
            row.CreatedAt,
            PersonRef.From(row.CreatedBy, people),
            row.UpdatedAt,
            row.UpdatedBy,
            RowVersions.Format(row.RowVersion));
    }

    /// <summary>Builds the views of a page with one name lookup for every recorder and reviewer on it.</summary>
    public static async Task<IReadOnlyList<ExpenseView>> FromAsync(
        IReadOnlyList<ExpenseRow> rows, IUserDirectory users, CancellationToken cancellationToken)
    {
        var userIds = rows.Select(row => row.CreatedBy)
            .Concat(rows.Where(row => row.ReviewedBy is not null).Select(row => row.ReviewedBy!.Value))
            .Distinct()
            .ToList();
        var people = userIds.Count == 0
            ? new Dictionary<int, UserSummary>()
            : await users.FindAsync(userIds, cancellationToken);

        return rows.Select(row => From(row, people)).ToList();
    }

    /// <summary>Reads one record of a project back after a write, with its names and payments.</summary>
    public static async Task<ExpenseView?> LoadAsync(
        ProjectsDbContext db, IUserDirectory users, int projectId, int expenseId, CancellationToken cancellationToken)
    {
        var row = await ExpenseQueries.One(db, projectId, expenseId).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : (await FromAsync([row], users, cancellationToken))[0];
    }
}
