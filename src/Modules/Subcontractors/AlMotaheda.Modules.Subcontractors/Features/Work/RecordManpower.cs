using AlMotaheda.BuildingBlocks.Authorization;
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
/// <c>POST /api/projects/{pid}/subcontracts/{cid}/manpower</c>: record a daily-rate crew over a period. The server
/// computes the amount (workers × days × daily rate; the rate defaults to the contract's). Saved as
/// <c>Recorded</c>, waiting for someone else to approve it.
/// </summary>
/// <remarks>
/// Only on a daily-rate contract that is not completed. The ceiling is checked under the project lock: the sheets
/// that are not rejected, plus this one, must fit in the contract value.
/// </remarks>
internal static class RecordManpower
{
    public const int MaxWorkers = 10_000;

    internal sealed record Request(
        string? FromDate,
        string? ToDate,
        int? Workers,
        decimal? Days,
        decimal? DailyRate,
        string? WorkType,
        int? ProjectItemId,
        string? Note);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/projects/{projectId:int}/subcontracts/{contractId:int}/manpower", HandleAsync)
            .RequirePermission(SubPermissions.Module, PermissionAction.Create)
            .WithName("RecordManpowerSheet")
            .WithSummary("Record a daily-rate manpower sheet (saved as Recorded, waiting for review)");

    internal static async Task<Results<Created<ManpowerView>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        int contractId,
        Request request,
        HttpContext httpContext,
        SubcontractorsDbContext db,
        IProjectAccess access,
        IProjectItems projectItems,
        ContractDetails details,
        CompanyClock clock,
        SubcontractorsLog log,
        CancellationToken cancellationToken)
    {
        if (SubCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var today = clock.Today;
        var fromDate = Input.RequiredDate(request.FromDate, errors, "fromDate", "The start of the period");
        var toDate = Input.RequiredDate(request.ToDate, errors, "toDate", "The end of the period");
        if (toDate > today)
        {
            errors.Add("toDate", "The end of the period can't be in the future.");
        }
        else if (fromDate is { } from && toDate is { } to && to < from)
        {
            errors.Add("toDate", "The end of the period can't be before its start.");
        }

        if (request.Workers is null or < 1 or > MaxWorkers)
        {
            errors.Add("workers", $"workers must be between 1 and {MaxWorkers}.");
        }

        var days = NumberInput.RequiredQuantity(request.Days, errors, "days", "The days");
        var dailyRate = NumberInput.OptionalMoney(request.DailyRate, errors, "dailyRate", "The daily rate");

        if (!WorkTypes.TryParse(request.WorkType, out var workType))
        {
            errors.Add("workType", $"workType must be one of: {string.Join(", ", WorkTypes.All)}.");
        }

        if (request.ProjectItemId is < 1)
        {
            errors.Add("projectItemId", "projectItemId is not valid.");
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
            .Select(candidate => new { candidate.Model, candidate.Status, Value = candidate.Value + (db.ContractVariations.Where(variation => variation.ContractId == candidate.Id && variation.Status == VariationStatus.Approved).Sum(variation => (decimal?)variation.Amount) ?? 0), candidate.DailyRate, candidate.ProjectItemId })
            .SingleOrDefaultAsync(cancellationToken);
        if (contract is null)
        {
            return SubcontractorsProblems.ContractNotFound();
        }

        if (contract.Status == ContractStatus.Completed)
        {
            return SubcontractorsProblems.CompletedContract();
        }

        if (contract.Model != ContractModel.DailyRate || contract.DailyRate is not { } contractRate)
        {
            return SubcontractorsProblems.ModelDoesNotFit("Manpower sheets are only for daily-rate contracts. Record measured work instead.");
        }

        if (request.ProjectItemId is { } projectItemId)
        {
            switch (await ProjectItemLinks.CheckAsync(projectItems, projectId, projectItemId, cancellationToken, contract.ProjectItemId))
            {
                case ProjectItemCheck.NotFound:
                    errors.Add("projectItemId", "The project item was not found on this project.");
                    return SubcontractorsProblems.Validation(errors);
                case ProjectItemCheck.Inactive:
                    return SubcontractorsProblems.InactiveItem();
                case ProjectItemCheck.OutsideScope:
                    return SubcontractorsProblems.OutsideContractScope();
            }
        }

        var rate = dailyRate ?? contractRate;
        var workers = request.Workers!.Value;
        var amount = WorkRules.ManpowerAmount(workers, days!.Value, rate);
        if (amount <= 0)
        {
            errors.Add("days", "This is too small to be worth anything at this daily rate.");
            return SubcontractorsProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await AppLock.TryAcquireAsync(db, AppLock.Project(projectId), cancellationToken))
        {
            return SubcontractorsProblems.TryAgainLater();
        }

        var recorded = await db.ManpowerSheets
            .Where(sheet => sheet.ContractId == contractId && sheet.Status != WorkStatus.Rejected)
            .SumAsync(sheet => sheet.Amount, cancellationToken);

        if (!WorkRules.FitsCeiling(contract.Value, recorded, amount))
        {
            return SubcontractorsProblems.OverCeiling(Math.Max(0, contract.Value - recorded));
        }

        var now = clock.UtcNow;
        var sheet = new ManpowerSheet
        {
            ContractId = contractId,
            ProjectItemId = request.ProjectItemId,
            FromDate = fromDate!.Value,
            ToDate = toDate!.Value,
            Workers = workers,
            Days = days.Value,
            DailyRate = rate,
            Amount = amount,
            WorkType = workType,
            Note = note,
            Status = WorkStatus.Recorded,
            CreatedAt = now,
            CreatedBy = caller.UserId,
        };
        db.ManpowerSheets.Add(sheet);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (sheet.ProjectItemId is not null && SqlViolations.IsReferenceOrCheck(exception))
        {
            // The project item was deleted by someone else after our check: a lost race is a 409, never a 500.
            return SubcontractorsProblems.ItemRemoved();
        }
        await transaction.CommitAsync(cancellationToken);

        log.ManpowerRecorded(caller.UserId, projectId, contractId, sheet.Id);

        var view = await details.ManpowerAsync(projectId, sheet.Id, caller.SeesMoney, cancellationToken);
        return TypedResults.Created($"/api/projects/{projectId}/subcontract-manpower/{sheet.Id}", view!);
    }
}
