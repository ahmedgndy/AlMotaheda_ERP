using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.SupplierPayments;

/// <summary>The permission module of payments (decision G4): recording needs Create, voiding needs Approve.</summary>
internal static class PaymentPermissions
{
    public const string Module = "payments";
}

/// <summary>One allocation of a payment, with what it settles.</summary>
internal sealed record PaymentAllocationView(int ExpenseId, string Kind, DateOnly ExpenseDate, string? Description, string? InvoiceNo, decimal Amount);

/// <summary>One supplier payment, as the list and the writes return it.</summary>
internal sealed record SupplierPaymentView(
    int Id,
    int ProjectId,
    int SupplierId,
    string SupplierNameAr,
    string SupplierNameEn,
    DateOnly PaymentDate,
    decimal Amount,
    string Method,
    string? Reference,
    string? Notes,
    Guid IdempotencyKey,
    bool IsVoided,
    DateTime? VoidedAt,
    int? VoidedBy,
    string? VoidReason,
    IReadOnlyList<PaymentAllocationView> Allocations,
    DateTime CreatedAt,
    int CreatedBy,
    string RowVersion)
{
    public static SupplierPaymentView From(PaymentRow row) => new(
        row.Id,
        row.ProjectId,
        row.SupplierId,
        row.SupplierNameAr,
        row.SupplierNameEn,
        row.PaymentDate,
        row.Amount,
        row.Method.ToString(),
        row.Reference,
        row.Notes,
        row.IdempotencyKey,
        row.VoidedAt is not null,
        row.VoidedAt,
        row.VoidedBy,
        row.VoidReason,
        row.Allocations
            .Select(allocation => new PaymentAllocationView(
                allocation.ExpenseId,
                allocation.Kind.ToString(),
                allocation.ExpenseDate,
                allocation.Kind == ExpenseKind.Supply ? allocation.ItemName : allocation.Description,
                allocation.InvoiceNo,
                allocation.Amount))
            .ToList(),
        row.CreatedAt,
        row.CreatedBy,
        RowVersions.Format(row.RowVersion));
}

/// <summary>A payment as the queries project it; the view is built from it in memory.</summary>
internal sealed class PaymentRow
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public int SupplierId { get; init; }
    public string SupplierNameAr { get; init; } = "";
    public string SupplierNameEn { get; init; } = "";
    public DateOnly PaymentDate { get; init; }
    public decimal Amount { get; init; }
    public PaymentMethod Method { get; init; }
    public string? Reference { get; init; }
    public string? Notes { get; init; }
    public Guid IdempotencyKey { get; init; }
    public DateTime? VoidedAt { get; init; }
    public int? VoidedBy { get; init; }
    public string? VoidReason { get; init; }
    public DateTime CreatedAt { get; init; }
    public int CreatedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];
    public List<PaymentAllocationRow> Allocations { get; init; } = [];
}

internal sealed class PaymentAllocationRow
{
    public int ExpenseId { get; init; }
    public ExpenseKind Kind { get; init; }
    public DateOnly ExpenseDate { get; init; }
    public string? ItemName { get; init; }
    public string? Description { get; init; }
    public string? InvoiceNo { get; init; }
    public decimal Amount { get; init; }
}

/// <summary>The supplier payment queries, as <see cref="IQueryable{T}"/> so tests can prove they translate to SQL.</summary>
internal static class PaymentQueries
{
    /// <summary>The payments of a project, newest first. Voided ones only when asked for.</summary>
    public static IQueryable<PaymentRow> List(ProjectsDbContext db, int projectId, int? supplierId, bool includeVoided)
    {
        var query = db.SupplierPayments.AsNoTracking().Where(payment => payment.ProjectId == projectId);

        if (supplierId is { } wanted)
        {
            query = query.Where(payment => payment.SupplierId == wanted);
        }

        if (!includeVoided)
        {
            query = query.Where(payment => payment.VoidedAt == null);
        }

        return Project(query.OrderByDescending(payment => payment.PaymentDate).ThenByDescending(payment => payment.Id));
    }

    /// <summary>One payment of a project, or none when the id belongs to another project.</summary>
    public static IQueryable<PaymentRow> One(ProjectsDbContext db, int projectId, int paymentId) =>
        Project(db.SupplierPayments.AsNoTracking().Where(payment => payment.ProjectId == projectId && payment.Id == paymentId));

    /// <summary>The payment recorded with this client key, whatever the project, or none.</summary>
    public static IQueryable<SupplierPayment> ByKey(ProjectsDbContext db, Guid idempotencyKey) =>
        db.SupplierPayments.AsNoTracking().Where(payment => payment.IdempotencyKey == idempotencyKey);

    private static IQueryable<PaymentRow> Project(IQueryable<SupplierPayment> query) =>
        query.Select(payment => new PaymentRow
        {
            Id = payment.Id,
            ProjectId = payment.ProjectId,
            SupplierId = payment.SupplierId,
            SupplierNameAr = payment.Supplier.NameAr,
            SupplierNameEn = payment.Supplier.NameEn,
            PaymentDate = payment.PaymentDate,
            Amount = payment.Amount,
            Method = payment.Method,
            Reference = payment.Reference,
            Notes = payment.Notes,
            IdempotencyKey = payment.IdempotencyKey,
            VoidedAt = payment.VoidedAt,
            VoidedBy = payment.VoidedBy,
            VoidReason = payment.VoidReason,
            CreatedAt = payment.CreatedAt,
            CreatedBy = payment.CreatedBy,
            RowVersion = payment.RowVersion,
            Allocations = payment.Allocations
                .OrderBy(allocation => allocation.Id)
                .Select(allocation => new PaymentAllocationRow
                {
                    ExpenseId = allocation.ExpenseId,
                    Kind = allocation.Expense.Kind,
                    ExpenseDate = allocation.Expense.ExpenseDate,
                    ItemName = allocation.Expense.ItemName,
                    Description = allocation.Expense.Description,
                    InvoiceNo = allocation.Expense.InvoiceNo,
                    Amount = allocation.Amount,
                })
                .ToList(),
        });
}
