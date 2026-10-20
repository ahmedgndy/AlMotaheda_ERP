using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Expenses;

/// <summary>One payment that settles part of an expense, as read with the expense.</summary>
internal sealed record ExpensePaymentRef(int PaymentId, DateOnly Date, PaymentMethod Method, string? Reference, decimal Amount);

/// <summary>
/// One expense with the names next to it and its payments, as the queries project it. Views are built from this
/// in memory with <see cref="ExpenseSettlement"/>.
/// </summary>
internal sealed class ExpenseRow
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public ExpenseKind Kind { get; init; }
    public DateOnly ExpenseDate { get; init; }
    public int? SupplierId { get; init; }
    public string? SupplierNameAr { get; init; }
    public string? SupplierNameEn { get; init; }
    public SupplierKind? SupplierKind { get; init; }
    public string? ItemName { get; init; }
    public string? Unit { get; init; }
    public decimal? Quantity { get; init; }
    public decimal? UnitPrice { get; init; }
    public ExpenseCategory? Category { get; init; }
    public string? Description { get; init; }
    public decimal Amount { get; init; }
    public PaymentChannel Channel { get; init; }
    public string? InvoiceNo { get; init; }
    public string? DeliveryNoteNo { get; init; }
    public string? SourceType { get; init; }
    public int? SourceId { get; init; }
    public int? CatalogItemId { get; init; }
    public string? CatalogItemNameAr { get; init; }
    public string? CatalogItemNameEn { get; init; }
    public decimal? CatalogPriceAtRecording { get; init; }
    public int? SupplyAgreementId { get; init; }
    public string? PayeeName { get; init; }
    public int? PriceItemId { get; init; }
    public string? PriceItemNameAr { get; init; }
    public string? PriceItemNameEn { get; init; }
    public int? PriceRangeId { get; init; }
    public decimal? ReferenceMinPrice { get; init; }
    public decimal? ReferenceMaxPrice { get; init; }
    public PriceCheckResult? PriceCheck { get; init; }
    public string? PriceJustification { get; init; }

    /// <summary>The record's newest price case (any status), when it has one.</summary>
    public int? ExceptionId { get; init; }
    public string? ExceptionStatus { get; init; }
    public int? ProjectItemId { get; init; }
    public string? ProjectItemNameAr { get; init; }
    public string? ProjectItemNameEn { get; init; }
    public DateTime? ItemLinkedAt { get; init; }
    public int? ItemLinkedBy { get; init; }
    public ExpenseStatus Status { get; init; }
    public DateTime? ReviewedAt { get; init; }
    public int? ReviewedBy { get; init; }
    public string? ReviewNote { get; init; }
    public string? Notes { get; init; }
    public DateTime CreatedAt { get; init; }
    public int CreatedBy { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public int? UpdatedBy { get; init; }
    public byte[] RowVersion { get; init; } = [];

    /// <summary>Allocations of payments that are not voided, oldest first.</summary>
    public List<ExpensePaymentRef> Payments { get; init; } = [];

    public decimal Allocated => Payments.Sum(payment => payment.Amount);

    /// <summary>The recorded amount plus its approved adjustments: what every figure uses.</summary>
    public decimal AdjustedAmount { get; init; }

    public List<ExpenseAdjustmentRef> Adjustments { get; init; } = [];

    public ExpenseSettlementRow Settlement => new(Id, Kind, Status, Channel, Category, SupplierId, ProjectItemId, AdjustedAmount, Allocated);
}

/// <summary>One correction of a record, as loaded with it.</summary>
internal sealed record ExpenseAdjustmentRef(
    int Id, decimal Amount, string Reason, AdjustmentStatus Status, DateTime RequestedAt, int RequestedBy,
    DateTime? DecidedAt, int? DecidedBy, string? DecisionNote, byte[] RowVersion);

/// <summary>The filters of <c>GET /api/projects/{pid}/expenses</c>, after validation.</summary>
internal sealed record ExpenseFilter(
    ExpenseKind? Kind,
    ExpenseStatus? Status,
    PaymentChannel? Channel,
    int? SupplierId,
    int? ItemId,
    bool Unlinked,
    string? PaymentState,
    DateOnly? From,
    DateOnly? To,
    string? Search,
    string Sort,
    int? RecordedBy);

