using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Features.Items;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Suppliers;

/// <summary>A supply agreement as the client sees it, with what has been delivered under it.</summary>
/// <param name="AgreedUnitPrice">Hidden (<c>null</c>) without projects See money, like every amount here.</param>
internal sealed record SupplyAgreementView(
    int Id,
    int ProjectId,
    int SupplierId,
    string? SupplierNameAr,
    string? SupplierNameEn,
    int ProjectItemId,
    string? ProjectItemNameAr,
    string? ProjectItemNameEn,
    int? ParentItemId,
    string MaterialName,
    string Unit,
    decimal? AgreedQuantity,
    decimal? AgreedUnitPrice,
    decimal? AgreedValue,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string Status,
    string? Notes,
    decimal DeliveredQuantity,
    decimal PendingQuantity,
    decimal? RemainingQuantity,
    decimal? Recognized,
    decimal? Invoiced,
    decimal? Paid,
    decimal? Outstanding,
    decimal? RemainingValue,
    int RecordCount,
    string RowVersion)
{
    public static SupplyAgreementView From(
        SupplyAgreement agreement, IReadOnlyCollection<SupplierRecordRow> records, bool seesMoney, Supplier? supplier, ProjectItem? item)
    {
        var progress = AgreementProgress.Of(agreement, records);
        return new SupplyAgreementView(
            agreement.Id, agreement.ProjectId, agreement.SupplierId, supplier?.NameAr, supplier?.NameEn, agreement.ProjectItemId, item?.NameAr, item?.NameEn,
            item?.ParentItemId, agreement.MaterialName, agreement.Unit, agreement.AgreedQuantity, seesMoney ? agreement.AgreedUnitPrice : null,
            seesMoney ? agreement.AgreedValue ?? agreement.AgreedQuantity * agreement.AgreedUnitPrice : null, agreement.StartDate, agreement.EndDate,
            agreement.Status.ToString(), agreement.Notes, progress.DeliveredQuantity, progress.PendingQuantity, progress.RemainingQuantity,
            seesMoney ? progress.Recognized : null, seesMoney ? progress.Invoiced : null, seesMoney ? progress.Paid : null,
            seesMoney ? progress.Outstanding : null, seesMoney ? progress.RemainingValue : null, records.Count, RowVersions.Format(agreement.RowVersion));
    }
}

/// <summary>
/// Supply agreements ("اتفاقيات التوريد"): which supplier supplies which material, in which unit, for which work item
/// of a project, with an optional agreed quantity, price and value. A supply recorded under an agreement takes its
/// work item. Written by the project's manager or an Executive (projects Edit), or by purchasing (procurement Edit).
/// </summary>
internal static class SupplyAgreements
{
    internal sealed record Request(
        int? SupplierId,
        int? ProjectItemId,
        string? MaterialName,
        string? Unit,
        decimal? AgreedQuantity,
        decimal? AgreedUnitPrice,
        decimal? AgreedValue,
        string? StartDate,
        string? EndDate,
        string? Notes,
        string? RowVersion = null);

    internal sealed record CloseRequest(string? RowVersion);

    public static void Map(IEndpointRouteBuilder projects)
    {
        projects.MapGet("/{projectId:int}/supply-agreements", ListAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.View)
            .WithName("ListSupplyAgreements")
            .WithSummary("The supply agreements of a project with delivered quantities (supplierId, projectItemId, status)");

        projects.MapPost("/{projectId:int}/supply-agreements", CreateAsync)
            .RequireAnyPermission((ProjectPermissions.Module, PermissionAction.Edit), (ProjectPermissions.ProcurementModule, PermissionAction.Edit))
            .WithName("CreateSupplyAgreement")
            .WithSummary("Agree a material with a supplier for a work item of the project");

        projects.MapPut("/{projectId:int}/supply-agreements/{agreementId:int}", UpdateAsync)
            .RequireAnyPermission((ProjectPermissions.Module, PermissionAction.Edit), (ProjectPermissions.ProcurementModule, PermissionAction.Edit))
            .WithName("UpdateSupplyAgreement")
            .WithSummary("Change an agreement's material, quantities, prices, dates or notes (supplier and work item stay)");

        projects.MapPost("/{projectId:int}/supply-agreements/{agreementId:int}/close", CloseAsync)
            .RequireAnyPermission((ProjectPermissions.Module, PermissionAction.Edit), (ProjectPermissions.ProcurementModule, PermissionAction.Edit))
            .WithName("CloseSupplyAgreement")
            .WithSummary("Close an agreement: no new supply can be recorded under it; what was delivered stays");
    }

