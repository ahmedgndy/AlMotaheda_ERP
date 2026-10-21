using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features.Vehicles;
using AlMotaheda.Modules.Fleet.Security;
using AlMotaheda.Modules.HR.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Fleet.Features.Gps;

/// <summary>One position of a vehicle's tracker.</summary>
internal sealed record VehicleLocation(DateTime At, decimal Latitude, decimal Longitude, decimal? SpeedKmh, string? DeviceId);

/// <summary>One trip the provider reconstructed: from, to, distance, with the driver and vehicle at the time.</summary>
internal sealed record VehicleTrip(DateTime StartedAt, DateTime EndedAt, decimal DistanceKm, VehicleLocation? Start, VehicleLocation? End, string? Reference);

/// <summary>
/// The vehicle GPS provider: where a tracked vehicle is, where it was, which trips it made. No provider is
/// integrated yet: the one implementation (<see cref="NullVehicleLocationProvider"/>) answers that no provider is
/// configured, and the location endpoints say so truthfully. The site-location pings of app users (Projects) are
/// something else and are never used as vehicle tracking.
/// </summary>
internal interface IVehicleLocationProvider
{
    /// <summary>The provider's name (<c>None</c> when nothing is integrated).</summary>
    string Name { get; }

    bool IsConfigured { get; }

    Task<VehicleLocation?> LatestAsync(string deviceId, CancellationToken cancellationToken);

    Task<IReadOnlyList<VehicleLocation>> HistoryAsync(string deviceId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken);

    Task<IReadOnlyList<VehicleTrip>> TripsAsync(string deviceId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken);
}

/// <summary>The honest default: no provider, no data, never a made-up point.</summary>
internal sealed class NullVehicleLocationProvider : IVehicleLocationProvider
{
    public string Name => VehicleGpsOptions.None;

    public bool IsConfigured => false;

    public Task<VehicleLocation?> LatestAsync(string deviceId, CancellationToken cancellationToken) => Task.FromResult<VehicleLocation?>(null);

    public Task<IReadOnlyList<VehicleLocation>> HistoryAsync(string deviceId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VehicleLocation>>([]);

    public Task<IReadOnlyList<VehicleTrip>> TripsAsync(string deviceId, DateTime fromUtc, DateTime toUtc, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VehicleTrip>>([]);
}

/// <summary>The answer of every location endpoint: the provider's state first, then whatever data exists (none without a provider).</summary>
/// <param name="Status"><c>NotConfigured</c> (no provider, or the vehicle has no tracker), <c>Unavailable</c> (the provider didn't answer) or <c>Available</c>.</param>
internal sealed record VehicleGpsAnswer<T>(
    int VehicleId, string Plate, string Provider, bool ProviderConfigured, string VehicleGpsStatus, string? DeviceId, string Status, int RetentionDays, string Note, T Data);

/// <summary>
/// Vehicle GPS: <c>GET /api/vehicles/{id}/location</c>, <c>/location-history?from&amp;to</c> and <c>/trips?from&amp;to</c>
/// (fleet View), and <c>GET /api/fleet/gps-status</c>. Every answer carries the provider's state; with no provider
/// configured (the shipped state) the data is empty and the status says <c>NotConfigured</c>. Access is checked
/// before any provider call, and the caller's own driving is no exception.
/// </summary>
internal static class VehicleLocationEndpoints
{
    internal sealed record GpsStatusResponse(string Provider, bool Configured, int RetentionDays, int TrackedVehicles, string Note);

    private const string NoProviderNote = "No vehicle GPS provider is integrated. Vehicle tracking needs a provider implementation of IVehicleLocationProvider and the Fleet:Gps settings; nothing is simulated, and the site-location pings of app users are not vehicle tracking.";

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/fleet/gps-status", StatusAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.View)
            .WithName("GetFleetGpsStatus")
            .WithSummary("Whether a vehicle GPS provider is configured, the retention setting and how many vehicles carry a tracker");

        api.MapGet("/vehicles/{vehicleId:int}/location", LatestAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.View)
            .WithName("GetVehicleLocation")
            .WithSummary("The vehicle's latest tracker position, or NotConfigured when there is no provider");

        api.MapGet("/vehicles/{vehicleId:int}/location-history", HistoryAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.View)
            .WithName("GetVehicleLocationHistory")
            .WithSummary("The vehicle's tracker positions between from and to, or NotConfigured when there is no provider");

        api.MapGet("/vehicles/{vehicleId:int}/trips", TripsAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.View)
            .WithName("GetVehicleTrips")
            .WithSummary("The vehicle's trips between from and to with the driver at the time, or NotConfigured when there is no provider");
    }

    internal static async Task<Results<Ok<GpsStatusResponse>, ProblemHttpResult>> StatusAsync(
        HttpContext httpContext, FleetDbContext db, IVehicleLocationProvider provider, IOptions<FleetOptions> settings, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var tracked = await db.Vehicles.AsNoTracking().CountAsync(vehicle => vehicle.GpsStatus != VehicleGpsStatus.NotConfigured, cancellationToken);
        return TypedResults.Ok(new GpsStatusResponse(provider.Name, provider.IsConfigured, settings.Value.Gps.RetentionDays, tracked,
            provider.IsConfigured ? $"Provider {provider.Name} is configured." : NoProviderNote));
    }

    internal static async Task<Results<Ok<VehicleGpsAnswer<VehicleLocation?>>, ProblemHttpResult>> LatestAsync(
        int vehicleId, HttpContext httpContext, FleetDbContext db, IVehicleLocationProvider provider, IOptions<FleetOptions> settings, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == vehicleId, cancellationToken);
        if (vehicle is null)
        {
            return FleetProblems.VehicleNotFound();
        }

        var (status, data) = await AskAsync(vehicle, provider, async device => await provider.LatestAsync(device, cancellationToken));
        return TypedResults.Ok(Answer(vehicle, provider, settings.Value, status, data));
    }

