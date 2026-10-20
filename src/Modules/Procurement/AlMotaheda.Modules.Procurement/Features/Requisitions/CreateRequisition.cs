using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Procurement.Common;
using AlMotaheda.Modules.Procurement.Data;
using AlMotaheda.Modules.Procurement.Data.Configurations;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Procurement.Features.Requisitions;

/// <summary>
/// <c>POST /api/procurement/requisitions</c> (procurement Create, a project the caller can see): what the project
/// needs. A line with a catalog item takes its name, unit and today's approved price (the estimate) from the
/// project's catalog. Then the approval step runs: under the auto-approval policy the system approves it at once and
/// says so in the history; otherwise it waits for the project manager, then an Accountant.
/// </summary>
internal static class CreateRequisition
{
    public const int MaxLines = 50;

    /// <param name="PriceItemId">The Price Control item the line is bought as (optional): orders and invoices are compared with its range.</param>
    internal sealed record LineRequest(int? CatalogItemId, int? ProjectItemId, string? Description, string? Unit, decimal? Quantity, int? PriceItemId = null);

    internal sealed record Request(int? ProjectId, string? NeededBy, string? Notes, IReadOnlyList<LineRequest>? Lines);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/procurement/requisitions", HandleAsync)
            .RequirePermission(ProcurementPermissions.Module, PermissionAction.Create)
            .WithName("CreateRequisition")
            .WithSummary("Create a purchase requisition; approved at once under the auto-approval policy");

    internal static async Task<Results<Created<RequisitionView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        Request request,
        HttpContext httpContext,
        ProcurementDbContext db,
        IProjectAccess access,
        IProjectCatalog catalog,
        IProjectItems projectItems,
        IPriceControl priceControl,
        ProcurementViews views,
        CompanyClock clock,
        ProcurementLog log,
        CancellationToken cancellationToken)
    {
        if (ProcurementCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        if (request.ProjectId is not > 0)
        {
            errors.Add("projectId", "The project is required.");
        }

        var neededBy = Input.OptionalDate(request.NeededBy, errors, "neededBy", "The date needed");
        var notes = Input.OptionalText(request.Notes, ProcurementColumns.NoteMaxLength, errors, "notes", "Notes");
        var lines = request.Lines ?? [];
        if (lines.Count is 0 or > MaxLines)
        {
            errors.Add("lines", $"A requisition has 1 to {MaxLines} lines.");
        }

        for (var index = 0; index < lines.Count && index < MaxLines; index++)
        {
            var line = lines[index];
            NumberInput.RequiredQuantity(line.Quantity, errors, $"lines[{index}].quantity", "The quantity");
            if (line.CatalogItemId is null)
            {
                Input.RequiredText(line.Description, ProcurementColumns.DescriptionMaxLength, errors, $"lines[{index}].description", "The description");
                if (!MeasurementUnits.TryParse(line.Unit, out _))
                {
                    errors.Add($"lines[{index}].unit", $"unit must be one of: {string.Join(", ", MeasurementUnits.All)}.");
                }
            }
        }

        if (errors.HasErrors)
        {
            return ProcurementProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, request.ProjectId!.Value, cancellationToken);
        if (project is null)
        {
            return ProcurementProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return ProcurementProblems.CompletedProject();
        }

        var today = clock.Today;
        var catalogItems = await catalog.FindAsync(project.ProjectId, lines.Where(line => line.CatalogItemId is not null).Select(line => line.CatalogItemId!.Value).Distinct().ToList(), today, cancellationToken);
        var items = await projectItems.FindAsync(project.ProjectId, lines.Where(line => line.ProjectItemId is not null).Select(line => line.ProjectItemId!.Value).Distinct().ToList(), cancellationToken);
        var priceItems = await priceControl.FindItemsAsync(lines.Where(line => line.PriceItemId is not null).Select(line => line.PriceItemId!.Value).Distinct().ToList(), cancellationToken);

        var requisition = new Requisition { ProjectId = project.ProjectId, NeededBy = neededBy, Notes = notes, CreatedAt = clock.UtcNow, CreatedBy = caller.UserId };
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            CatalogItemRef? item = null;
            if (line.CatalogItemId is { } catalogItemId && (!catalogItems.TryGetValue(catalogItemId, out item) || !item.IsActive))
            {
                errors.Add($"lines[{index}].catalogItemId", "The catalog item isn't an active item of this project.");
            }

            if (line.ProjectItemId is { } itemId && (!items.TryGetValue(itemId, out var costItem) || !costItem.IsActive))
            {
                errors.Add($"lines[{index}].projectItemId", "The cost item isn't an active item of this project.");
            }

            MeasurementUnits.TryParse(line.Unit, out var unit);
            var lineUnit = item?.Unit ?? unit;

            // A Price Control item must be active and priced per the line's unit, or the invoice couldn't be compared.
            PriceItemRef? priced = null;
            if (line.PriceItemId is { } priceItemId && (!priceItems.TryGetValue(priceItemId, out priced) || !priced.IsActive))
            {
                errors.Add($"lines[{index}].priceItemId", "The price list item isn't an active item of Price Control.");
            }
            else if (priced is not null && !string.IsNullOrEmpty(lineUnit) && !string.Equals(priced.Unit, lineUnit, StringComparison.Ordinal))
            {
                errors.Add($"lines[{index}].priceItemId", $"The price list item is priced per {priced.Unit}: order it in {priced.Unit}.");
            }

            requisition.Lines.Add(new RequisitionLine
            {
                CatalogItemId = line.CatalogItemId,
                PriceItemId = line.PriceItemId,
                ProjectItemId = line.ProjectItemId,
                Description = Input.Clean(line.Description) ?? item?.NameEn ?? priced?.NameEn ?? "",
                Unit = string.IsNullOrEmpty(lineUnit) ? priced?.Unit ?? "" : lineUnit,
                Quantity = line.Quantity!.Value,
                EstimatedUnitPrice = item?.CurrentPrice,
            });
        }

        if (errors.HasErrors)
        {
            return ProcurementProblems.Validation(errors);
        }

        var policy = await db.Policies.AsNoTracking().SingleAsync(cancellationToken);
        var now = clock.UtcNow;
        requisition.Record("Submit", caller.UserId, null, null, now, null);
        if (policy.AutoApproveRequisitions)
        {
            requisition.ApproveByPolicy(now);
        }

        db.Requisitions.Add(requisition);
        await db.SaveChangesAsync(cancellationToken);
        log.RequisitionCreated(caller.UserId, project.ProjectId, requisition.Id, policy.AutoApproveRequisitions);

        var view = await views.RequisitionAsync(requisition.Id, caller.SeesMoney, cancellationToken);
        return TypedResults.Created($"/api/procurement/requisitions/{requisition.Id}", view!);
    }
}
