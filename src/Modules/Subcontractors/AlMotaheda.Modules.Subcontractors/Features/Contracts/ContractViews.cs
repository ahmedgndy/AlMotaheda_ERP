using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Subcontractors.Common;
using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Features.Accounts;
using AlMotaheda.Modules.Subcontractors.Features.Work;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Features.Contracts;

/// <summary>The company of a contract.</summary>
internal sealed record CompanyRef(int Id, string NameAr, string NameEn, string Specialty, string Status);

/// <summary>
/// One contract, as the list, the detail and the contract writes return it. Money (<see cref="Value"/>,
/// <see cref="DailyRate"/>, <see cref="Account"/>) is <c>null</c> without subcontractors See money.
/// </summary>
/// <remarks>
/// <see cref="Value"/> is the signed value (what the edit form changes); <see cref="EffectiveValue"/> adds the
/// approved variations, and every figure of <see cref="Account"/> uses it.
/// </remarks>
internal sealed record SubcontractView(
    int Id,
    int ProjectId,
    CompanyRef Subcontractor,
    string? ContractNo,
    string Model,
    decimal? Value,
    decimal? DailyRate,
    decimal RetentionPercent,
    DateOnly StartDate,
    DateOnly? EndDate,
    string Status,
    string? Scope,
    int ItemCount,
    ContractAccount? Account,
    DateTime CreatedAt,
    int CreatedBy,
    DateTime? UpdatedAt,
    int? UpdatedBy,
    string RowVersion,
    int? ProjectItemId = null,
    string? ProjectItemNameAr = null,
    string? ProjectItemNameEn = null,
    decimal? ApprovedVariations = null,
    decimal? EffectiveValue = null)
{
    public static SubcontractView From(
        ContractListRow row, ContractAccount account, bool seesMoney, IReadOnlyDictionary<int, ProjectItemRef>? itemNames = null)
    {
        var item = row.ProjectItemId is { } itemId && itemNames is not null ? itemNames.GetValueOrDefault(itemId) : null;
        return From(row, account, seesMoney) with
        {
            ProjectItemId = row.ProjectItemId,
            ProjectItemNameAr = item?.NameAr,
            ProjectItemNameEn = item?.NameEn,
            ApprovedVariations = seesMoney ? row.ApprovedVariations : null,
            EffectiveValue = seesMoney ? row.Value + row.ApprovedVariations : null,
        };
    }

    private static SubcontractView From(ContractListRow row, ContractAccount account, bool seesMoney) => new(
        row.Id,
        row.ProjectId,
        new CompanyRef(row.SubcontractorId, row.SubcontractorNameAr, row.SubcontractorNameEn, row.Specialty.ToString(), row.SubcontractorStatus.ToString()),
        row.ContractNo,
        row.Model.ToString(),
        seesMoney ? row.Value : null,
        seesMoney ? row.DailyRate : null,
        row.RetentionPercent,
        row.StartDate,
        row.EndDate,
        row.Status.ToString(),
        row.Scope,
        row.ItemCount,
        seesMoney ? account : null,
        row.CreatedAt,
        row.CreatedBy,
        row.UpdatedAt,
        row.UpdatedBy,
        RowVersions.Format(row.RowVersion));
}

/// <summary>
/// One contract item with its progress: measured (approved), waiting for review, and what is left to measure.
/// Prices and amounts are <c>null</c> without See money.
/// </summary>
/// <param name="RemainingQuantity">The quantity minus every measurement that is not rejected: what can still be recorded.</param>
/// <param name="ApprovedPercent">For Job and Lot lines: the approved quantity as a percentage of the line.</param>
internal sealed record ContractItemView(
    int Id,
    int ContractId,
    int? ProjectItemId,
    string? ProjectItemNameAr,
    string? ProjectItemNameEn,
    string Name,
    string WorkType,
    string Unit,
    decimal Quantity,
    decimal? UnitPrice,
    decimal? LineTotal,
    decimal ApprovedQuantity,
    decimal PendingQuantity,
    decimal RemainingQuantity,
    decimal? ApprovedPercent,
    decimal? ExecutedAmount,
    decimal? RemainingValue,
    int MeasurementCount,
    string? Description,
    string? Notes,
    DateTime CreatedAt,
    int CreatedBy,
    DateTime? UpdatedAt,
    int? UpdatedBy,
    string RowVersion)
{
    public static ContractItemView From(
        ContractItem item, IEnumerable<MeasurementRow> measurements, IReadOnlyDictionary<int, ProjectItemRef> projectItems, bool seesMoney)
    {
        var own = measurements.Where(measurement => measurement.ContractItemId == item.Id).ToList();
        var approved = own.Where(measurement => measurement.Status == WorkStatus.Approved).ToList();
        var approvedQuantity = approved.Sum(measurement => measurement.Quantity);
        var pendingQuantity = own.Where(measurement => measurement.Status == WorkStatus.Recorded).Sum(measurement => measurement.Quantity);
        var executed = approved.Sum(measurement => measurement.Amount);
        var lineTotal = Money.Round(item.Quantity * item.UnitPrice);
        var projectItem = item.ProjectItemId is { } projectItemId ? projectItems.GetValueOrDefault(projectItemId) : null;

        return new ContractItemView(
            item.Id,
            item.ContractId,
            item.ProjectItemId,
            projectItem?.NameAr,
            projectItem?.NameEn,
            item.Name,
            item.WorkType,
            item.Unit,
            item.Quantity,
            seesMoney ? item.UnitPrice : null,
            seesMoney ? lineTotal : null,
            approvedQuantity,
            pendingQuantity,
            WorkRules.RemainingQuantity(item.Quantity, approvedQuantity + pendingQuantity),
            MeasurementUnits.IsLumpSum(item.Unit) ? WorkRules.PercentOfLine(approvedQuantity, item.Quantity) : null,
            seesMoney ? executed : null,
            seesMoney ? Math.Max(0, lineTotal - executed) : null,
            own.Count,
            item.Description,
            item.Notes,
            item.CreatedAt,
            item.CreatedBy,
            item.UpdatedAt,
            item.UpdatedBy,
            RowVersions.Format(item.RowVersion));
    }
}

