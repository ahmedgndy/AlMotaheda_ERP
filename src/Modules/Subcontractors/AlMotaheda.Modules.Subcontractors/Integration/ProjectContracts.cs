using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Features.Accounts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Integration;

/// <summary>
/// The Subcontractors module's answer to <see cref="ISubcontractItemCosts"/>: approved subcontract work per project
/// item, for the Projects items screens. Projects never reads this module's tables.
/// </summary>
/// <remarks>
/// A measurement counts on the project item of its contract item; a manpower sheet on its own project item. Only
/// approved work is included, and the contract value never is.
/// </remarks>
internal sealed class SubcontractItemCosts(SubcontractorsDbContext db) : ISubcontractItemCosts
{
    public async Task<IReadOnlyList<ItemCostTotal>> TotalsByItemAsync(int projectId, CancellationToken cancellationToken)
    {
        var work = await ItemCostQueries.ApprovedWork(db, projectId, itemFilter: false, itemId: null).ToListAsync(cancellationToken);
        return SubcontractFigures.ItemTotals(work.Select(row => row.Work));
    }

    public async Task<IReadOnlyList<SubcontractItemLine>> LinesAsync(int projectId, int? itemId, CancellationToken cancellationToken)
    {
        var rows = await ItemCostQueries.ApprovedWork(db, projectId, itemFilter: true, itemId).ToListAsync(cancellationToken);

        return rows
            .OrderByDescending(row => row.Date)
            .ThenBy(row => row.Kind)
            .ThenByDescending(row => row.Id)
            .Select(row => row.ToLine())
            .ToList();
    }

    public async Task<IReadOnlyDictionary<int, IReadOnlyList<SubcontractItemLine>>> LinesByItemAsync(
        int projectId, IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return new Dictionary<int, IReadOnlyList<SubcontractItemLine>>();
        }

        var ids = itemIds.Distinct().ToArray();
        var rows = await ItemCostQueries.ApprovedWorkOn(db, projectId, ids).ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.ProjectItemId!.Value)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<SubcontractItemLine>)group
                    .OrderByDescending(row => row.Date)
                    .ThenBy(row => row.Kind)
                    .ThenByDescending(row => row.Id)
                    .Select(row => row.ToLine())
                    .ToList());
    }

    public async Task<IReadOnlyList<SubcontractItemCommitment>> CommitmentsByItemAsync(
        int projectId, IReadOnlyCollection<int> itemIds, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return [];
        }

        var ids = itemIds.Distinct().ToArray();
        var lines = await ItemCostQueries.CommitmentLines(db, projectId, ids).ToListAsync(cancellationToken);

        return lines
            .OrderBy(line => line.ItemId)
            .ThenBy(line => line.ContractId)
            .ThenBy(line => line.ContractItemId)
            .Select(line =>
            {
                var value = Money.Round(line.Quantity * line.UnitPrice);
                // The same rule as the project's commitment (SubcontractFigures.CostFigures): every contract not completed.
                var remaining = line.Status == ContractStatus.Completed ? 0 : Math.Max(0, value - line.Executed);
                return new SubcontractItemCommitment(
                    line.ItemId, line.ContractId, line.ContractNo, line.ContractItemId, line.ContractItemName, line.SubcontractorId,
                    line.SubcontractorNameAr, line.SubcontractorNameEn, line.Status.ToString(), value, line.Executed, remaining);
            })
            .ToList();
    }

    public async Task<bool> HasLinksAsync(int projectId, int itemId, CancellationToken cancellationToken) =>
        await db.ContractItems.AnyAsync(item => item.ProjectItemId == itemId && item.Contract.ProjectId == projectId, cancellationToken)
        || await db.ManpowerSheets.AnyAsync(sheet => sheet.ProjectItemId == itemId && sheet.Contract.ProjectId == projectId, cancellationToken);
}

/// <summary>
/// The Subcontractors cost group for the overview and the reports. Recognized = approved work; committed = the
/// remaining value of the contracts that are not completed.
/// </summary>
internal sealed class SubcontractCostSource(SubcontractorsDbContext db) : IProjectCostSource
{
    public string Group => CostGroups.Subcontractors;

