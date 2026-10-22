using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;

namespace AlMotaheda.Modules.Fleet.Features.Gps;

/// <summary>
/// How trustworthy a vehicle's tracking is right now.
/// </summary>
/// <param name="State">
/// <c>NotTracked</c> (tracking turned off for the vehicle), <c>NoDriver</c>, <c>NotSetUp</c> (the driver has not
/// accepted the tracking notice on a phone), <c>NoData</c> (set up, nothing received yet), <c>Live</c>,
/// <c>Delayed</c>, and, once stale: <c>PermissionBlocked</c>, <c>GpsOff</c>, <c>TrackingStopped</c>, <c>NoFix</c>
/// (each only when the phone itself said so after its last position) or <c>Offline</c> (nothing explains the silence:
/// no network, phone off, app stopped… unknown).
/// </param>
/// <param name="Source"><c>Device</c> when the phone reported the condition, <c>Server</c> when the server inferred it from silence.</param>
internal sealed record GpsHealthView(string State, string Source, DateTime? LastPointAt, int? MinutesSinceLastPoint, string Detail);

/// <summary>What the driver's phone last reported about itself.</summary>
internal sealed record GpsDeviceView(
    string Platform, string? AppVersion, DateTime PolicyAcceptedAt, DateTime? LastHeartbeatAt, DateTime? LastUploadAt, int? BatteryPercent, bool? BatteryCharging,
    string? TrackingState, string? Permission, bool? GpsEnabled, int? PendingPoints)
{
    public static GpsDeviceView? From(VehicleTrackingDevice? device) => device is null
        ? null
        : new GpsDeviceView(device.Platform, device.AppVersion, device.PolicyAcceptedAt, device.LastHeartbeatAt, device.LastUploadAt, device.BatteryPercent, device.BatteryCharging,
            device.TrackingState, device.Permission, device.GpsEnabled, device.PendingPoints);
}

/// <summary>
/// Classifies tracking health from evidence only. A silent vehicle is never said to be parked, switched off or out
/// of coverage: when the phone said nothing after its last position, the state is <c>Offline</c> with "reason
/// unknown". A condition the phone reported (permission removed, GPS off) is named, with its time.
/// </summary>
internal static class TrackingHealth
{
    public static GpsHealthView Classify(
        bool trackingEnabled, bool hasDriver, VehicleTrackingDevice? device, DateTime? lastPointAt, DateTime now, VehicleGpsOptions settings)
    {
        int? minutes = lastPointAt is { } last ? (int)Math.Max(0, Math.Floor((now - last).TotalMinutes)) : null;
        if (!trackingEnabled)
        {
            return new("NotTracked", "Server", lastPointAt, minutes, "Tracking is turned off for this vehicle.");
        }

        if (!hasDriver)
        {
            return new("NoDriver", "Server", lastPointAt, minutes, "No driver is assigned, so no phone records this vehicle.");
        }

        if (device is null)
        {
            return new("NotSetUp", "Server", lastPointAt, minutes, "The driver has not accepted the tracking notice on a phone yet.");
        }

        if (lastPointAt is { } at && now - at <= TimeSpan.FromMinutes(settings.LiveMinutes))
        {
            return new("Live", "Server", at, minutes, "The last position is recent.");
        }

        if (lastPointAt is { } delayed && now - delayed <= TimeSpan.FromMinutes(settings.StaleMinutes))
        {
            return new("Delayed", "Server", delayed, minutes, "No position for a few minutes; the phone may be queueing them.");
        }

        // Stale (or never). Only a heartbeat taken after the last position may explain it.
        var beat = device.LastHeartbeatAt;
        if (beat is { } heartbeat && (lastPointAt is null || heartbeat > lastPointAt))
        {
            var when = $"reported by the phone at {heartbeat:yyyy-MM-dd HH:mm} UTC";
            if (device.Permission == "Denied" || device.TrackingState is "PermissionDenied" or "BackgroundDenied")
            {
                return new("PermissionBlocked", "Device", lastPointAt, minutes, $"Location permission is missing ({when}).");
            }

            if (device.GpsEnabled == false || device.TrackingState == "GpsOff")
            {
                return new("GpsOff", "Device", lastPointAt, minutes, $"Location services are off on the phone ({when}).");
            }

            if (device.TrackingState == "Stopped")
            {
                return new("TrackingStopped", "Device", lastPointAt, minutes, $"Tracking was stopped on the phone ({when}).");
            }

            if (now - heartbeat <= TimeSpan.FromMinutes(settings.StaleMinutes))
            {
                return new("NoFix", "Device", lastPointAt, minutes, $"The phone is reporting but has no usable position ({when}).");
            }
        }

        return lastPointAt is null
            ? new("NoData", "Server", null, null, "Set up, but no position has been received yet.")
            : new("Offline", "Server", lastPointAt, minutes, "No position received for a while. The reason is unknown: no network, the phone switched off, or the app stopped.");
    }
}