    internal static async Task<Results<Ok<VehicleGpsAnswer<IReadOnlyList<VehicleLocation>>>, ValidationProblem, ProblemHttpResult>> HistoryAsync(
        int vehicleId, HttpContext httpContext, FleetDbContext db, IVehicleLocationProvider provider, IOptions<FleetOptions> settings, CancellationToken cancellationToken,
        string? from = null, string? to = null)
    {
        if (FleetCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var range = Range(from, to, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == vehicleId, cancellationToken);
        if (vehicle is null)
        {
            return FleetProblems.VehicleNotFound();
        }

        var (status, data) = await AskAsync(vehicle, provider, async device => await provider.HistoryAsync(device, range.From, range.To, cancellationToken));
        return TypedResults.Ok(Answer(vehicle, provider, settings.Value, status, data ?? []));
    }

    internal static async Task<Results<Ok<VehicleGpsAnswer<IReadOnlyList<VehicleTripView>>>, ValidationProblem, ProblemHttpResult>> TripsAsync(
        int vehicleId, HttpContext httpContext, FleetDbContext db, IVehicleLocationProvider provider, IEmployeeDirectory employees, IOptions<FleetOptions> settings,
        CancellationToken cancellationToken, string? from = null, string? to = null)
    {
        if (FleetCaller.From(httpContext.User) is null)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var range = Range(from, to, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == vehicleId, cancellationToken);
        if (vehicle is null)
        {
            return FleetProblems.VehicleNotFound();
        }

        var (status, trips) = await AskAsync(vehicle, provider, async device => await provider.TripsAsync(device, range.From, range.To, cancellationToken));

        // The driver at the time of each trip, from the assignment history (never from the provider).
        var assignments = await db.VehicleDriverAssignments.AsNoTracking().Where(assignment => assignment.VehicleId == vehicleId).ToListAsync(cancellationToken);
        var driverIds = assignments.Select(assignment => assignment.DriverEmployeeId).Distinct().ToList();
        var drivers = driverIds.Count == 0 ? new Dictionary<int, EmployeeRef>() : await employees.FindAsync(driverIds, cancellationToken);
        var views = (trips ?? []).Select(trip =>
        {
            var day = DateOnly.FromDateTime(trip.StartedAt);
            var driver = assignments.FirstOrDefault(assignment => assignment.Covers(day));
            return new VehicleTripView(trip, DriverRef.From(driver?.DriverEmployeeId, drivers));
        }).ToList();
        return TypedResults.Ok(Answer(vehicle, provider, settings.Value, status, (IReadOnlyList<VehicleTripView>)views));
    }

    private static async Task<(string Status, T? Data)> AskAsync<T>(Vehicle vehicle, IVehicleLocationProvider provider, Func<string, Task<T?>> ask)
    {
        if (!provider.IsConfigured || vehicle.GpsStatus == VehicleGpsStatus.NotConfigured || vehicle.GpsDeviceId is null)
        {
            return ("NotConfigured", default);
        }

        try
        {
            return ("Available", await ask(vehicle.GpsDeviceId));
        }
        catch (Exception exception) when (exception is HttpRequestException or TimeoutException or IOException)
        {
            return ("Unavailable", default);
        }
    }

    private static VehicleGpsAnswer<T> Answer<T>(Vehicle vehicle, IVehicleLocationProvider provider, FleetOptions options, string status, T data) => new(
        vehicle.Id, vehicle.Plate, provider.Name, provider.IsConfigured, vehicle.GpsStatus.ToString(), vehicle.GpsDeviceId, status, options.Gps.RetentionDays,
        status == "NotConfigured" ? NoProviderNote : status == "Unavailable" ? "The provider did not answer for this vehicle." : "Positions come from the configured provider.", data);

    private static (DateTime From, DateTime To) Range(string? from, string? to, ValidationErrors errors)
    {
        var fromDate = Input.OptionalDate(from, errors, "from", "from") ?? DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1);
        var toDate = Input.OptionalDate(to, errors, "to", "to") ?? DateOnly.FromDateTime(DateTime.UtcNow);
        if (toDate < fromDate)
        {
            errors.Add("to", "to must be on or after from.");
        }

        return (fromDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), toDate.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
    }
}

/// <summary>A trip with the vehicle's driver at the time, from the assignment history.</summary>
internal sealed record VehicleTripView(VehicleTrip Trip, DriverRef? Driver);