    public async Task<ProjectCostFigures> SummarizeAsync(int projectId, DateOnly? from, DateOnly? to, CancellationToken cancellationToken)
    {
        var contracts = await WorkQueries.Contracts(db, projectId).ToListAsync(cancellationToken);
        if (contracts.Count == 0)
        {
            return ProjectCostFigures.Empty;
        }

        var work = await WorkQueries.LoadWorkAsync(db, projectId, contractId: null, cancellationToken);
        return SubcontractFigures.CostFigures(contracts, work, from, to);
    }
}

/// <summary>One piece of approved work with the names the items screens show.</summary>
internal sealed class ItemWorkRow
{
    public WorkKind Kind { get; init; }
    public int Id { get; init; }
    public int ContractId { get; init; }
    public string? ContractNo { get; init; }
    public int SubcontractorId { get; init; }
    public string SubcontractorNameAr { get; init; } = "";
    public string SubcontractorNameEn { get; init; } = "";
    public SubcontractorSpecialty Specialty { get; init; }
    public int? ContractItemId { get; init; }
    public int? ProjectItemId { get; init; }
    public string Description { get; init; } = "";
    public DateOnly Date { get; init; }
    public decimal Quantity { get; init; }
    public string Unit { get; init; } = "";
    public decimal UnitPrice { get; init; }
    public decimal Amount { get; init; }
    public decimal Allocated { get; init; }

    public WorkRow Work => new(Kind, Id, ContractId, SubcontractorId, ContractItemId, ProjectItemId, Date, Amount, WorkStatus.Approved, Allocated);

    public SubcontractItemLine ToLine() => new(
        Kind.ToString(),
        Id,
        ContractId,
        ContractNo,
        SubcontractorId,
        SubcontractorNameAr,
        SubcontractorNameEn,
        Specialty.ToString(),
        ContractItemId,
        Description,
        Date,
        Quantity,
        Unit,
        UnitPrice,
        Amount,
        Allocated,
        Amount - Allocated,
        PaymentStates.Of(Amount, Allocated));
}

/// <summary>One contract line linked to a project item, with its approved measured work.</summary>
internal sealed class CommitmentLineRow
{
    public int ItemId { get; init; }
    public int ContractId { get; init; }
    public string? ContractNo { get; init; }
    public ContractStatus Status { get; init; }
    public int ContractItemId { get; init; }
    public string ContractItemName { get; init; } = "";
    public int SubcontractorId { get; init; }
    public string SubcontractorNameAr { get; init; } = "";
    public string SubcontractorNameEn { get; init; } = "";
    public decimal Quantity { get; init; }
    public decimal UnitPrice { get; init; }
    public decimal Executed { get; init; }
}

/// <summary>The queries behind the items contracts, as <see cref="IQueryable{T}"/> so tests can prove they translate.</summary>
internal static class ItemCostQueries
{
    /// <summary>The approved work of a project on some project items: the rows of <see cref="ApprovedWork"/> on those items.</summary>
    public static IQueryable<ItemWorkRow> ApprovedWorkOn(SubcontractorsDbContext db, int projectId, int[] itemIds) =>
        ApprovedWork(db, projectId, itemFilter: false, itemId: null)
            .Where(row => row.ProjectItemId != null && itemIds.Contains(row.ProjectItemId.Value));

    /// <summary>The contract lines of a project linked to some project items, with their approved measured amount.</summary>
    public static IQueryable<CommitmentLineRow> CommitmentLines(SubcontractorsDbContext db, int projectId, int[] itemIds) =>
        db.ContractItems.AsNoTracking()
            .Where(item => item.Contract.ProjectId == projectId && item.ProjectItemId != null && itemIds.Contains(item.ProjectItemId.Value))
            .Select(item => new CommitmentLineRow
            {
                ItemId = item.ProjectItemId!.Value,
                ContractId = item.ContractId,
                ContractNo = item.Contract.ContractNo,
                Status = item.Contract.Status,
                ContractItemId = item.Id,
                ContractItemName = item.Name,
                SubcontractorId = item.Contract.SubcontractorId,
                SubcontractorNameAr = item.Contract.Subcontractor.NameAr,
                SubcontractorNameEn = item.Contract.Subcontractor.NameEn,
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                Executed = db.ProgressMeasurements
                    .Where(measurement => measurement.ContractItemId == item.Id && measurement.Status == WorkStatus.Approved)
                    .Sum(measurement => (decimal?)measurement.Amount) ?? 0,
            });