/// <summary>
/// One measurement. <see cref="Paid"/> is what payment allocations settle on it (an unapplied advance is not in
/// it); the money fields are <c>null</c> without See money, and the payment figures exist only once approved.
/// </summary>
internal sealed record MeasurementView(
    int Id,
    int ContractId,
    int ContractItemId,
    string ContractItemName,
    string Unit,
    DateOnly MeasuredOn,
    decimal Quantity,
    decimal? PercentOfLine,
    decimal? UnitPrice,
    decimal? Amount,
    string Status,
    string? Note,
    decimal? Paid,
    decimal? Outstanding,
    string? PaymentState,
    DateTime? ReviewedAt,
    SubPersonRef? Reviewer,
    string? ReviewNote,
    DateTime CreatedAt,
    SubPersonRef Recorder,
    string RowVersion)
{
    public static MeasurementView From(MeasurementRow row, IReadOnlyDictionary<int, UserSummary> people, bool seesMoney)
    {
        var approved = row.Status == WorkStatus.Approved;
        var showPaid = seesMoney && approved;

        return new MeasurementView(
            row.Id,
            row.ContractId,
            row.ContractItemId,
            row.ContractItemName,
            row.Unit,
            row.MeasuredOn,
            row.Quantity,
            MeasurementUnits.IsLumpSum(row.Unit) ? WorkRules.PercentOfLine(row.Quantity, row.ItemQuantity) : null,
            seesMoney ? row.UnitPrice : null,
            seesMoney ? row.Amount : null,
            row.Status.ToString(),
            row.Note,
            showPaid ? row.Allocated : null,
            showPaid ? row.Amount - row.Allocated : null,
            showPaid ? PaymentStates.Of(row.Amount, row.Allocated) : null,
            row.ReviewedAt,
            SubPersonRef.From(row.ReviewedBy, people),
            row.ReviewNote,
            row.CreatedAt,
            SubPersonRef.From(row.CreatedBy, people),
            RowVersions.Format(row.RowVersion));
    }
}

/// <summary>One manpower sheet, with the same money rules as <see cref="MeasurementView"/>.</summary>
internal sealed record ManpowerView(
    int Id,
    int ContractId,
    int? ProjectItemId,
    string? ProjectItemNameAr,
    string? ProjectItemNameEn,
    DateOnly FromDate,
    DateOnly ToDate,
    int Workers,
    decimal Days,
    decimal? DailyRate,
    decimal? Amount,
    string WorkType,
    string? Note,
    string Status,
    decimal? Paid,
    decimal? Outstanding,
    string? PaymentState,
    DateTime? ReviewedAt,
    SubPersonRef? Reviewer,
    string? ReviewNote,
    DateTime CreatedAt,
    SubPersonRef Recorder,
    DateTime? UpdatedAt,
    int? UpdatedBy,
    string RowVersion)
{
    public static ManpowerView From(
        ManpowerRow row, IReadOnlyDictionary<int, UserSummary> people, IReadOnlyDictionary<int, ProjectItemRef> projectItems, bool seesMoney)
    {
        var showPaid = seesMoney && row.Status == WorkStatus.Approved;
        var projectItem = row.ProjectItemId is { } projectItemId ? projectItems.GetValueOrDefault(projectItemId) : null;

        return new ManpowerView(
            row.Id,
            row.ContractId,
            row.ProjectItemId,
            projectItem?.NameAr,
            projectItem?.NameEn,
            row.FromDate,
            row.ToDate,
            row.Workers,
            row.Days,
            seesMoney ? row.DailyRate : null,
            seesMoney ? row.Amount : null,
            row.WorkType,
            row.Note,
            row.Status.ToString(),
            showPaid ? row.Allocated : null,
            showPaid ? row.Amount - row.Allocated : null,
            showPaid ? PaymentStates.Of(row.Amount, row.Allocated) : null,
            row.ReviewedAt,
            SubPersonRef.From(row.ReviewedBy, people),
            row.ReviewNote,
            row.CreatedAt,
            SubPersonRef.From(row.CreatedBy, people),
            row.UpdatedAt,
            row.UpdatedBy,
            RowVersions.Format(row.RowVersion));
    }
}

