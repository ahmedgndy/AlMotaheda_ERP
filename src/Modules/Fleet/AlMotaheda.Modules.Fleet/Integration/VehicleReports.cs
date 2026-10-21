using System.Security.Claims;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features.Vehicles;
using AlMotaheda.Modules.HR.Contracts;
using AlMotaheda.Modules.Reports.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Integration;

/// <summary>
/// The vehicle reports, from the same append-only entries and the same arithmetic as the vehicle screens
/// (<see cref="VehicleCustodyBalance"/>): funds by vehicle, vehicle operating expenses, pending and rejected claims.
/// Only posted claims are expenses; a funding is never one.
/// </summary>
internal sealed class VehicleReports(FleetDbContext db, IEmployeeDirectory employees) : IReportSource
{
    public const string Custody = "FLT-VEHICLE-CUSTODY";
    public const string Expenses = "FLT-VEHICLE-EXPENSES";
    public const string Claims = "FLT-CLAIMS";

    private static readonly ReportPermission[] FleetMoney = [new("fleet", ReportActions.SeeMoney), new("fuel", ReportActions.SeeMoney)];

    private static readonly ReportFilter From = new("from", ReportFilterTypes.Date, true, "من تاريخ", "From");
    private static readonly ReportFilter To = new("to", ReportFilterTypes.Date, true, "إلى تاريخ", "To");
    private static readonly ReportFilter VehicleFilter = new("vehicleId", ReportFilterTypes.Id, false, "المركبة", "Vehicle");

    public IReadOnlyList<ReportDefinition> Reports { get; } =
    [
        new(Custody, "عهد المركبات (الأموال حسب المركبة)", "Funds by vehicle", ReportCategories.Equipment, FleetMoney, [VehicleFilter],
            [
                new("plate", "اللوحة", "Plate", ReportColumnTypes.Text),
                new("vehicle", "المركبة", "Vehicle", ReportColumnTypes.Text),
                new("driver", "السائق الحالي", "Current driver", ReportColumnTypes.Text),
                new("funded", "ممّول", "Funded", ReportColumnTypes.Money, Total: true),
                new("returned", "مرتجع", "Returned", ReportColumnTypes.Money, Total: true),
                new("spent", "مصروف (معتمد)", "Spent (posted)", ReportColumnTypes.Money, Total: true),
                new("adjusted", "تسويات", "Adjustments", ReportColumnTypes.Money, Total: true),
                new("posted", "الرصيد", "Balance", ReportColumnTypes.Money, Total: true),
                new("pending", "مطالبات معلّقة", "Pending claims", ReportColumnTypes.Money, Total: true),
                new("pendingCount", "عدد المعلّق", "Pending", ReportColumnTypes.Integer, Total: true),
            ]),
        new(Expenses, "مصروفات تشغيل المركبات", "Vehicle operating expenses", ReportCategories.Equipment, FleetMoney, [From, To, VehicleFilter],
            [
                new("plate", "اللوحة", "Plate", ReportColumnTypes.Text),
                new("vehicle", "المركبة", "Vehicle", ReportColumnTypes.Text),
                new("fuel", "وقود", "Fuel", ReportColumnTypes.Money, Total: true),
                new("liters", "اللترات", "Litres", ReportColumnTypes.Quantity, Total: true),
                new("maintenance", "صيانة وإصلاح", "Maintenance and repair", ReportColumnTypes.Money, Total: true),
                new("other", "أخرى", "Other", ReportColumnTypes.Money, Total: true),
                new("total", "الإجمالي", "Total", ReportColumnTypes.Money, Total: true),
                new("claims", "عدد المطالبات", "Claims", ReportColumnTypes.Integer, Total: true),
                new("distanceKm", "المسافة (كم)", "Distance (km)", ReportColumnTypes.Quantity),
            ]),
        new(Claims, "مطالبات المركبات المعلّقة والمرفوضة", "Pending and rejected vehicle claims", ReportCategories.Equipment, FleetMoney,
            [From, To, VehicleFilter, new("status", ReportFilterTypes.Option, false, "الحالة", "Status", Enum.GetNames<VehicleExpenseStatus>())],
            [
                new("number", "رقم المطالبة", "Claim", ReportColumnTypes.Text),
                new("date", "التاريخ", "Date", ReportColumnTypes.Date),
                new("plate", "اللوحة", "Plate", ReportColumnTypes.Text),
                new("driver", "السائق", "Driver", ReportColumnTypes.Text),
                new("type", "النوع", "Type", ReportColumnTypes.Text),
                new("amount", "المبلغ", "Amount", ReportColumnTypes.Money, Total: true),
                new("status", "الحالة", "Status", ReportColumnTypes.Text),
                new("flags", "تنبيهات", "Flags", ReportColumnTypes.Integer, Total: true),
                new("reviewNote", "ملاحظة المراجع", "Review note", ReportColumnTypes.Text),
                new("hrReferral", "إحالة للموارد البشرية", "HR referral", ReportColumnTypes.Integer),
            ]),
    ];

