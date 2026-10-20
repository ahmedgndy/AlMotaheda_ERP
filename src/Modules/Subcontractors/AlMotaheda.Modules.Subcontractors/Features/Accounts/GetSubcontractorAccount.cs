using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Subcontractors.Common;
using AlMotaheda.Modules.Subcontractors.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Features.Accounts;

/// <summary>One kind of entry on a company's ledger. Sent by name.</summary>
internal static class SubLedgerTypes
{
    /// <summary>A contract signed: its value is a commitment, not a liability (no balance change).</summary>
    public const string Contract = "Contract";

    /// <summary>An approved change of a contract's value (no balance change).</summary>
    public const string Variation = "Variation";

    /// <summary>Approved measured work or manpower: the company is owed it (debit).</summary>
    public const string Work = "Work";

    /// <summary>Money paid to the company (credit). An advance is paid before work.</summary>
    public const string Payment = "Payment";

    /// <summary>A payment voided: the money no longer counts (debit back).</summary>
    public const string PaymentVoided = "PaymentVoided";
}

/// <summary>One line of a company's ledger, on one engagement (contract) of one project.</summary>
/// <param name="Debit">What the company became owed (approved work, a voided payment taken back).</param>
/// <param name="Credit">What it was paid.</param>
/// <param name="Balance">Running: Σ debit − Σ credit = approved work − payments. Negative means paid ahead (an advance).</param>
/// <param name="ValueChange">A contract's value, or a variation's amount: a commitment, never in the balance.</param>
internal sealed record SubLedgerEntry(
    DateOnly Date,
    string Type,
    int ProjectId,
    string? ProjectCode,
    string? ProjectNameAr,
    string? ProjectNameEn,
    int ContractId,
    string? ContractNo,
    int? ProjectItemId,
    string? ProjectItemNameAr,
    string? ProjectItemNameEn,
    string? Description,
    decimal? ValueChange,
    decimal Debit,
    decimal Credit,
    decimal Balance,
    string? SourceType,
    int SourceId,
    int? RecordedBy);

/// <summary>One engagement of the company on one project, with the figures of its contract account.</summary>
internal sealed record SubEngagementView(
    int ContractId,
    string? ContractNo,
    int ProjectId,
    string? ProjectCode,
    string? ProjectNameAr,
    string? ProjectNameEn,
    int? ProjectItemId,
    string? ProjectItemNameAr,
    string? ProjectItemNameEn,
    string Status,
    decimal SignedValue,
    decimal ApprovedVariations,
    ContractAccount Account);

/// <summary>The company's figures on one project: the sum of its engagements there.</summary>
internal sealed record SubProjectTotals(int ProjectId, string? ProjectCode, string? ProjectNameAr, string? ProjectNameEn, ContractTotals Totals);

/// <summary>A dated fact of one contract, before it becomes a ledger line.</summary>
internal sealed record SubLedgerFact(
    DateOnly Date, int Order, string Type, int ContractId, int? ProjectItemId, string? Description, decimal? ValueChange,
    decimal Debit, decimal Credit, string? SourceType, int SourceId, int? RecordedBy);

/// <summary>
/// The arithmetic of a company's account across projects, with no database access. Every figure is the sum of the
/// engagements' own contract accounts (<see cref="ContractAccount"/>), so the company page, each project's
/// subcontractor screen and the work-item breakdown can't disagree, and a payment counts once: it belongs to one
/// contract of one project.
/// </summary>
internal static class SubcontractorLedger
{
    /// <summary>The ledger, oldest first, with the running balance (approved work − payments).</summary>
    public static IReadOnlyList<(SubLedgerFact Fact, decimal Balance)> Run(IEnumerable<SubLedgerFact> facts)
    {
        decimal balance = 0;
        var lines = new List<(SubLedgerFact, decimal)>();
        foreach (var fact in facts.OrderBy(fact => fact.Date).ThenBy(fact => fact.Order).ThenBy(fact => fact.SourceId))
        {
            balance += fact.Debit - fact.Credit;
            lines.Add((fact, balance));
        }

        return lines;
    }
}

