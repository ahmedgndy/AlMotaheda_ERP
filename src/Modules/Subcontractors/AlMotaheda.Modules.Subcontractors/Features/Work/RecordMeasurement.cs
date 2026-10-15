using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Subcontractors.Common;
using AlMotaheda.Modules.Subcontractors.Data;
using AlMotaheda.Modules.Subcontractors.Data.Configurations;
using AlMotaheda.Modules.Subcontractors.Features.ContractItems;
using AlMotaheda.Modules.Subcontractors.Features.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Subcontractors.Features.Work;

/// <summary>
/// <c>POST /api/projects/{pid}/subcontracts/{cid}/measurements</c>: record completed work measured on site for one
/// contract item. Saved as <c>Recorded</c>: it is cost only once someone else approves it (decision S1).
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Needs subcontractors Create or Edit (checked here, because <c>RequirePermission</c> asks for one action).</item>
///   <item>A unit-rate or lump-sum contract that is not completed. A Job or Lot line may be measured as
///   <c>percentOfLine</c> instead of a quantity.</item>
///   <item>The unit price is copied from the item now, so a later price change never changes this measurement.</item>
///   <item>Over-quantity is blocked under the project lock: what is measured and not rejected, plus this, must fit
///   in the item quantity.</item>
/// </list>
/// </remarks>
internal static class RecordMeasurement
{
    internal sealed record Request(int? ContractItemId, string? MeasuredOn, decimal? Quantity, decimal? PercentOfLine, string? Note);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/subcontracts/{contractId:int}/measurements", HandleAsync)
            .WithName("RecordMeasurement")
            .WithSummary("Record measured work on a contract item (subcontractors Create or Edit; saved as Recorded, waiting for review)");

    internal static async Task<Results<Created<MeasurementView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int contractId,
        Request request,
        HttpContext httpContext,
        SubcontractorsDbContext db,
        IProjectAccess access,
        ContractDetails details,
        CompanyClock clock,
        SubcontractorsLog log,
        CancellationToken cancellationToken)
    {
        if (SubCaller.From(httpContext.User) is not { } caller || !ItemPermissions.CanWrite(caller))
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        if (request.ContractItemId is null or < 1)
        {
            errors.Add("contractItemId", "contractItemId is required.");
        }

        var measuredOn = Input.RequiredDate(request.MeasuredOn, errors, "measuredOn", "The measurement date");
        if (measuredOn > clock.Today)
        {
            errors.Add("measuredOn", "The measurement date can't be in the future.");
        }

        decimal? quantity = null, percent = null;
        if (request.Quantity is not null && request.PercentOfLine is not null)
        {
            errors.Add("quantity", "Send either quantity or percentOfLine, not both.");
        }
        else if (request.PercentOfLine is { } percentOfLine)
        {
            if (percentOfLine is <= 0 or > 100 || decimal.Round(percentOfLine, 2) != percentOfLine)
            {
                errors.Add("percentOfLine", "percentOfLine must be above 0 and at most 100, with at most two decimals.");
            }
            else
            {
                percent = percentOfLine;
            }
        }
        else
        {
            quantity = NumberInput.RequiredQuantity(request.Quantity, errors, "quantity", "The quantity");
        }

        var note = Input.OptionalText(request.Note, WorkReview.NoteMaxLength, errors, "note", "The note");
        if (errors.HasErrors)
        {
            return SubcontractorsProblems.Validation(errors);
        }

        var project = await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken);
        if (project is null)
        {
            return SubcontractorsProblems.ProjectNotFound();
        }

        if (project.WritesBlocked)
        {
            return SubcontractorsProblems.CompletedProject();
        }

        var contract = await db.SubContracts.AsNoTracking()
            .Where(candidate => candidate.Id == contractId && candidate.ProjectId == projectId)
            .Select(candidate => new { candidate.Model, candidate.Status })
            .SingleOrDefaultAsync(cancellationToken);
        if (contract is null)
        {
            return SubcontractorsProblems.ContractNotFound();
        }

        if (contract.Status == ContractStatus.Completed)
        {
            return SubcontractorsProblems.CompletedContract();
        }

        if (contract.Model == ContractModel.DailyRate)
        {
            return SubcontractorsProblems.ModelDoesNotFit("A daily-rate contract records manpower sheets, not measurements.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await AppLock.TryAcquireAsync(db, AppLock.Project(projectId), cancellationToken))
        {
            return SubcontractorsProblems.TryAgainLater();
        }

        var contractItemId = request.ContractItemId!.Value;
        var item = await db.ContractItems.AsNoTracking()
            .Where(candidate => candidate.Id == contractItemId && candidate.ContractId == contractId)
            .Select(candidate => new { candidate.Unit, candidate.Quantity, candidate.UnitPrice })
            .SingleOrDefaultAsync(cancellationToken);
        if (item is null)
        {
            errors.Add("contractItemId", "The contract item was not found on this contract.");
            return SubcontractorsProblems.Validation(errors);
        }

        if (percent is { } share)
        {
            if (!MeasurementUnits.IsLumpSum(item.Unit))
            {
                errors.Add("percentOfLine", "percentOfLine is only for items measured by Job or Lot. Send a quantity.");
                return SubcontractorsProblems.Validation(errors);
            }

            quantity = WorkRules.QuantityFromPercent(share, item.Quantity);
        }

        var measuredQuantity = quantity!.Value;
        var amount = WorkRules.MeasurementAmount(measuredQuantity, item.UnitPrice);
        if (measuredQuantity <= 0 || amount <= 0)
        {
            errors.Add(percent is null ? "quantity" : "percentOfLine", "This is too small to be worth anything at the item's unit price.");
            return SubcontractorsProblems.Validation(errors);
        }

        // quantity × unit price can exceed what an amount column holds; refuse it as a validation error, never a 500.
        if (amount > NumberInput.MaxMoney)
        {
            errors.Add(percent is null ? "quantity" : "percentOfLine", $"The measured amount must be at most {NumberInput.MaxMoney:0.00}.");
            return SubcontractorsProblems.Validation(errors);
        }

        var measured = await db.ProgressMeasurements
            .Where(measurement => measurement.ContractItemId == contractItemId && measurement.Status != WorkStatus.Rejected)
            .SumAsync(measurement => measurement.Quantity, cancellationToken);

        if (!WorkRules.FitsItemQuantity(item.Quantity, measured, measuredQuantity))
        {
            return SubcontractorsProblems.OverQuantity(WorkRules.RemainingQuantity(item.Quantity, measured));
        }

        var measurement = new ProgressMeasurement
        {
            ContractItemId = contractItemId,
            MeasuredOn = measuredOn!.Value,
            Quantity = measuredQuantity,
            UnitPrice = item.UnitPrice,
            Amount = amount,
            Status = WorkStatus.Recorded,
            Note = note,
            CreatedAt = clock.UtcNow,
            CreatedBy = caller.UserId,
        };
        db.ProgressMeasurements.Add(measurement);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        log.MeasurementRecorded(caller.UserId, projectId, contractId, measurement.Id);

        var view = await details.MeasurementAsync(projectId, measurement.Id, caller.SeesMoney, cancellationToken);
        return TypedResults.Created($"/api/projects/{projectId}/subcontract-measurements/{measurement.Id}", view!);
    }
}
