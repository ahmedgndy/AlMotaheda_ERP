using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Security;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Features.Costs;

/// <summary>
/// <c>GET /api/projects/{pid}/equipment-summary</c>: the project's equipment cost per machine (rental, fuel,
/// maintenance with repair, other, total, this month, litres) and in total, plus what waits for review. Money only,
/// so it needs equipment See money.
/// </summary>
internal static class GetEquipmentSummary
{
    internal sealed record MachineView(
        int EquipmentId,
        string AssetNo,
        string NameAr,
        string NameEn,
        string Ownership,
        decimal Rental,
        decimal Fuel,
        decimal Maintenance,
        decimal Other,
        decimal Total,
        decimal ThisMonth,
        decimal Liters,
        decimal Paid,
        decimal Outstanding);

    internal sealed record BreakdownView(decimal Rental, decimal Fuel, decimal Maintenance, decimal Other, decimal Total)
    {
        public static BreakdownView From(EquipmentCostBreakdown breakdown) =>
            new(breakdown.Rental, breakdown.Fuel, breakdown.Maintenance, breakdown.Other, breakdown.Total);
    }

    /// <param name="ToDate">Approved costs, all dates.</param>
    /// <param name="ThisMonth">Approved costs dated from <paramref name="MonthStart"/> on.</param>
    internal sealed record Response(
        int ProjectId,
        DateOnly MonthStart,
        IReadOnlyList<MachineView> Machines,
        BreakdownView ToDate,
        BreakdownView ThisMonth,
        decimal Liters,
        decimal Paid,
        decimal Outstanding,
        decimal PendingReviewAmount,
        int PendingReviewCount,
        int ReturnedCount);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/projects/{projectId:int}/equipment-summary", HandleAsync)
            .RequirePermission(FleetPermissions.Module, PermissionAction.SeeMoney)
            .WithName("GetEquipmentSummary")
            .WithSummary("A project's approved equipment cost per machine and in total, this month, litres, and what waits for review");

    internal static async Task<Results<Ok<Response>, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        FleetDbContext db,
        IProjectAccess access,
        CompanyClock clock,
        CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return FleetProblems.ProjectNotFound();
        }

        var monthStart = EquipmentSummary.MonthStart(clock.Today);
        var rows = await EquipmentCostQueries.SettlementRows(db, projectId).ToListAsync(cancellationToken);
        var figures = EquipmentSummary.Compute(rows, monthStart);

        var ids = figures.Machines.Select(machine => machine.EquipmentId).ToList();
        var names = ids.Count == 0
            ? new Dictionary<int, EquipmentNameRow>()
            : await EquipmentCostQueries.Names(db, ids).ToDictionaryAsync(name => name.Id, cancellationToken);

        var machines = figures.Machines
            .Select(machine =>
            {
                names.TryGetValue(machine.EquipmentId, out var name);
                return new MachineView(
                    machine.EquipmentId,
                    name?.AssetNo ?? $"#{machine.EquipmentId}",
                    name?.NameAr ?? "",
                    name?.NameEn ?? "",
                    name?.Ownership.ToString() ?? "",
                    machine.ToDate.Rental,
                    machine.ToDate.Fuel,
                    machine.ToDate.Maintenance,
                    machine.ToDate.Other,
                    machine.ToDate.Total,
                    machine.ThisMonth,
                    machine.Liters,
                    machine.Paid,
                    machine.Outstanding);
            })
            .ToList();

        return TypedResults.Ok(new Response(
            projectId,
            monthStart,
            machines,
            BreakdownView.From(figures.ToDate),
            BreakdownView.From(figures.ThisMonth),
            figures.Liters,
            figures.Paid,
            figures.Outstanding,
            figures.PendingReviewAmount,
            figures.PendingReviewCount,
            figures.ReturnedCount));
    }
}