/// <summary>
/// <c>GET /api/subcontractors/{id}/account</c>: one subcontractor company across every project the caller may see:
/// each engagement (project, main work item, contract value and approved changes, approved work, payments, what is
/// owed, what is left of the contract, claims waiting for review), the totals per project and overall, and the
/// chronological ledger. subcontractors See money.
/// </summary>
/// <remarks>
/// <para>The four figures are never mixed: <c>remainingToExecute</c> (contract value not executed yet),
/// <c>outstandingWork</c> (approved work not paid yet: what is owed now), <c>paidTotal</c> (money paid) and
/// <c>pendingReview</c> (claims not approved yet).</para>
/// <para>A project the caller can't see is left out of every figure, so the totals are "the company on the
/// projects you can see". An Executive or an Accountant sees every project.</para>
/// </remarks>
internal static class GetSubcontractorAccount
{
    internal sealed record Response(
        Companies.SubcontractorView Company,
        ContractTotals Totals,
        IReadOnlyList<SubProjectTotals> Projects,
        IReadOnlyList<SubEngagementView> Engagements,
        IReadOnlyList<SubLedgerEntry> Ledger,
        int Page,
        int PageSize,
        int TotalCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/subcontractors/{subcontractorId:int}/account", HandleAsync)
            .RequirePermission(SubPermissions.Module, PermissionAction.SeeMoney)
            .WithName("GetSubcontractorAccount")
            .WithSummary("A subcontractor's account across every project you can see: engagements, totals per project and the chronological ledger (projectId, page, pageSize)");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int subcontractorId,
        HttpContext httpContext,
        SubcontractorsDbContext db,
        IVisibleProjects visibleProjects,
        IProjectDirectory directory,
        CancellationToken cancellationToken,
        int? projectId = null,
        int page = 1,
        int pageSize = 50)
    {
        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return SubcontractorsProblems.Validation(errors);
        }

