using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Procurement.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Catalog;
using AlMotaheda.Modules.Projects.Features.Expenses;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Integration;

/// <summary>The project's price catalog for Procurement (requisition estimates).</summary>
internal sealed class ProjectCatalogContract(ProjectsDbContext db) : IProjectCatalog
{
    public async Task<IReadOnlyDictionary<int, CatalogItemRef>> FindAsync(
        int projectId, IReadOnlyCollection<int> catalogItemIds, DateOnly date, CancellationToken cancellationToken)
    {
        if (catalogItemIds.Count == 0)
        {
            return new Dictionary<int, CatalogItemRef>();
        }

        var items = await db.CatalogItems.AsNoTracking()
            .Where(item => item.ProjectId == projectId && catalogItemIds.Contains(item.Id))
            .Select(item => new
            {
                item.Id,
                item.NameAr,
                item.NameEn,
                item.Unit,
                item.Status,
                // The rule of CatalogPrices.ApprovedOn, written out so it translates inside this projection.
                Price = db.ItemPrices
                    .Where(price => price.CatalogItemId == item.Id && price.Status == ItemPriceStatus.Approved && price.EffectiveFrom <= date)
                    .OrderByDescending(price => price.EffectiveFrom)
                    .ThenByDescending(price => price.Id)
                    .Select(price => (decimal?)price.Price)
                    .FirstOrDefault(),
                // No project price in force: the master catalog's price of the item it was selected from.
                MasterPrice = db.MasterItemPrices
                    .Where(price => price.MasterItemId == item.MasterItemId && price.Status == ItemPriceStatus.Approved && price.EffectiveFrom <= date)
                    .OrderByDescending(price => price.EffectiveFrom)
                    .ThenByDescending(price => price.Id)
                    .Select(price => (decimal?)price.Price)
                    .FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        return items.ToDictionary(
            item => item.Id,
            item => new CatalogItemRef(item.Id, projectId, item.NameAr, item.NameEn, item.Unit, item.Status == CatalogItemStatus.Active, item.Price ?? item.MasterPrice));
    }
}

/// <summary>
/// Turns a matched supplier invoice into supply records: approved, on credit, one per invoice line, in
/// Procurement's transaction and under this project's lock. From then on they are ordinary supplier cost: the
/// project's cost breakdown, the supplier balance and the supplier payment request all read them.
/// </summary>
internal sealed class InvoicePostingContract(
    DbContextOptions<ProjectsDbContext> options,
    ProjectsDbContext db,
    CompanyClock clock,
    ProjectsLog log) : IInvoicePosting
{
    public async Task<IReadOnlyDictionary<int, int>> PostAsync(PostedInvoice invoice, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        await using var joined = await transaction.JoinAsync(options, ProjectsDatabase.Configure, joinedOptions => new ProjectsDbContext(joinedOptions), cancellationToken);

        if (!await ProjectLock.TryAcquireAsync(joined, invoice.ProjectId, cancellationToken))
        {
            throw new InvoiceNotPostableException("The project is busy. Try again in a moment.");
        }

        if (!await joined.Suppliers.AnyAsync(supplier => supplier.Id == invoice.SupplierId, cancellationToken))
        {
            throw new InvoiceNotPostableException("The supplier was not found.");
        }

        var catalogIds = invoice.Lines.Where(line => line.CatalogItemId is not null).Select(line => line.CatalogItemId!.Value).Distinct().ToList();
        var catalog = await joined.CatalogItems.AsNoTracking()
            .Where(item => item.ProjectId == invoice.ProjectId && catalogIds.Contains(item.Id))
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        var itemIds = invoice.Lines.Where(line => line.ProjectItemId is not null).Select(line => line.ProjectItemId!.Value).Distinct().ToList();
        var items = await joined.ProjectItems.AsNoTracking()
            .Where(item => item.ProjectId == invoice.ProjectId && itemIds.Contains(item.Id))
            .Select(item => item.Id)
            .ToListAsync(cancellationToken);
        if (catalog.Count != catalogIds.Count || items.Count != itemIds.Count)
        {
            throw new InvoiceNotPostableException("A catalog item or cost item of the invoice is no longer on this project.");
        }

        var lineIds = invoice.Lines.Select(line => line.InvoiceLineId).ToList();
        if (await joined.ProjectExpenses.AnyAsync(
                expense => expense.SourceType == PostedRecordSources.SupplierInvoiceLine && expense.SourceId != null && lineIds.Contains(expense.SourceId.Value),
                cancellationToken))
        {
            throw new InvoiceNotPostableException("This invoice was already posted.");
        }

        var now = clock.UtcNow;
        var created = new List<(int LineId, ProjectExpense Expense)>();
        foreach (var line in invoice.Lines)
        {
            var expense = new ProjectExpense
            {
                ProjectId = invoice.ProjectId,
                Kind = ExpenseKind.Supply,
                ExpenseDate = invoice.InvoiceDate,
                SupplierId = invoice.SupplierId,
                ItemName = line.ItemName,
                Unit = line.Unit,
                Quantity = line.Quantity,
                UnitPrice = line.UnitPrice,
                Amount = decimal.Round(line.Quantity * line.UnitPrice, 2, MidpointRounding.AwayFromZero),
                Channel = PaymentChannel.Credit,
                InvoiceNo = invoice.InvoiceNo,
                DeliveryNoteNo = invoice.DeliveryNoteNo,
                CatalogItemId = line.CatalogItemId,
                CatalogPriceAtRecording = line.CatalogItemId is { } catalogItemId
                    ? await CatalogPrices.CurrentAsync(joined, catalogItemId, invoice.InvoiceDate, cancellationToken)
                    : null,
                ProjectItemId = line.ProjectItemId,
                ItemLinkedAt = line.ProjectItemId is null ? null : now,
                ItemLinkedBy = line.ProjectItemId is null ? null : invoice.ApprovedBy,
                SourceType = PostedRecordSources.SupplierInvoiceLine,
                SourceId = line.InvoiceLineId,
                Notes = invoice.Reference,
                CreatedAt = now,
                CreatedBy = invoice.ApprovedBy,
            };
            // The Accountant's invoice review is the approval (3-way match passed or resolved).
            expense.Review(ExpenseStatus.Approved, invoice.ApprovedBy, now, $"Posted from {invoice.Reference}");
            joined.ProjectExpenses.Add(expense);
            created.Add((line.InvoiceLineId, expense));
        }

        await joined.SaveChangesAsync(cancellationToken);
        log.SupplierInvoicePosted(invoice.ApprovedBy, invoice.ProjectId, created.Count);
        return created.ToDictionary(entry => entry.LineId, entry => entry.Expense.Id);
    }

    public async Task<IReadOnlyDictionary<int, PostedLineState>> StatesAsync(IReadOnlyCollection<int> invoiceLineIds, CancellationToken cancellationToken)
    {
        if (invoiceLineIds.Count == 0)
        {
            return new Dictionary<int, PostedLineState>();
        }

        var records = await db.ProjectExpenses.AsNoTracking()
            .Where(expense => expense.SourceType == PostedRecordSources.SupplierInvoiceLine && expense.SourceId != null
                              && invoiceLineIds.Contains(expense.SourceId.Value))
            .Select(expense => new { expense.Id, expense.ProjectId, LineId = expense.SourceId!.Value })
            .ToListAsync(cancellationToken);

        var states = new Dictionary<int, PostedLineState>();
        foreach (var project in records.GroupBy(record => record.ProjectId))
        {
            var ids = project.Select(record => record.Id).ToList();
            var rows = await ExpenseQueries.SettlementRows(db, project.Key, ids).ToListAsync(cancellationToken);
            foreach (var record in project)
            {
                var row = rows.Single(candidate => candidate.ExpenseId == record.Id);
                states[record.LineId] = new PostedLineState(record.LineId, record.Id, project.Key, row.Amount, ExpenseSettlement.Paid(row));
            }
        }

        return states;
    }
}