/// <summary>One allocation of a payment: which work it settles and how much.</summary>
/// <param name="WorkType"><c>Measurement</c> or <c>Manpower</c>.</param>
internal sealed record SubAllocationView(string WorkType, int WorkId, decimal Amount);

/// <summary>
/// One payment. <see cref="Applied"/> is what it settles on work, <see cref="Unapplied"/> the advance still waiting
/// for work. A voided payment applies nothing; its allocations are still listed for the record.
/// </summary>
internal sealed record SubPaymentView(
    int Id,
    int ContractId,
    DateOnly PaymentDate,
    decimal Amount,
    string Method,
    string Purpose,
    string? Reference,
    string? Notes,
    decimal Applied,
    decimal Unapplied,
    IReadOnlyList<SubAllocationView> Allocations,
    bool IsVoided,
    DateTime? VoidedAt,
    int? VoidedBy,
    string? VoidReason,
    DateTime CreatedAt,
    SubPersonRef Recorder,
    string RowVersion)
{
    public static SubPaymentView From(PaymentDetailRow row, IReadOnlyDictionary<int, UserSummary> people)
    {
        var applied = row.IsVoided ? 0 : row.Allocations.Sum(allocation => allocation.Amount);

        return new SubPaymentView(
            row.Id,
            row.ContractId,
            row.PaymentDate,
            row.Amount,
            row.Method.ToString(),
            row.Purpose.ToString(),
            row.Reference,
            row.Notes,
            applied,
            row.IsVoided ? 0 : row.Amount - applied,
            row.Allocations
                .Select(allocation => allocation.MeasurementId is { } measurementId
                    ? new SubAllocationView(nameof(WorkKind.Measurement), measurementId, allocation.Amount)
                    : new SubAllocationView(nameof(WorkKind.Manpower), allocation.ManpowerSheetId ?? 0, allocation.Amount))
                .ToList(),
            row.IsVoided,
            row.VoidedAt,
            row.VoidedBy,
            row.VoidReason,
            row.CreatedAt,
            SubPersonRef.From(row.CreatedBy, people),
            RowVersions.Format(row.RowVersion));
    }
}

/// <summary>
/// One contract with everything under it: company, items (with measured and remaining quantity), measurements,
/// manpower sheets, payments and the account. Payments are <c>null</c> without See money.
/// </summary>
/// <param name="Variations">The contract's changes, newest first; amounts are <c>null</c> without See money.</param>
internal sealed record SubcontractDetailView(
    SubcontractView Contract,
    IReadOnlyList<ContractItemView> Items,
    IReadOnlyList<MeasurementView> Measurements,
    IReadOnlyList<ManpowerView> ManpowerSheets,
    IReadOnlyList<SubPaymentView>? Payments,
    IReadOnlyList<VariationView>? Variations = null);

/// <summary>One change of the contract value: requested, then approved or rejected by someone else.</summary>
internal sealed record VariationView(
    int Id,
    int ContractId,
    decimal? Amount,
    string Reason,
    string? Reference,
    string Status,
    DateTime RequestedAt,
    SubPersonRef RequestedBy,
    DateTime? DecidedAt,
    SubPersonRef? DecidedBy,
    string? DecisionNote)
{
    public static VariationView From(ContractVariation row, IReadOnlyDictionary<int, UserSummary> people, bool seesMoney) => new(
        row.Id, row.ContractId, seesMoney ? row.Amount : null, row.Reason, row.Reference, row.Status.ToString(), row.RequestedAt,
        SubPersonRef.From(row.RequestedBy, people), row.DecidedAt, row.DecidedBy is { } decider ? SubPersonRef.From(decider, people) : null,
        row.DecisionNote);
}