    public async Task<ReportData?> RunAsync(string code, ReportQuery query, ClaimsPrincipal caller, CancellationToken cancellationToken) => code switch
    {
        Custody => await CustodyAsync(query, cancellationToken),
        Expenses => await ExpensesAsync(query, cancellationToken),
        Claims => await ClaimsAsync(query, cancellationToken),
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "Not a vehicle report."),
    };

    private async Task<ReportData> CustodyAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var vehicleId = query.Id("vehicleId");
        var vehicles = await db.Vehicles.AsNoTracking().Include(vehicle => vehicle.Equipment).Where(vehicle => vehicleId == null || vehicle.Id == vehicleId).OrderBy(vehicle => vehicle.Plate).ToListAsync(cancellationToken);
        var ids = vehicles.Select(vehicle => vehicle.Id).ToList();
        var entries = await db.VehicleCustodyEntries.AsNoTracking().Where(entry => ids.Contains(entry.VehicleId)).ToListAsync(cancellationToken);
        var drivers = await db.VehicleDriverAssignments.AsNoTracking().Where(assignment => ids.Contains(assignment.VehicleId) && assignment.EndDate == null).ToListAsync(cancellationToken);
        var names = await NamesAsync(drivers.Select(driver => driver.DriverEmployeeId), cancellationToken);

        return new ReportData(vehicles.Select(vehicle =>
        {
            var figures = VehicleCustodyBalance.Compute(entries.Where(entry => entry.VehicleId == vehicle.Id));
            var driver = drivers.SingleOrDefault(candidate => candidate.VehicleId == vehicle.Id);
            return new ReportRow(new Dictionary<string, object?>
            {
                ["plate"] = vehicle.Plate,
                ["vehicle"] = vehicle.Equipment.NameEn,
                ["driver"] = driver is null ? null : names.GetValueOrDefault(driver.DriverEmployeeId),
                ["funded"] = figures.Funded,
                ["returned"] = figures.Returned,
                ["spent"] = figures.SpentPosted,
                ["adjusted"] = figures.Adjusted,
                ["posted"] = figures.Posted,
                ["pending"] = figures.Pending,
                ["pendingCount"] = figures.PendingCount,
            }, new ReportLink("vehicle", vehicle.Id));
        }).ToList(), "Balance = fundings − returns − posted claims ± adjustments. Pending claims are the exposure, not yet in the balance.");
    }

    private async Task<ReportData> ExpensesAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var from = query.Date("from")!.Value;
        var to = query.Date("to")!.Value;
        var vehicleId = query.Id("vehicleId");
        var claims = await db.VehicleExpenses.AsNoTracking().Include(claim => claim.Vehicle).ThenInclude(vehicle => vehicle.Equipment)
            .Where(claim => claim.Status == VehicleExpenseStatus.Approved && claim.PostingStatus == VehiclePostingStatus.Posted && claim.ExpenseDate >= from && claim.ExpenseDate <= to
                            && (vehicleId == null || claim.VehicleId == vehicleId))
            .ToListAsync(cancellationToken);

        return new ReportData(claims.GroupBy(claim => claim.VehicleId).OrderBy(group => group.First().Vehicle.Plate).Select(group =>
        {
            decimal Of(params VehicleExpenseType[] types) => group.Where(claim => types.Contains(claim.Type)).Sum(claim => claim.Amount);
            var readings = group.Select(claim => claim.OdometerKm).ToList();
            return new ReportRow(new Dictionary<string, object?>
            {
                ["plate"] = group.First().Vehicle.Plate,
                ["vehicle"] = group.First().Vehicle.Equipment.NameEn,
                ["fuel"] = Of(VehicleExpenseType.Fuel),
                ["liters"] = group.Where(claim => claim.Type == VehicleExpenseType.Fuel).Sum(claim => claim.FuelQuantity ?? 0),
                ["maintenance"] = Of(VehicleExpenseType.Maintenance, VehicleExpenseType.Repair),
                ["other"] = Of(VehicleExpenseType.Toll, VehicleExpenseType.Parking, VehicleExpenseType.Wash, VehicleExpenseType.Other),
                ["total"] = group.Sum(claim => claim.Amount),
                ["claims"] = group.Count(),
                ["distanceKm"] = readings.Count > 1 ? readings.Max() - readings.Min() : 0m,
            }, new ReportLink("vehicle", group.Key));
        }).ToList(), "Only approved, posted claims are expenses; a custody funding is never one. Distance is between the lowest and highest odometer readings of the period's claims.");
    }

    private async Task<ReportData> ClaimsAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var from = query.Date("from")!.Value;
        var to = query.Date("to")!.Value;
        var vehicleId = query.Id("vehicleId");
        var list = db.VehicleExpenses.AsNoTracking().Include(claim => claim.Vehicle).Include(claim => claim.Flags)
            .Where(claim => claim.ExpenseDate >= from && claim.ExpenseDate <= to && (vehicleId == null || claim.VehicleId == vehicleId));
        if (query.Text("status") is { } status)
        {
            var wanted = Enum.Parse<VehicleExpenseStatus>(status);
            list = list.Where(claim => claim.Status == wanted);
        }
        else
        {
            list = list.Where(claim => claim.Status != VehicleExpenseStatus.Approved);
        }

        var claims = await list.OrderBy(claim => claim.ExpenseDate).ThenBy(claim => claim.Id).ToListAsync(cancellationToken);
        var names = await NamesAsync(claims.Select(claim => claim.DriverEmployeeId), cancellationToken);
        return new ReportData(claims.Select(claim => new ReportRow(new Dictionary<string, object?>
        {
            ["number"] = claim.Number,
            ["date"] = claim.ExpenseDate,
            ["plate"] = claim.Vehicle.Plate,
            ["driver"] = names.GetValueOrDefault(claim.DriverEmployeeId),
            ["type"] = claim.Type.ToString(),
            ["amount"] = claim.Amount,
            ["status"] = claim.Status.ToString(),
            ["flags"] = claim.Flags.Count,
            ["reviewNote"] = claim.ReviewNote,
            ["hrReferral"] = claim.HrReferralId,
        }, new ReportLink("vehicleExpense", claim.Id))).ToList(), "Without a status filter: every claim not approved (submitted, escalated, waiting for evidence, rejected).");
    }

    private async Task<Dictionary<int, string>> NamesAsync(IEnumerable<int> employeeIds, CancellationToken cancellationToken)
    {
        var ids = employeeIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        return (await employees.FindAsync(ids, cancellationToken)).ToDictionary(pair => pair.Key, pair => pair.Value.NameEn);
    }
}