        var company = await db.Subcontractors.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == subcontractorId, cancellationToken);
        if (company is null)
        {
            return SubcontractorsProblems.SubcontractorNotFound();
        }

        var visible = await visibleProjects.IdsAsync(httpContext.User, cancellationToken);
        var contracts = await db.SubContracts.AsNoTracking()
            .Where(contract => contract.SubcontractorId == subcontractorId)
            .Where(contract => projectId == null || contract.ProjectId == projectId)
            .Select(contract => new
            {
                contract.Id,
                contract.ProjectId,
                contract.ProjectItemId,
                contract.ContractNo,
                contract.Value,
                contract.RetentionPercent,
                contract.Status,
                contract.StartDate,
                contract.CreatedBy,
                ApprovedVariations = contract.Variations.Where(variation => variation.Status == VariationStatus.Approved)
                    .Sum(variation => (decimal?)variation.Amount) ?? 0,
            })
            .ToListAsync(cancellationToken);
        contracts = contracts.Where(contract => visible is null || visible.Contains(contract.ProjectId)).ToList();
        var ids = contracts.Select(contract => contract.Id).ToArray();

        var work = (await CompanyQueries.Measurements(db, ids).ToListAsync(cancellationToken))
            .Concat(await CompanyQueries.Manpower(db, ids).ToListAsync(cancellationToken))
            .ToList();
        var payments = await CompanyQueries.Payments(db, ids).ToListAsync(cancellationToken);
        var variations = await db.ContractVariations.AsNoTracking()
            .Where(variation => ids.Contains(variation.ContractId) && variation.Status == VariationStatus.Approved)
            .Select(variation => new { variation.Id, variation.ContractId, variation.Amount, variation.Reason, variation.DecidedAt, variation.DecidedBy })
            .ToListAsync(cancellationToken);

        var projects = await directory.FindAsync(contracts.Select(contract => contract.ProjectId).Distinct().ToList(), cancellationToken);
        var itemIds = contracts.Select(contract => contract.ProjectItemId).Concat(work.Select(row => row.ProjectItemId)).OfType<int>().Distinct().ToList();
        var items = await directory.ItemsAsync(itemIds, cancellationToken);

        var workByContract = work.ToLookup(row => row.ContractId);
        var paymentsByContract = payments.ToLookup(row => row.ContractId);
        var engagements = contracts
            .Select(contract =>
            {
                var terms = new ContractRow(contract.Id, subcontractorId, contract.Value + contract.ApprovedVariations, contract.RetentionPercent, contract.Status);
                var account = ContractAccount.Compute(
                    terms,
                    workByContract[contract.Id].Select(row => row.Row),
                    paymentsByContract[contract.Id].Select(row => new PaymentRow(row.Id, row.ContractId, row.PaymentDate, row.Amount, row.VoidedAt != null, row.Applied)));
                var project = projects.GetValueOrDefault(contract.ProjectId);
                var item = contract.ProjectItemId is { } itemId ? items.GetValueOrDefault(itemId) : null;
                return new SubEngagementView(
                    contract.Id, contract.ContractNo, contract.ProjectId, project?.Code, project?.NameAr, project?.NameEn,
                    contract.ProjectItemId, item?.NameAr, item?.NameEn, contract.Status.ToString(), contract.Value, contract.ApprovedVariations, account);
            })
            .OrderBy(engagement => engagement.ProjectCode)
            .ThenBy(engagement => engagement.ContractId)
            .ToList();

        var perProject = engagements
            .GroupBy(engagement => engagement.ProjectId)
            .Select(group => new SubProjectTotals(
                group.Key, group.First().ProjectCode, group.First().ProjectNameAr, group.First().ProjectNameEn,
                ContractTotals.Sum(group.Select(engagement => engagement.Account).ToList())))
            .ToList();

        // The ledger: contracts and approved variations (commitments), approved work (owed), payments (paid), voids.
        var facts = new List<SubLedgerFact>();
        facts.AddRange(contracts.Select(contract => new SubLedgerFact(
            contract.StartDate, 0, SubLedgerTypes.Contract, contract.Id, contract.ProjectItemId, contract.ContractNo, contract.Value, 0, 0,
            "SubContract", contract.Id, contract.CreatedBy)));
        facts.AddRange(variations.Select(variation => new SubLedgerFact(
            DateOnly.FromDateTime(variation.DecidedAt!.Value), 1, SubLedgerTypes.Variation, variation.ContractId, null, variation.Reason, variation.Amount,
            0, 0, "ContractVariation", variation.Id, variation.DecidedBy)));
        facts.AddRange(work.Where(row => row.Row.IsApproved).Select(row => new SubLedgerFact(
            row.Row.Date, 2, SubLedgerTypes.Work, row.ContractId, row.ProjectItemId, row.Description, null, row.Row.Amount, 0,
            row.Row.Kind == WorkKind.Measurement ? "ProgressMeasurement" : "ManpowerSheet", row.Row.Id, row.ReviewedBy)));
        facts.AddRange(payments.Select(payment => new SubLedgerFact(
            payment.PaymentDate, 3, SubLedgerTypes.Payment, payment.ContractId, null, $"{payment.Purpose} · {payment.Method}{(payment.Reference is null ? "" : " · " + payment.Reference)}",
            null, 0, payment.Amount, "SubPayment", payment.Id, payment.CreatedBy)));
        facts.AddRange(payments.Where(payment => payment.VoidedAt != null).Select(payment => new SubLedgerFact(
            DateOnly.FromDateTime(payment.VoidedAt!.Value), 4, SubLedgerTypes.PaymentVoided, payment.ContractId, null, payment.VoidReason, null,
            payment.Amount, 0, "SubPayment", payment.Id, payment.VoidedBy)));

        var byContract = contracts.ToDictionary(contract => contract.Id);
        var ledger = SubcontractorLedger.Run(facts)
            .Select(line =>
            {
                var contract = byContract[line.Fact.ContractId];
                var project = projects.GetValueOrDefault(contract.ProjectId);
                var itemId = line.Fact.ProjectItemId ?? contract.ProjectItemId;
                var item = itemId is { } id ? items.GetValueOrDefault(id) : null;
                return new SubLedgerEntry(
                    line.Fact.Date, line.Fact.Type, contract.ProjectId, project?.Code, project?.NameAr, project?.NameEn, contract.Id, contract.ContractNo,
                    itemId, item?.NameAr, item?.NameEn, line.Fact.Description, line.Fact.ValueChange, line.Fact.Debit, line.Fact.Credit, line.Balance,
                    line.Fact.SourceType, line.Fact.SourceId, line.Fact.RecordedBy);
            })
            .ToList();

        // Newest first on screen; the running balance was computed oldest first.
        var newestFirst = ledger.AsEnumerable().Reverse().ToList();
        return TypedResults.Ok(new Response(
            Companies.SubcontractorView.From(company),
            ContractTotals.Sum(engagements.Select(engagement => engagement.Account).ToList()),
            perProject,
            engagements,
            newestFirst.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            page,
            pageSize,
            newestFirst.Count));
    }
}