/// <summary>The expense queries, as <see cref="IQueryable{T}"/> so tests can prove they translate to SQL.</summary>
internal static class ExpenseQueries
{
    /// <summary>
    /// Every record of a project, flat: what every calculation needs and nothing else. One query per project,
    /// then the pure classes do the arithmetic (decision G5).
    /// </summary>
    public static IQueryable<ExpenseSettlementRow> SettlementRows(ProjectsDbContext db, int projectId) =>
        Settle(db, db.ProjectExpenses.AsNoTracking().Where(expense => expense.ProjectId == projectId));

    /// <summary>The same rows for a few records of the project, for a payment's targets.</summary>
    public static IQueryable<ExpenseSettlementRow> SettlementRows(ProjectsDbContext db, int projectId, IReadOnlyCollection<int> expenseIds) =>
        Settle(db, db.ProjectExpenses.AsNoTracking().Where(expense => expense.ProjectId == projectId && expenseIds.Contains(expense.Id)));

    private static IQueryable<ExpenseSettlementRow> Settle(ProjectsDbContext db, IQueryable<ProjectExpense> query) =>
        query.Select(expense => new ExpenseSettlementRow(
            expense.Id,
            expense.Kind,
            expense.Status,
            expense.Channel,
            expense.Category,
            expense.SupplierId,
            expense.ProjectItemId,
            // The adjusted amount: the recorded amount plus its approved corrections (M6).
            expense.Amount + (expense.Adjustments.Where(adjustment => adjustment.Status == AdjustmentStatus.Approved)
                .Sum(adjustment => (decimal?)adjustment.Amount) ?? 0),
            db.SupplierPaymentAllocations
                .Where(allocation => allocation.ExpenseId == expense.Id && allocation.Payment.VoidedAt == null)
                .Sum(allocation => allocation.Amount)));

    /// <summary>The records of a project with their names and payments, unordered. Callers filter and sort.</summary>
    public static IQueryable<ExpenseRow> Rows(ProjectsDbContext db, int projectId) =>
        Project(db, db.ProjectExpenses.AsNoTracking().Where(expense => expense.ProjectId == projectId));

    /// <summary>One record of a project, or none when the id belongs to another project.</summary>
    public static IQueryable<ExpenseRow> One(ProjectsDbContext db, int projectId, int expenseId) =>
        Project(db, db.ProjectExpenses.AsNoTracking().Where(expense => expense.ProjectId == projectId && expense.Id == expenseId));

    /// <summary>
    /// The records on the project items screens: a supply, from a supplier, or linked to an item. Approved ones
    /// (the lines), or the ones still waiting for review (recorded or returned). <paramref name="itemId"/> picks
    /// one item; <c>null</c> picks the "not linked yet" group. Newest first.
    /// </summary>
    public static IQueryable<ExpenseRow> ItemLines(ProjectsDbContext db, int projectId, int? itemId, bool approved)
    {
        var query = db.ProjectExpenses.AsNoTracking()
            .Where(expense => expense.ProjectId == projectId && expense.ProjectItemId == itemId)
            .Where(expense => expense.Kind == ExpenseKind.Supply || expense.SupplierId != null || expense.ProjectItemId != null);

        query = approved
            ? query.Where(expense => expense.Status == ExpenseStatus.Approved)
            : query.Where(expense => expense.Status == ExpenseStatus.Recorded || expense.Status == ExpenseStatus.Returned);

        return Project(db, query.OrderByDescending(expense => expense.ExpenseDate).ThenByDescending(expense => expense.Id));
    }

    /// <summary>
    /// The records linked to any of <paramref name="itemIds"/> (a main item and its sub-items), approved or still
    /// waiting for review (recorded or returned), newest first. One query for the whole breakdown.
    /// </summary>
    public static IQueryable<ExpenseRow> LinesOfItems(ProjectsDbContext db, int projectId, int[] itemIds, bool approved)
    {
        var query = db.ProjectExpenses.AsNoTracking()
            .Where(expense => expense.ProjectId == projectId && expense.ProjectItemId != null && itemIds.Contains(expense.ProjectItemId.Value));

        query = approved
            ? query.Where(expense => expense.Status == ExpenseStatus.Approved)
            : query.Where(expense => expense.Status == ExpenseStatus.Recorded || expense.Status == ExpenseStatus.Returned);

        return Project(db, query.OrderByDescending(expense => expense.ExpenseDate).ThenByDescending(expense => expense.Id));
    }