    internal sealed record ListResponse(IReadOnlyList<SupplyAgreementView> Items);

    internal static async Task<Results<Ok<ListResponse>, ProblemHttpResult>> ListAsync(
        int projectId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        CancellationToken cancellationToken,
        int? supplierId = null,
        int? projectItemId = null,
        string? status = null)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var wantedStatus = Input.TryParseName<SupplyAgreementStatus>(status, out var parsed) ? parsed : (SupplyAgreementStatus?)null;
        var agreements = await db.SupplyAgreements.AsNoTracking()
            .Where(agreement => agreement.ProjectId == projectId)
            .Where(agreement => supplierId == null || agreement.SupplierId == supplierId)
            .Where(agreement => projectItemId == null || agreement.ProjectItemId == projectItemId)
            .Where(agreement => wantedStatus == null || agreement.Status == wantedStatus)
            .OrderBy(agreement => agreement.SupplierId).ThenBy(agreement => agreement.Id)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new ListResponse(await ViewsAsync(db, projectId, agreements, caller.Can(PermissionAction.SeeMoney), cancellationToken)));
    }

    /// <summary>The views of a project's agreements, with their records, suppliers and items in three queries.</summary>
    public static async Task<IReadOnlyList<SupplyAgreementView>> ViewsAsync(
        ProjectsDbContext db, int projectId, IReadOnlyList<SupplyAgreement> agreements, bool seesMoney, CancellationToken cancellationToken)
    {
        if (agreements.Count == 0)
        {
            return [];
        }

        var agreementIds = agreements.Select(agreement => agreement.Id).ToList();
        var records = await SupplierLedger.Records(db, projectId, null, agreementIds).ToListAsync(cancellationToken);
        var supplierIds = agreements.Select(agreement => agreement.SupplierId).Distinct().ToList();
        var suppliers = await db.Suppliers.AsNoTracking().Where(supplier => supplierIds.Contains(supplier.Id)).ToDictionaryAsync(supplier => supplier.Id, cancellationToken);
        var itemIds = agreements.Select(agreement => agreement.ProjectItemId).Distinct().ToList();
        var items = await db.ProjectItems.AsNoTracking().Where(item => itemIds.Contains(item.Id)).ToDictionaryAsync(item => item.Id, cancellationToken);
        var byAgreement = records.ToLookup(record => record.SupplyAgreementId!.Value);

        return agreements
            .Select(agreement => SupplyAgreementView.From(
                agreement, byAgreement[agreement.Id].ToList(), seesMoney, suppliers.GetValueOrDefault(agreement.SupplierId), items.GetValueOrDefault(agreement.ProjectItemId)))
            .ToList();
    }

    internal static async Task<Results<Created<SupplyAgreementView>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        int projectId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = Validate(request, errors);
        if (request.SupplierId is null or < 1)
        {
            errors.Add("supplierId", "supplierId is required.");
        }

        if (request.ProjectItemId is null or < 1)
        {
            errors.Add("projectItemId", "projectItemId is required: an agreement supplies one work item.");
        }

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        if (WriteProblem(httpContext, project) is { } refused)
        {
            return refused;
        }

        var supplier = await db.Suppliers.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == request.SupplierId, cancellationToken);
        if (supplier is null)
        {
            errors.Add("supplierId", "The supplier was not found.");
        }
        else if (supplier.Status != SupplierStatus.Active)
        {
            errors.Add("supplierId", "The supplier is not active.");
        }

        var item = await db.ProjectItems.AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == request.ProjectItemId && candidate.ProjectId == projectId, cancellationToken);
        if (item is null)
        {
            errors.Add("projectItemId", "The work item was not found on this project.");
        }

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (item!.Status == ProjectItemStatus.Inactive)
        {
            return WorkspaceProblems.InactiveItem();
        }

        var agreement = new SupplyAgreement
        {
            ProjectId = projectId,
            SupplierId = supplier!.Id,
            ProjectItemId = item.Id,
            MaterialName = fields!.MaterialName,
            Unit = fields.Unit,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };
        fields.ApplyTo(agreement);
        db.SupplyAgreements.Add(agreement);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, SupplyAgreementConfiguration.UniqueIndex))
        {
            return SupplyProblems.AgreementExists();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsReferenceOrCheck(exception))
        {
            return CoreProblems.ItemRemoved();
        }

        log.SupplyAgreementSaved(caller.UserId, projectId, agreement.Id, supplier.Id, item.Id);
        var view = SupplyAgreementView.From(agreement, [], caller.Can(PermissionAction.SeeMoney), supplier, item);
        return TypedResults.Created($"/api/projects/{projectId}/supply-agreements/{agreement.Id}", view);
    }

    internal static async Task<Results<Ok<SupplyAgreementView>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        int projectId,
        int agreementId,
        Request request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = Validate(request, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        if (WriteProblem(httpContext, project) is { } refused)
        {
            return refused;
        }

        var agreement = await db.SupplyAgreements.SingleOrDefaultAsync(candidate => candidate.Id == agreementId && candidate.ProjectId == projectId, cancellationToken);
        if (agreement is null)
        {
            return SupplyProblems.AgreementNotFound();
        }

        if ((request.SupplierId is { } supplierId && supplierId != agreement.SupplierId)
            || (request.ProjectItemId is { } itemId && itemId != agreement.ProjectItemId))
        {
            return SupplyProblems.FixedParties();
        }

        // Records already under the agreement keep their unit: the unit can't change once something was delivered.
        if (fields!.Unit != agreement.Unit
            && await db.ProjectExpenses.AnyAsync(expense => expense.SupplyAgreementId == agreement.Id && expense.Status != ExpenseStatus.Rejected, cancellationToken))
        {
            return SupplyProblems.UnitInUse();
        }

        agreement.MaterialName = fields.MaterialName;
        agreement.Unit = fields.Unit;
        fields.ApplyTo(agreement);
        agreement.UpdatedAt = clock.UtcNow;
        agreement.UpdatedBy = caller.UserId;
        db.Entry(agreement).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, SupplyAgreementConfiguration.UniqueIndex))
        {
            return SupplyProblems.AgreementExists();
        }

        log.SupplyAgreementSaved(caller.UserId, projectId, agreement.Id, agreement.SupplierId, agreement.ProjectItemId);
        var views = await ViewsAsync(db, projectId, [agreement], caller.Can(PermissionAction.SeeMoney), cancellationToken);
        return TypedResults.Ok(views[0]);
    }

    internal static async Task<Results<Ok<SupplyAgreementView>, ValidationProblem, ProblemHttpResult>> CloseAsync(
        int projectId,
        int agreementId,
        CloseRequest request,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        CompanyClock clock,
        ProjectsLog log,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        if (WriteProblem(httpContext, project) is { } refused)
        {
            return refused;
        }

        var agreement = await db.SupplyAgreements.SingleOrDefaultAsync(candidate => candidate.Id == agreementId && candidate.ProjectId == projectId, cancellationToken);
        if (agreement is null)
        {
            return SupplyProblems.AgreementNotFound();
        }

        if (agreement.Status == SupplyAgreementStatus.Closed)
        {
            return WorkspaceProblems.WrongStatus("The agreement is already closed.");
        }

        agreement.Status = SupplyAgreementStatus.Closed;
        agreement.UpdatedAt = clock.UtcNow;
        agreement.UpdatedBy = caller.UserId;
        db.Entry(agreement).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.SupplyAgreementClosed(caller.UserId, projectId, agreement.Id);
        var views = await ViewsAsync(db, projectId, [agreement], caller.Can(PermissionAction.SeeMoney), cancellationToken);
        return TypedResults.Ok(views[0]);
    }

    /// <summary>
    /// Purchasing (procurement Edit) works every project's suppliers; otherwise only the project's manager or an
    /// Executive changes a project's agreements, like its items. A completed project is read-only.
    /// </summary>
    private static ProblemHttpResult? WriteProblem(HttpContext httpContext, ProjectAccessInfo project)
    {
        if (project.WritesBlocked)
        {
            return ProjectProblems.CompletedProject();
        }

        return httpContext.User.HasPermission(ProjectPermissions.ProcurementModule, PermissionAction.Edit)
            ? null
            : ItemRules.ManageProblem(project);
    }

    private sealed record Fields(
        string MaterialName, string Unit, decimal? AgreedQuantity, decimal? AgreedUnitPrice, decimal? AgreedValue, DateOnly? StartDate, DateOnly? EndDate,
        string? Notes)
    {
        public void ApplyTo(SupplyAgreement agreement)
        {
            agreement.AgreedQuantity = AgreedQuantity;
            agreement.AgreedUnitPrice = AgreedUnitPrice;
            agreement.AgreedValue = AgreedValue;
            agreement.StartDate = StartDate;
            agreement.EndDate = EndDate;
            agreement.Notes = Notes;
        }
    }

    private static Fields? Validate(Request request, ValidationErrors errors)
    {
        var material = Input.RequiredText(request.MaterialName, SupplyAgreementConfiguration.MaterialMaxLength, errors, "materialName", "The material");
        var unit = Input.Clean(request.Unit);
        if (unit is null || !MeasurementUnits.All.Contains(unit))
        {
            errors.Add("unit", $"unit must be one of: {string.Join(", ", MeasurementUnits.All)}.");
        }

        var quantity = request.AgreedQuantity is null ? null : NumberInput.RequiredQuantity(request.AgreedQuantity, errors, "agreedQuantity", "The agreed quantity");
        var unitPrice = NumberInput.OptionalMoney(request.AgreedUnitPrice, errors, "agreedUnitPrice", "The agreed unit price");
        var value = NumberInput.OptionalMoney(request.AgreedValue, errors, "agreedValue", "The agreed value");

        // Quantity × price is the value when both are given and no value was sent; a value that disagrees is refused.
        if (quantity is { } q && unitPrice is { } p)
        {
            var computed = Math.Round(q * p, 2, MidpointRounding.AwayFromZero);
            if (value is { } given && given != computed)
            {
                errors.Add("agreedValue", $"agreedValue must be the agreed quantity × unit price ({computed:0.00}), or left empty.");
            }

            value ??= computed;
        }

        var start = Input.OptionalDate(request.StartDate, errors, "startDate", "The start date");
        var end = Input.OptionalDate(request.EndDate, errors, "endDate", "The end date");
        if (start is { } from && end is { } to && to < from)
        {
            errors.Add("endDate", "The end date can't be before the start date.");
        }

        var notes = Input.OptionalText(request.Notes, SupplyAgreementConfiguration.NotesMaxLength, errors, "notes", "Notes");
        return errors.HasErrors ? null : new Fields(material!, unit!, quantity, unitPrice, value, start, end, notes);
    }
}

/// <summary>Error responses of supply agreements and supplier accounts.</summary>
internal static class SupplyProblems
{
    public const string Exists = "supply_agreement_exists";
    public const string PartiesFixed = "supply_agreement_parties_fixed";
    public const string UnitUsed = "supply_agreement_unit_in_use";

    public static ProblemHttpResult AgreementNotFound() => WorkspaceProblems.NotFound("supply agreement");

    public static ProblemHttpResult AgreementExists() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, Exists, "This supplier already has an agreement for this material on this work item.");

    public static ProblemHttpResult FixedParties() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, PartiesFixed, "An agreement's supplier and work item can't change. Close it and make a new one.");

    public static ProblemHttpResult UnitInUse() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, UnitUsed, "Supplies were recorded under this agreement in its unit: the unit can't change.");
}
