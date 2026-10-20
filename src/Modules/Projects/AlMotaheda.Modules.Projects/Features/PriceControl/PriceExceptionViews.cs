using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.PriceControl;

/// <summary>A price exception as the inbox, the reports and the record screens show it.</summary>
internal sealed record PriceExceptionView(
    int Id,
    string SourceType,
    int SourceId,
    int ProjectId,
    string? ProjectCode,
    string? ProjectNameAr,
    string? ProjectNameEn,
    int? SupplierId,
    string? SupplierNameAr,
    string? SupplierNameEn,
    int PriceItemId,
    string? PriceItemNameAr,
    string? PriceItemNameEn,
    string? CategoryCode,
    string Unit,
    decimal Quantity,
    decimal UnitPrice,
    decimal? ReferenceMinPrice,
    decimal? ReferenceMaxPrice,
    decimal? ReferencePrice,
    decimal VariancePerUnit,
    decimal VarianceTotal,
    decimal VariancePercent,
    string? Reason,
    string Summary,
    DateOnly TransactionDate,
    string Status,
    int SubmittedBy,
    string? SubmittedByNameAr,
    string? SubmittedByNameEn,
    DateTime SubmittedAt,
    int? DecidedBy,
    string? DecidedByNameAr,
    string? DecidedByNameEn,
    DateTime? DecidedAt,
    string? DecisionNote,
    int? NewRangeId,
    string RowVersion);

/// <summary>The price exception queries, as <see cref="IQueryable{T}"/> so tests can prove they translate.</summary>
internal static class PriceExceptionQueries
{
    internal sealed class Row
    {
        public required PriceException Exception { get; init; }
        public string? ProjectCode { get; init; }
        public string? ProjectNameAr { get; init; }
        public string? ProjectNameEn { get; init; }
        public string? SupplierNameAr { get; init; }
        public string? SupplierNameEn { get; init; }
        public string? PriceItemNameAr { get; init; }
        public string? PriceItemNameEn { get; init; }
        public string? CategoryCode { get; init; }
        public decimal? ReferencePrice { get; init; }

        public PriceExceptionView View(IReadOnlyDictionary<int, UserSummary> people)
        {
            var e = Exception;
            var submitter = people.GetValueOrDefault(e.SubmittedBy);
            var decider = e.DecidedBy is { } id ? people.GetValueOrDefault(id) : null;
            return new PriceExceptionView(
                e.Id, e.SourceType, e.SourceId, e.ProjectId, ProjectCode, ProjectNameAr, ProjectNameEn, e.SupplierId, SupplierNameAr, SupplierNameEn,
                e.PriceItemId, PriceItemNameAr, PriceItemNameEn, CategoryCode, e.Unit, e.Quantity, e.UnitPrice, e.ReferenceMinPrice, e.ReferenceMaxPrice,
                ReferencePrice, e.VariancePerUnit, e.VarianceTotal, e.VariancePercent, e.Reason, e.Summary, e.TransactionDate, e.Status.ToString(),
                e.SubmittedBy, submitter?.NameAr, submitter?.NameEn, e.SubmittedAt, e.DecidedBy, decider?.NameAr, decider?.NameEn, e.DecidedAt,
                e.DecisionNote, e.NewRangeId, RowVersions.Format(e.RowVersion));
        }
    }

    public static IQueryable<Row> Rows(ProjectsDbContext db, IQueryable<PriceException> exceptions) =>
        exceptions.Select(exception => new Row
        {
            Exception = exception,
            ProjectCode = exception.Project.Code,
            ProjectNameAr = exception.Project.NameAr,
            ProjectNameEn = exception.Project.NameEn,
            SupplierNameAr = db.Suppliers.Where(supplier => supplier.Id == exception.SupplierId).Select(supplier => supplier.NameAr).FirstOrDefault(),
            SupplierNameEn = db.Suppliers.Where(supplier => supplier.Id == exception.SupplierId).Select(supplier => supplier.NameEn).FirstOrDefault(),
            PriceItemNameAr = exception.PriceItem.NameAr,
            PriceItemNameEn = exception.PriceItem.NameEn,
            CategoryCode = exception.PriceItem.Category.Code,
            ReferencePrice = db.PriceRanges.Where(range => range.Id == exception.PriceRangeId).Select(range => range.ReferencePrice).FirstOrDefault(),
        });

    public static async Task<IReadOnlyList<PriceExceptionView>> ViewsAsync(
        ProjectsDbContext db, IUserDirectory users, IQueryable<PriceException> exceptions, CancellationToken cancellationToken)
    {
        var rows = await Rows(db, exceptions).ToListAsync(cancellationToken);
        var people = await users.FindAsync(
            rows.SelectMany(row => new[] { row.Exception.SubmittedBy, row.Exception.DecidedBy ?? row.Exception.SubmittedBy }).Distinct().ToList(), cancellationToken);
        return rows.Select(row => row.View(people)).ToList();
    }

    public static async Task<PriceExceptionView?> OneAsync(ProjectsDbContext db, IUserDirectory users, int exceptionId, CancellationToken cancellationToken) =>
        (await ViewsAsync(db, users, db.PriceExceptions.AsNoTracking().Where(exception => exception.Id == exceptionId), cancellationToken)).SingleOrDefault();
}
