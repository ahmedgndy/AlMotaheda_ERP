using AlMotaheda.Modules.Fleet.Data;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Features.Vehicles;

/// <summary>The figures of one vehicle's operating custody, from its append-only entries. Never stored.</summary>
/// <param name="Posted">Fundings − returns − posted expenses ± adjustments: what the driver should hold.</param>
/// <param name="Pending">Claims submitted and not reviewed: the exposure, not yet in <see cref="Posted"/>.</param>
internal sealed record VehicleCustodyFigures(decimal Funded, decimal Returned, decimal SpentPosted, decimal Pending, decimal Adjusted, decimal Posted, int PendingCount)
{
    public static VehicleCustodyFigures Empty { get; } = new(0, 0, 0, 0, 0, 0, 0);
}

/// <summary>The custody arithmetic, with no database access: one rule for the statement, the claim checks, the reports and Finance's figures.</summary>
internal static class VehicleCustodyBalance
{
    public static VehicleCustodyFigures Compute(IEnumerable<VehicleCustodyEntry> entries)
    {
        decimal funded = 0, returned = 0, spent = 0, pending = 0, adjusted = 0;
        var pendingCount = 0;
        foreach (var entry in entries)
        {
            if (entry.Status == VehicleCustodyStatus.Released)
            {
                continue;
            }

            switch (entry.Kind)
            {
                case VehicleCustodyKind.Funding:
                    funded += entry.Amount;
                    break;
                case VehicleCustodyKind.Return:
                    returned += entry.Amount;
                    break;
                case VehicleCustodyKind.AdjustmentIn:
                    adjusted += entry.Amount;
                    break;
                case VehicleCustodyKind.AdjustmentOut:
                    adjusted -= entry.Amount;
                    break;
                case VehicleCustodyKind.Expense:
                    if (entry.Status == VehicleCustodyStatus.Pending)
                    {
                        pending += entry.Amount;
                        pendingCount++;
                    }
                    else
                    {
                        spent += entry.Amount;
                    }

                    break;
            }
        }

        return new VehicleCustodyFigures(funded, returned, spent, pending, adjusted, funded - returned - spent + adjusted, pendingCount);
    }

    /// <summary>Cash in is positive for the running statement.</summary>
    public static decimal Signed(VehicleCustodyEntry entry) =>
        entry.Kind is VehicleCustodyKind.Funding or VehicleCustodyKind.AdjustmentIn ? entry.Amount : -entry.Amount;
}

/// <summary>The vehicle queries, as <see cref="IQueryable{T}"/> so tests can prove they translate.</summary>
internal static class VehicleQueries
{
    public static IQueryable<VehicleCustodyEntry> CustodyEntries(FleetDbContext db, int vehicleId) =>
        db.VehicleCustodyEntries.AsNoTracking().Where(entry => entry.VehicleId == vehicleId);

    public static async Task<VehicleCustodyFigures> FiguresAsync(FleetDbContext db, int vehicleId, CancellationToken cancellationToken) =>
        VehicleCustodyBalance.Compute(await CustodyEntries(db, vehicleId).ToListAsync(cancellationToken));

    /// <summary>The driver assignment covering <paramref name="day"/>, or <c>null</c> when nobody drove the vehicle that day.</summary>
    public static IQueryable<VehicleDriverAssignment> DriverOn(FleetDbContext db, int vehicleId, DateOnly day) =>
        db.VehicleDriverAssignments.AsNoTracking()
            .Where(assignment => assignment.VehicleId == vehicleId && assignment.StartDate <= day && (assignment.EndDate == null || assignment.EndDate >= day));

    public static IQueryable<VehicleDriverAssignment> CurrentDriver(FleetDbContext db, int vehicleId) =>
        db.VehicleDriverAssignments.AsNoTracking().Where(assignment => assignment.VehicleId == vehicleId && assignment.EndDate == null);

    /// <summary>The project the vehicle (an equipment row) was on at <paramref name="day"/>, if any.</summary>
    public static IQueryable<int> ProjectOn(FleetDbContext db, int vehicleId, DateOnly day) =>
        db.EquipmentAssignments.AsNoTracking()
            .Where(stay => stay.EquipmentId == vehicleId && stay.StartDate <= day && (stay.EndDate == null || stay.EndDate >= day))
            .Select(stay => stay.ProjectId);

    /// <summary>The highest odometer reading trusted so far before <paramref name="beforeId"/>: the vehicle's reading and approved claims.</summary>
    public static async Task<(decimal Km, DateOnly? On)> LastTrustedOdometerAsync(FleetDbContext db, int vehicleId, CancellationToken cancellationToken)
    {
        var vehicle = await db.Vehicles.AsNoTracking().Where(candidate => candidate.Id == vehicleId).Select(candidate => new { candidate.OdometerKm, candidate.OdometerUpdatedAt }).SingleAsync(cancellationToken);
        var lastApproved = await db.VehicleExpenses.AsNoTracking()
            .Where(expense => expense.VehicleId == vehicleId && expense.Status == VehicleExpenseStatus.Approved)
            .OrderByDescending(expense => expense.OdometerKm)
            .Select(expense => new { expense.OdometerKm, expense.ExpenseDate })
            .FirstOrDefaultAsync(cancellationToken);
        return lastApproved is not null && lastApproved.OdometerKm >= vehicle.OdometerKm
            ? (lastApproved.OdometerKm, lastApproved.ExpenseDate)
            : (vehicle.OdometerKm, vehicle.OdometerUpdatedAt is { } at ? DateOnly.FromDateTime(at) : null);
    }
}
