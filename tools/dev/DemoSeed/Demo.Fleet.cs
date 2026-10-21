namespace AlMotaheda.DemoSeed;

internal sealed record VehicleSpec(string Key, string AssetNo, string Plate, string NameAr, string NameEn, string Type, int ModelYear, string Fuel, decimal OdometerKm, Person Driver, ProjectSpec? Project);

internal sealed partial class Demo
{
    private static readonly VehicleSpec Pickup = new("pickup", "VH-001", "س ط ر 4821", "بيك أب تويوتا هايلكس", "Toyota Hilux pickup", "Pickup", 2021, "Diesel", 48200, Cast.Driver, Sites.NileTower);
    private static readonly VehicleSpec Truck = new("truck", "VH-002", "ن ق ل 7310", "نقل مرسيدس أكتروس", "Mercedes Actros truck", "Truck", 2019, "Diesel", 211500, Cast.Driver2, null);

    private readonly Dictionary<string, int> _vehicleIds = new();

    private int VehicleId(VehicleSpec vehicle) => _vehicleIds[vehicle.Key];

    /// <summary>Two vehicles, each with its driver (an HR employee linked to a Staff login); the pickup works on the Nile Tower.</summary>
    private async Task VehiclesAsync()
    {
        Log.Section("Vehicles and drivers");
        var ceo = As(Cast.Ceo);
        foreach (var spec in new[] { Pickup, Truck })
        {
            var found = (await ceo.GetAsync($"/api/vehicles?search={Uri.EscapeDataString(spec.AssetNo)}&pageSize=10"))
                .Items().FirstOrDefault(row => row.Str("assetNo") == spec.AssetNo);
            if (found is null)
            {
                found = await ceo.PostAsync("/api/vehicles", new
                {
                    assetNo = spec.AssetNo,
                    nameAr = spec.NameAr,
                    nameEn = spec.NameEn,
                    plate = spec.Plate,
                    type = spec.Type,
                    modelYear = spec.ModelYear,
                    fuelType = spec.Fuel,
                    ownership = "Owned",
                    condition = "Good",
                    status = "InService",
                    odometerKm = spec.OdometerKm,
                    notes = $"{Marker} demo vehicle. No GPS provider is configured, so no vehicle positions exist.",
                });
                Log.New($"vehicle {spec.AssetNo} {spec.Plate} {spec.NameEn}");
            }
            else
            {
                Log.Kept($"vehicle {spec.AssetNo} {spec.Plate}");
            }

            _vehicleIds[spec.Key] = found.Id();

            var drivers = (await ceo.GetAsync($"/api/vehicles/{found.Id()}/drivers")).Items().ToList();
            if (!drivers.Any(assignment => assignment["endDate"] is null))
            {
                await ceo.PostAsync($"/api/vehicles/{found.Id()}/drivers", new
                {
                    driverEmployeeId = EmployeeId(spec.Driver),
                    startDate = Iso(today.AddDays(-90)),
                    startOdometerKm = spec.OdometerKm,
                    notes = $"{Marker} handed over with a full tank",
                });
                Log.New($"driver {spec.Driver.NameEn} on {spec.AssetNo}");
            }
            else
            {
                Log.Kept($"driver on {spec.AssetNo}");
            }

            if (spec.Project is not null)
            {
                var equipment = (await ceo.GetAsync($"/api/projects/{ProjectId(spec.Project)}/equipment?pageSize=50")).Items().ToList();
                if (!equipment.Any(row => (int?)row["equipmentId"] == found.Id() && row["endDate"] is null))
                {
                    await ceo.PostAsync($"/api/projects/{ProjectId(spec.Project)}/equipment", new
                    {
                        equipmentId = found.Id(),
                        responsibleUserId = UserId(spec.Project.SiteEngineer ?? Cast.Manager),
                        operatorName = spec.Driver.NameAr,
                        startDate = Iso(today.AddDays(-90)),
                        notes = Marker,
                    });
                    Log.New($"{spec.AssetNo} assigned to {spec.Project.Code}");
                }
                else
                {
                    Log.Kept($"{spec.AssetNo} on {spec.Project.Code}");
                }
            }
        }
    }
}
