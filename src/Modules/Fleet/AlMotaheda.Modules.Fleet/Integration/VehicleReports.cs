using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features.Gps;
using AlMotaheda.Modules.Fleet.Features.Vehicles;
using AlMotaheda.Modules.HR.Contracts;
using AlMotaheda.Modules.Reports.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Fleet.Integration;

/// <summary>
/// The vehicle reports, from the same append-only entries and the same arithmetic as the vehicle screens
/// (<see cref="VehicleCustodyBalance"/>): funds by vehicle, vehicle operating expenses, pending and rejected claims.
/// Only posted claims are expenses; a funding is never one.
/// </summary>
internal sealed class VehicleReports(FleetDbContext db, IEmployeeDirectory employees, CompanyClock clock, IOptions<FleetOptions> options) : IReportSource
{
    public const string Custody = "FLT-VEHICLE-CUSTODY";
    public const string Expenses = "FLT-VEHICLE-EXPENSES";
    public const string Claims = "FLT-CLAIMS";
    public const string Lines = "FLT-VEHICLE-EXPENSE-LINES";

    /// <summary>The expense groups of the reports. Every type is in exactly one, so nothing is counted twice.</summary>
    internal static readonly VehicleExpenseType[] MaintenanceTypes =
        [VehicleExpenseType.Maintenance, VehicleExpenseType.Repair, VehicleExpenseType.Tires, VehicleExpenseType.Oil, VehicleExpenseType.SpareParts];

    internal static readonly VehicleExpenseType[] FixedTypes = [VehicleExpenseType.Insurance, VehicleExpenseType.Licensing];