    /// <summary>The list screen: every filter runs in SQL, including the payment state.</summary>
    public static IQueryable<ExpenseRow> Filtered(ProjectsDbContext db, int projectId, ExpenseFilter filter)
    {
        var query = db.ProjectExpenses.AsNoTracking().Where(expense => expense.ProjectId == projectId);

        if (filter.RecordedBy is { } recorder)
        {
            query = query.Where(expense => expense.CreatedBy == recorder);
        }

        if (filter.Kind is { } kind)
        {
            query = query.Where(expense => expense.Kind == kind);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(expense => expense.Status == status);
        }

        if (filter.Channel is { } channel)
        {
            query = query.Where(expense => expense.Channel == channel);
        }

        if (filter.SupplierId is { } supplierId)
        {
            query = query.Where(expense => expense.SupplierId == supplierId);
        }

        if (filter.ItemId is { } itemId)
        {
            query = query.Where(expense => expense.ProjectItemId == itemId);
        }

        if (filter.Unlinked)
        {
            query = query.Where(expense => expense.ProjectItemId == null);
        }

        if (filter.From is { } from)
        {
            query = query.Where(expense => expense.ExpenseDate >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(expense => expense.ExpenseDate <= to);
        }

        if (filter.Search is { } text)
        {
            query = query.Where(expense =>
                (expense.ItemName != null && expense.ItemName.Contains(text))
                || (expense.Description != null && expense.Description.Contains(text))
                || (expense.InvoiceNo != null && expense.InvoiceNo.Contains(text))
                || (expense.DeliveryNoteNo != null && expense.DeliveryNoteNo.Contains(text))
                || (expense.Supplier != null && (expense.Supplier.NameEn.Contains(text) || expense.Supplier.NameAr.Contains(text))));
        }

        // The payment state only exists on approved records. Custody is paid at approval; credit by allocations.
        // The subquery is written out in each branch: EF translates expressions, not calls to helper methods.
        switch (filter.PaymentState)
        {
            case PaymentStates.Paid:
                query = query.Where(expense => expense.Status == ExpenseStatus.Approved
                    && (expense.Channel == PaymentChannel.Custody
                        || db.SupplierPaymentAllocations
                            .Where(allocation => allocation.ExpenseId == expense.Id && allocation.Payment.VoidedAt == null)
                            .Sum(allocation => allocation.Amount) >= expense.Amount));
                break;
            case PaymentStates.PartiallyPaid:
                query = query.Where(expense => expense.Status == ExpenseStatus.Approved && expense.Channel == PaymentChannel.Credit
                    && db.SupplierPaymentAllocations
                        .Where(allocation => allocation.ExpenseId == expense.Id && allocation.Payment.VoidedAt == null)
                        .Sum(allocation => allocation.Amount) > 0
                    && db.SupplierPaymentAllocations
                        .Where(allocation => allocation.ExpenseId == expense.Id && allocation.Payment.VoidedAt == null)
                        .Sum(allocation => allocation.Amount) < expense.Amount);
                break;
            case PaymentStates.Unpaid:
                query = query.Where(expense => expense.Status == ExpenseStatus.Approved && expense.Channel == PaymentChannel.Credit
                    && !db.SupplierPaymentAllocations
                        .Any(allocation => allocation.ExpenseId == expense.Id && allocation.Payment.VoidedAt == null));
                break;
        }

        query = filter.Sort switch
        {
            "amount" => query.OrderByDescending(expense => expense.Amount).ThenByDescending(expense => expense.Id),
            "status" => query.OrderBy(expense => expense.Status).ThenByDescending(expense => expense.ExpenseDate).ThenByDescending(expense => expense.Id),
            _ => query.OrderByDescending(expense => expense.ExpenseDate).ThenByDescending(expense => expense.Id),
        };

        return Project(db, query);
    }

    private static IQueryable<ExpenseRow> Project(ProjectsDbContext db, IQueryable<ProjectExpense> query) =>
        query.Select(expense => new ExpenseRow
        {
            Id = expense.Id,
            ProjectId = expense.ProjectId,
            Kind = expense.Kind,
            ExpenseDate = expense.ExpenseDate,
            SupplierId = expense.SupplierId,
            SupplierNameAr = expense.Supplier != null ? expense.Supplier.NameAr : null,
            SupplierNameEn = expense.Supplier != null ? expense.Supplier.NameEn : null,
            SupplierKind = expense.Supplier != null ? expense.Supplier.Kind : null,
            ItemName = expense.ItemName,
            Unit = expense.Unit,
            Quantity = expense.Quantity,
            UnitPrice = expense.UnitPrice,
            Category = expense.Category,
            Description = expense.Description,
            Amount = expense.Amount,
            AdjustedAmount = expense.Amount + (expense.Adjustments.Where(adjustment => adjustment.Status == AdjustmentStatus.Approved)
                .Sum(adjustment => (decimal?)adjustment.Amount) ?? 0),
            Adjustments = expense.Adjustments
                .OrderBy(adjustment => adjustment.Id)
                .Select(adjustment => new ExpenseAdjustmentRef(
                    adjustment.Id, adjustment.Amount, adjustment.Reason, adjustment.Status, adjustment.RequestedAt, adjustment.RequestedBy,
                    adjustment.DecidedAt, adjustment.DecidedBy, adjustment.DecisionNote, adjustment.RowVersion))
                .ToList(),
            Channel = expense.Channel,
            InvoiceNo = expense.InvoiceNo,
            DeliveryNoteNo = expense.DeliveryNoteNo,
            SourceType = expense.SourceType,
            SourceId = expense.SourceId,
            CatalogItemId = expense.CatalogItemId,
            CatalogItemNameAr = expense.CatalogItem != null ? expense.CatalogItem.NameAr : null,
            CatalogItemNameEn = expense.CatalogItem != null ? expense.CatalogItem.NameEn : null,
            CatalogPriceAtRecording = expense.CatalogPriceAtRecording,
            SupplyAgreementId = expense.SupplyAgreementId,
            PayeeName = expense.PayeeName,
            PriceItemId = expense.PriceItemId,
            PriceItemNameAr = expense.PriceItem != null ? expense.PriceItem.NameAr : null,
            PriceItemNameEn = expense.PriceItem != null ? expense.PriceItem.NameEn : null,
            PriceRangeId = expense.PriceRangeId,
            ReferenceMinPrice = expense.ReferenceMinPrice,
            ReferenceMaxPrice = expense.ReferenceMaxPrice,
            PriceCheck = expense.PriceCheck,
            PriceJustification = expense.PriceJustification,
            // The record's newest live case; a withdrawn one (the record was changed) is history, not shown.
            ExceptionId = db.PriceExceptions
                .Where(exception => exception.SourceType == PriceExceptionSources.ProjectExpense && exception.SourceId == expense.Id
                                    && exception.Status != PriceExceptionStatus.Withdrawn)
                .OrderByDescending(exception => exception.Id)
                .Select(exception => (int?)exception.Id)
                .FirstOrDefault(),
            ExceptionStatus = db.PriceExceptions
                .Where(exception => exception.SourceType == PriceExceptionSources.ProjectExpense && exception.SourceId == expense.Id
                                    && exception.Status != PriceExceptionStatus.Withdrawn)
                .OrderByDescending(exception => exception.Id)
                .Select(exception => exception.Status.ToString())
                .FirstOrDefault(),
            ProjectItemId = expense.ProjectItemId,
            ProjectItemNameAr = expense.ProjectItem != null ? expense.ProjectItem.NameAr : null,
            ProjectItemNameEn = expense.ProjectItem != null ? expense.ProjectItem.NameEn : null,
            ItemLinkedAt = expense.ItemLinkedAt,
            ItemLinkedBy = expense.ItemLinkedBy,
            Status = expense.Status,
            ReviewedAt = expense.ReviewedAt,
            ReviewedBy = expense.ReviewedBy,
            ReviewNote = expense.ReviewNote,
            Notes = expense.Notes,
            CreatedAt = expense.CreatedAt,
            CreatedBy = expense.CreatedBy,
            UpdatedAt = expense.UpdatedAt,
            UpdatedBy = expense.UpdatedBy,
            RowVersion = expense.RowVersion,
            Payments = db.SupplierPaymentAllocations
                .Where(allocation => allocation.ExpenseId == expense.Id && allocation.Payment.VoidedAt == null)
                .OrderBy(allocation => allocation.Payment.PaymentDate)
                .ThenBy(allocation => allocation.PaymentId)
                .Select(allocation => new ExpensePaymentRef(
                    allocation.PaymentId,
                    allocation.Payment.PaymentDate,
                    allocation.Payment.Method,
                    allocation.Payment.Reference,
                    allocation.Amount))
                .ToList(),
        });
}