    /// <summary>
    /// The approved measurements and manpower sheets of a project. With <paramref name="itemFilter"/>, only those on
    /// <paramref name="itemId"/>, or those not linked yet when it is <c>null</c>.
    /// </summary>
    /// <remarks>
    /// A manpower sheet's quantity is in worker-days (workers × days) at its daily rate; its description is its
    /// work type, which the client shows in its own language.
    /// </remarks>
    public static IQueryable<ItemWorkRow> ApprovedWork(SubcontractorsDbContext db, int projectId, bool itemFilter, int? itemId)
    {
        var measurements = db.ProgressMeasurements.AsNoTracking()
            .Where(measurement => measurement.ContractItem.Contract.ProjectId == projectId && measurement.Status == WorkStatus.Approved);
        var sheets = db.ManpowerSheets.AsNoTracking()
            .Where(sheet => sheet.Contract.ProjectId == projectId && sheet.Status == WorkStatus.Approved);

        if (itemFilter)
        {
            measurements = measurements.Where(measurement => measurement.ContractItem.ProjectItemId == itemId);
            sheets = sheets.Where(sheet => sheet.ProjectItemId == itemId);
        }

        return measurements
            .Select(measurement => new ItemWorkRow
            {
                Kind = WorkKind.Measurement,
                Id = measurement.Id,
                ContractId = measurement.ContractItem.ContractId,
                ContractNo = measurement.ContractItem.Contract.ContractNo,
                SubcontractorId = measurement.ContractItem.Contract.SubcontractorId,
                SubcontractorNameAr = measurement.ContractItem.Contract.Subcontractor.NameAr,
                SubcontractorNameEn = measurement.ContractItem.Contract.Subcontractor.NameEn,
                Specialty = measurement.ContractItem.Contract.Subcontractor.Specialty,
                ContractItemId = measurement.ContractItemId,
                ProjectItemId = measurement.ContractItem.ProjectItemId,
                Description = measurement.ContractItem.Name,
                Date = measurement.MeasuredOn,
                Quantity = measurement.Quantity,
                Unit = measurement.ContractItem.Unit,
                UnitPrice = measurement.UnitPrice,
                Amount = measurement.Amount,
                Allocated = db.SubPaymentAllocations
                    .Where(allocation => allocation.MeasurementId == measurement.Id && allocation.Payment.VoidedAt == null)
                    .Sum(allocation => allocation.Amount),
            })
            .Concat(sheets.Select(sheet => new ItemWorkRow
            {
                Kind = WorkKind.Manpower,
                Id = sheet.Id,
                ContractId = sheet.ContractId,
                ContractNo = sheet.Contract.ContractNo,
                SubcontractorId = sheet.Contract.SubcontractorId,
                SubcontractorNameAr = sheet.Contract.Subcontractor.NameAr,
                SubcontractorNameEn = sheet.Contract.Subcontractor.NameEn,
                Specialty = sheet.Contract.Subcontractor.Specialty,
                ContractItemId = null,
                ProjectItemId = sheet.ProjectItemId,
                Description = sheet.WorkType,
                Date = sheet.ToDate,
                Quantity = sheet.Workers * sheet.Days,
                Unit = MeasurementUnits.Day,
                UnitPrice = sheet.DailyRate,
                Amount = sheet.Amount,
                Allocated = db.SubPaymentAllocations
                    .Where(allocation => allocation.ManpowerSheetId == sheet.Id && allocation.Payment.VoidedAt == null)
                    .Sum(allocation => allocation.Amount),
            }));
    }
}