    internal static readonly VehicleExpenseType[] OtherTypes = [VehicleExpenseType.Toll, VehicleExpenseType.Parking, VehicleExpenseType.Wash, VehicleExpenseType.Other];

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
                new("maintenance", "صيانة وإصلاح وإطارات وزيوت وقطع غيار", "Maintenance, repair, tires, oil, parts", ReportColumnTypes.Money, Total: true),
                new("fixed", "تأمين وترخيص", "Insurance and licensing", ReportColumnTypes.Money, Total: true),
                new("other", "رسوم وانتظار وغسيل وأخرى", "Tolls, parking, cleaning, other", ReportColumnTypes.Money, Total: true),
                new("total", "الإجمالي", "Total", ReportColumnTypes.Money, Total: true),
                new("claims", "عدد المطالبات", "Claims", ReportColumnTypes.Integer, Total: true),
                new("distanceKm", "المسافة (كم، عداد)", "Distance (km, odometer)", ReportColumnTypes.Quantity),
                new("costPerKm", "التكلفة لكل كم", "Cost per km", ReportColumnTypes.Money),
                new("gpsDistanceKm", "المسافة (كم، GPS تقديري)", "Distance (km, GPS estimate)", ReportColumnTypes.Quantity),
            ]),
        new(Lines, "سجل مصروفات المركبات (وقود وصيانة وعداد)", "Vehicle expense lines (fuel, maintenance, odometer)", ReportCategories.Equipment, FleetMoney,
            [From, To, VehicleFilter, new("driverEmployeeId", ReportFilterTypes.Id, false, "السائق", "Driver"),
             new("type", ReportFilterTypes.Option, false, "النوع", "Type", Enum.GetNames<VehicleExpenseType>())],
            [
                new("number", "رقم المطالبة", "Claim", ReportColumnTypes.Text),
                new("date", "التاريخ", "Date", ReportColumnTypes.Date),
                new("plate", "اللوحة", "Plate", ReportColumnTypes.Text),
                new("driver", "السائق", "Driver", ReportColumnTypes.Text),
                new("type", "النوع", "Type", ReportColumnTypes.Text),
                new("vendor", "المورد / المحطة", "Vendor / station", ReportColumnTypes.Text),
                new("liters", "اللترات", "Litres", ReportColumnTypes.Quantity, Total: true),
                new("unitPrice", "سعر اللتر", "Price per litre", ReportColumnTypes.Money),
                new("amount", "المبلغ", "Amount", ReportColumnTypes.Money, Total: true),
                new("odometerKm", "قراءة العداد", "Odometer (km)", ReportColumnTypes.Quantity),
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
        Lines => await LinesAsync(query, cancellationToken),
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

        var gpsDistances = to.DayNumber - from.DayNumber <= GpsReportMaxDays
            ? await GpsDistancesAsync(claims.Select(claim => claim.VehicleId).Distinct().ToList(), from, to, cancellationToken)
            : null;

        return new ReportData(claims.GroupBy(claim => claim.VehicleId).OrderBy(group => group.First().Vehicle.Plate).Select(group =>
        {
            decimal Of(params VehicleExpenseType[] types) => group.Where(claim => types.Contains(claim.Type)).Sum(claim => claim.Amount);
            var readings = group.Where(claim => claim.OdometerKm != null).Select(claim => claim.OdometerKm!.Value).ToList();
            var distance = readings.Count > 1 ? readings.Max() - readings.Min() : 0m;
            var total = group.Sum(claim => claim.Amount);
            return new ReportRow(new Dictionary<string, object?>
            {
                ["plate"] = group.First().Vehicle.Plate,
                ["vehicle"] = group.First().Vehicle.Equipment.NameEn,
                ["fuel"] = Of(VehicleExpenseType.Fuel),
                ["liters"] = group.Where(claim => claim.Type == VehicleExpenseType.Fuel).Sum(claim => claim.FuelQuantity ?? 0),
                ["maintenance"] = Of(MaintenanceTypes),
                ["fixed"] = Of(FixedTypes),
                ["other"] = Of(OtherTypes),
                ["total"] = total,
                ["claims"] = group.Count(),
                ["distanceKm"] = distance,
                ["costPerKm"] = distance > 0 ? Math.Round(total / distance, 2) : null,
                ["gpsDistanceKm"] = gpsDistances is null ? null : Math.Round((decimal)gpsDistances.GetValueOrDefault(group.Key) / 1000m, 1),
            }, new ReportLink("vehicle", group.Key));
        }).ToList(),
            "Only approved, posted claims are expenses; a custody funding is never one. Distance (odometer) is between the lowest and highest readings given on the period's claims; " +
            "cost per km is shown only when two readings exist. GPS distance is an estimate from the driver's phone positions (empty for periods over " + GpsReportMaxDays +
            " days; missing where the phone did not track).");
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

    private const int GpsReportMaxDays = 62;

    /// <summary>The GPS distance (metres) of each vehicle over the period, by the same rules as the day view.</summary>
    private async Task<Dictionary<int, double>> GpsDistancesAsync(List<int> vehicleIds, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var start = clock.StartOfDayUtc(from);
        var end = clock.StartOfDayUtc(to.AddDays(1));
        var settings = MotionSettings.From(options.Value.Gps);
        var distances = new Dictionary<int, double>();
        foreach (var vehicleId in vehicleIds)
        {
            var rows = await db.VehicleGpsPoints.AsNoTracking()
                .Where(point => point.VehicleId == vehicleId && point.RecordedAt >= start && point.RecordedAt < end)
                .Select(point => new { point.Id, point.RecordedAt, point.Latitude, point.Longitude, point.AccuracyMeters, point.SpeedKmh })
                .ToListAsync(cancellationToken);
            var timeline = TripSegmenter.Segment(
                rows.Select(row => new GpsSample(row.Id, row.RecordedAt, (double)row.Latitude, (double)row.Longitude, row.AccuracyMeters, (double?)row.SpeedKmh)), settings);
            distances[vehicleId] = timeline.Trips.Sum(trip => trip.DistanceMeters);
        }

        return distances;
    }

    private async Task<ReportData> LinesAsync(ReportQuery query, CancellationToken cancellationToken)
    {
        var from = query.Date("from")!.Value;
        var to = query.Date("to")!.Value;
        var vehicleId = query.Id("vehicleId");
        var driverId = query.Id("driverEmployeeId");
        var list = db.VehicleExpenses.AsNoTracking().Include(claim => claim.Vehicle)
            .Where(claim => claim.Status == VehicleExpenseStatus.Approved && claim.PostingStatus == VehiclePostingStatus.Posted && claim.ExpenseDate >= from && claim.ExpenseDate <= to
                            && (vehicleId == null || claim.VehicleId == vehicleId) && (driverId == null || claim.DriverEmployeeId == driverId));
        if (query.Text("type") is { } type)
        {
            var wanted = Enum.Parse<VehicleExpenseType>(type);
            list = list.Where(claim => claim.Type == wanted);
        }

        var claims = await list.OrderBy(claim => claim.Vehicle.Plate).ThenBy(claim => claim.ExpenseDate).ThenBy(claim => claim.Id).ToListAsync(cancellationToken);
        var names = await NamesAsync(claims.Select(claim => claim.DriverEmployeeId), cancellationToken);
        return new ReportData(claims.Select(claim => new ReportRow(new Dictionary<string, object?>
        {
            ["number"] = claim.Number,
            ["date"] = claim.ExpenseDate,
            ["plate"] = claim.Vehicle.Plate,
            ["driver"] = names.GetValueOrDefault(claim.DriverEmployeeId),
            ["type"] = claim.Type.ToString(),
            ["vendor"] = claim.VendorName,
            ["liters"] = claim.FuelQuantity,
            ["unitPrice"] = claim.UnitPrice,
            ["amount"] = claim.Amount,
            ["odometerKm"] = claim.OdometerKm,
        }, new ReportLink("vehicleExpense", claim.Id))).ToList(),
            "Approved, posted claims only, one line each: refuellings (litres and price per litre), maintenance and the odometer readings given. The driver is the one recorded on the claim.");
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
