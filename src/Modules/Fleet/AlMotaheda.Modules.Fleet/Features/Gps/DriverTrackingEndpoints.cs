using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Data.Configurations;
using AlMotaheda.Modules.Fleet.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Fleet.Features.Gps;

/// <summary>The settings the phone follows while it tracks the vehicle. They come from the server, so they change without a new app.</summary>
internal sealed record DriverTrackingSettingsView(int SampleSeconds, int MinDistanceMeters, int HeartbeatMinutes, int MaxLateDays, int MaxBatchPoints, int StaleMinutes);

/// <summary>This phone's acceptance of the notice, as the server stored it.</summary>
internal sealed record DriverTrackingDeviceView(string DeviceId, int PolicyVersion, DateTime PolicyAcceptedAt, DateTime? LastHeartbeatAt, DateTime? LastUploadAt);

/// <summary>
/// <c>GET /api/fleet/tracking/me</c>: whether the caller's phone should track a vehicle now, which one, under which
/// notice version and settings, and what the server last received from them.
/// </summary>
/// <param name="Subject">True for an active employee with an open assignment on a vehicle whose tracking is on.</param>
/// <param name="Accepted">True when this phone accepted the current notice version.</param>
/// <param name="Viewers">Who may see the positions: <c>FleetManagersAndExecutives</c>.</param>
internal sealed record DriverTrackingConfigView(
    bool Subject,
    GpsTrackedVehicle? Vehicle,
    int PolicyVersion,
    bool Accepted,
    DriverTrackingDeviceView? Device,
    DriverTrackingSettingsView Settings,
    DateTime? LastPointReceivedAt,
    string Viewers);

/// <summary>
/// Vehicle tracking on the driver's phone. There is no daily start button: the driver accepts the tracking notice once
/// on the phone; from then on, while they are assigned a vehicle whose tracking is on, the phone records positions
/// and uploads its queue by itself.
/// <list type="bullet">
/// <item><c>GET /api/fleet/tracking/me?deviceId=</c>: the configuration above.</item>
/// <item><c>PUT /api/fleet/tracking/me/policy</c> <c>{deviceId, platform, appVersion, policyVersion}</c>: the driver accepted the notice on this phone.</item>
/// <item><c>POST /api/fleet/tracking/me/batch</c> <c>{deviceId, appVersion, points[], heartbeats[]}</c>: the phone's queue,
/// each position with its own id and capture time (idempotent by id).</item>
/// </list>
/// fleet View is enough (every driver has it); the vehicle always comes from the caller's own assignments.
/// </summary>
internal static class DriverTrackingEndpoints
{
    internal sealed record PolicyRequest(string? DeviceId, string? Platform, string? AppVersion, int? PolicyVersion);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/fleet/tracking/me", GetConfigAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.View)
            .WithName("GetMyVehicleTracking")
            .WithSummary("Whether this user's phone should track a vehicle now, which one, under which notice version and settings");

        api.MapPut("/fleet/tracking/me/policy", AcceptPolicyAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.View)
            .WithName("AcceptVehicleTrackingPolicy")
            .WithSummary("The driver accepted the vehicle tracking notice on this phone; tracking then runs by itself");

        api.MapPost("/fleet/tracking/me/batch", UploadBatchAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.View)
            .WithName("UploadVehicleTrackingBatch")
            .WithSummary("The phone's queue of vehicle positions and heartbeats, each position with the time it was taken (idempotent by position id)");
    }

    internal static async Task<Results<Ok<DriverTrackingConfigView>, ValidationProblem, ProblemHttpResult>> GetConfigAsync(
        HttpContext httpContext, DriverTracking tracking, FleetDbContext db, IOptions<FleetOptions> options, CancellationToken cancellationToken, string? deviceId = null)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var device = Input.Clean(deviceId) is { } id ? await tracking.DeviceAsync(caller.UserId, id, cancellationToken) : null;
        return TypedResults.Ok(await ConfigAsync(caller.UserId, device, tracking, db, options.Value.Gps, cancellationToken));
    }

    internal static async Task<Results<Ok<DriverTrackingConfigView>, ValidationProblem, ProblemHttpResult>> AcceptPolicyAsync(
        PolicyRequest request, HttpContext httpContext, DriverTracking tracking, FleetDbContext db, IOptions<FleetOptions> options, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var settings = options.Value.Gps;
        var errors = new ValidationErrors();
        var deviceId = DeviceId(request.DeviceId, errors);
        var platform = Input.Clean(request.Platform);
        if (platform is null || !DriverTracking.Platforms.Contains(platform))
        {
            errors.Add("platform", $"platform must be one of {string.Join(", ", DriverTracking.Platforms)}.");
        }

        if (request.PolicyVersion != settings.PolicyVersion)
        {
            errors.Add("policyVersion", $"Accept the current notice, version {settings.PolicyVersion}.");
        }

        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var device = await tracking.AcceptPolicyAsync(caller.UserId, deviceId!, platform!, request.AppVersion, cancellationToken);
        return TypedResults.Ok(await ConfigAsync(caller.UserId, device, tracking, db, settings, cancellationToken));
    }

    internal static async Task<Results<Ok<VehicleTrackingBatchResult>, ValidationProblem, ProblemHttpResult>> UploadBatchAsync(
        VehicleTrackingBatchRequest request, HttpContext httpContext, DriverTracking tracking, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var deviceId = DeviceId(request.DeviceId, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        // Without the notice accepted on this phone nothing is kept: tracking is never silent.
        if (await tracking.AcceptedDeviceAsync(caller.UserId, deviceId!, cancellationToken) is not { } device)
        {
            return FleetProblems.TrackingNoticeNotAccepted();
        }

        var result = await tracking.RecordAsync(caller.UserId, device, request, errors, cancellationToken);
        return result is null ? FleetProblems.Validation(errors) : TypedResults.Ok(result);
    }

    private static string? DeviceId(string? value, ValidationErrors errors)
    {
        var deviceId = Input.Clean(value);
        if (deviceId is null || deviceId.Length > VehicleTrackingDeviceConfiguration.DeviceIdMaxLength
                             || deviceId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            errors.Add("deviceId", $"deviceId is required: letters, digits and dashes, at most {VehicleTrackingDeviceConfiguration.DeviceIdMaxLength}.");
            return null;
        }

        return deviceId;
    }

    private static async Task<DriverTrackingConfigView> ConfigAsync(
        int userId, VehicleTrackingDevice? device, DriverTracking tracking, FleetDbContext db, VehicleGpsOptions settings, CancellationToken cancellationToken)
    {
        var vehicle = await tracking.CurrentAsync(userId, cancellationToken);
        var lastReceived = vehicle is null
            ? null
            : await db.VehicleTrackingStates.AsNoTracking().Where(state => state.VehicleId == vehicle.VehicleId).Select(state => state.LastReceivedAt).FirstOrDefaultAsync(cancellationToken);
        return new DriverTrackingConfigView(
            vehicle is not null,
            vehicle,
            settings.PolicyVersion,
            device is not null && device.PolicyVersion >= settings.PolicyVersion,
            device is null ? null : new DriverTrackingDeviceView(device.DeviceId, device.PolicyVersion, device.PolicyAcceptedAt, device.LastHeartbeatAt, device.LastUploadAt),
            new DriverTrackingSettingsView(settings.SampleSeconds, settings.MinDistanceMeters, settings.HeartbeatMinutes, settings.MaxLateDays, settings.MaxBatchPoints, settings.StaleMinutes),
            lastReceived,
            "FleetManagersAndExecutives");
    }
}