/// <summary>
/// Loads the views of one contract and of the records under it, for the detail and for the answers of the writes.
/// One query per kind of record, then the pure classes do the arithmetic (decision G5).
/// </summary>
internal sealed class ContractDetails(SubcontractorsDbContext db, IUserDirectory users, IProjectItems projectItems)
{
    public async Task<SubcontractDetailView?> LoadAsync(int projectId, int contractId, bool seesMoney, CancellationToken cancellationToken)
    {
        var contract = await ContractQueries.One(db, projectId, contractId).SingleOrDefaultAsync(cancellationToken);
        if (contract is null)
        {
            return null;
        }

        var items = await ContractQueries.Items(db, projectId, contractId).ToListAsync(cancellationToken);
        var measurements = await ContractQueries.Measurements(db, projectId, contractId).ToListAsync(cancellationToken);
        var sheets = await ContractQueries.ManpowerSheets(db, projectId, contractId).ToListAsync(cancellationToken);
        var payments = await ContractQueries.Payments(db, projectId, contractId).ToListAsync(cancellationToken);
        var variations = await db.ContractVariations.AsNoTracking()
            .Where(variation => variation.ContractId == contractId)
            .OrderByDescending(variation => variation.RequestedAt)
            .ThenByDescending(variation => variation.Id)
            .ToListAsync(cancellationToken);

        var itemIds = items.Select(item => item.ProjectItemId).Concat(sheets.Select(sheet => sheet.ProjectItemId))
            .Append(contract.ProjectItemId).OfType<int>().Distinct().ToList();
        var names = await projectItems.FindAsync(projectId, itemIds, cancellationToken);

        var userIds = measurements.SelectMany(row => new[] { (int?)row.CreatedBy, row.ReviewedBy })
            .Concat(sheets.SelectMany(row => new[] { (int?)row.CreatedBy, row.ReviewedBy }))
            .Concat(variations.SelectMany(row => new[] { (int?)row.RequestedBy, row.DecidedBy }))
            .Concat(payments.Select(row => (int?)row.CreatedBy))
            .OfType<int>()
            .Distinct()
            .ToList();
        var people = await users.FindAsync(userIds, cancellationToken);

        var work = measurements.Select(row => row.Work).Concat(sheets.Select(row => row.Work)).ToList();
        var account = ContractAccount.Compute(contract.Terms, work, payments.Select(row => row.Figures));

        return new SubcontractDetailView(
            SubcontractView.From(contract, account, seesMoney, names),
            items.Select(item => ContractItemView.From(item, measurements, names, seesMoney)).ToList(),
            measurements.Select(row => MeasurementView.From(row, people, seesMoney)).ToList(),
            sheets.Select(row => ManpowerView.From(row, people, names, seesMoney)).ToList(),
            seesMoney ? payments.Select(row => SubPaymentView.From(row, people)).ToList() : null,
            variations.Select(row => VariationView.From(row, people, seesMoney)).ToList());
    }

    /// <summary>One contract item with its progress, or <c>null</c> when it is not on this contract of this project.</summary>
    public async Task<ContractItemView?> ItemAsync(int projectId, int contractId, int itemId, bool seesMoney, CancellationToken cancellationToken)
    {
        var item = await ContractQueries.Items(db, projectId, contractId).SingleOrDefaultAsync(candidate => candidate.Id == itemId, cancellationToken);
        if (item is null)
        {
            return null;
        }

        var measurements = await ContractQueries.Measurements(db, projectId, contractId)
            .Where(measurement => measurement.ContractItemId == itemId)
            .ToListAsync(cancellationToken);
        var names = await projectItems.FindAsync(projectId, item.ProjectItemId is { } id ? [id] : [], cancellationToken);

        return ContractItemView.From(item, measurements, names, seesMoney);
    }

    public async Task<MeasurementView?> MeasurementAsync(int projectId, int measurementId, bool seesMoney, CancellationToken cancellationToken)
    {
        var row = await ContractQueries.Measurement(db, projectId, measurementId).SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var people = await users.FindAsync(new[] { (int?)row.CreatedBy, row.ReviewedBy }.OfType<int>().Distinct().ToList(), cancellationToken);
        return MeasurementView.From(row, people, seesMoney);
    }

    public async Task<ManpowerView?> ManpowerAsync(int projectId, int sheetId, bool seesMoney, CancellationToken cancellationToken)
    {
        var row = await ContractQueries.ManpowerSheet(db, projectId, sheetId).SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var people = await users.FindAsync(new[] { (int?)row.CreatedBy, row.ReviewedBy }.OfType<int>().Distinct().ToList(), cancellationToken);
        var names = await projectItems.FindAsync(projectId, row.ProjectItemId is { } id ? [id] : [], cancellationToken);
        return ManpowerView.From(row, people, names, seesMoney);
    }

    public async Task<SubPaymentView?> PaymentAsync(int projectId, int paymentId, CancellationToken cancellationToken)
    {
        var row = await ContractQueries.Payment(db, projectId, paymentId).SingleOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var people = await users.FindAsync([row.CreatedBy], cancellationToken);
        return SubPaymentView.From(row, people);
    }
}