/// <summary>One piece of work of the company, with what the ledger shows next to it.</summary>
internal sealed record CompanyWorkRow(int ContractId, int? ProjectItemId, string? Description, int? ReviewedBy, WorkRow Row);

/// <summary>One payment of the company, voided ones included.</summary>
internal sealed record CompanyPaymentRow(
    int Id, int ContractId, DateOnly PaymentDate, decimal Amount, SubPaymentMethod Method, SubPaymentPurpose Purpose, string? Reference, int CreatedBy,
    DateTime? VoidedAt, int? VoidedBy, string? VoidReason, decimal Applied);

/// <summary>The company account's queries (across projects), as <see cref="IQueryable{T}"/> so tests can prove they translate.</summary>
internal static class CompanyQueries
{
    /// <summary>The company's measured work. Read apart from <see cref="Manpower"/>: SQL can't union two shaped projections.</summary>
    public static IQueryable<CompanyWorkRow> Measurements(SubcontractorsDbContext db, int[] contractIds) =>
        db.ProgressMeasurements.AsNoTracking()
            .Where(measurement => contractIds.Contains(measurement.ContractItem.ContractId))
            .Select(measurement => new CompanyWorkRow(
                measurement.ContractItem.ContractId,
                measurement.ContractItem.ProjectItemId,
                measurement.ContractItem.Name,
                measurement.ReviewedBy,
                new WorkRow(
                    WorkKind.Measurement, measurement.Id, measurement.ContractItem.ContractId, measurement.ContractItem.Contract.SubcontractorId,
                    measurement.ContractItemId, measurement.ContractItem.ProjectItemId, measurement.MeasuredOn, measurement.Amount, measurement.Status,
                    db.SubPaymentAllocations.Where(allocation => allocation.MeasurementId == measurement.Id && allocation.Payment.VoidedAt == null)
                        .Sum(allocation => allocation.Amount))));

    /// <summary>The company's manpower sheets.</summary>
    public static IQueryable<CompanyWorkRow> Manpower(SubcontractorsDbContext db, int[] contractIds) =>
        db.ManpowerSheets.AsNoTracking()
            .Where(sheet => contractIds.Contains(sheet.ContractId))
            .Select(sheet => new CompanyWorkRow(
                sheet.ContractId,
                sheet.ProjectItemId,
                sheet.WorkType,
                sheet.ReviewedBy,
                new WorkRow(
                    WorkKind.Manpower, sheet.Id, sheet.ContractId, sheet.Contract.SubcontractorId, null, sheet.ProjectItemId, sheet.ToDate,
                    sheet.Amount, sheet.Status,
                    db.SubPaymentAllocations.Where(allocation => allocation.ManpowerSheetId == sheet.Id && allocation.Payment.VoidedAt == null)
                        .Sum(allocation => allocation.Amount))));

    public static IQueryable<CompanyPaymentRow> Payments(SubcontractorsDbContext db, int[] contractIds) =>
        db.SubPayments.AsNoTracking()
            .Where(payment => contractIds.Contains(payment.ContractId))
            .Select(payment => new CompanyPaymentRow(
                payment.Id, payment.ContractId, payment.PaymentDate, payment.Amount, payment.Method, payment.Purpose,
                payment.Reference, payment.CreatedBy, payment.VoidedAt, payment.VoidedBy, payment.VoidReason,
                payment.Allocations.Sum(allocation => (decimal?)allocation.Amount) ?? 0));
}
