using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Projects.Features.SiteLocation;

/// <summary>The settings the phone follows while it tracks. They come from the server, so they can change without a new app.</summary>
internal sealed record TrackingSettingsView(
    int HeartbeatMinutes,
    int SampleSeconds,
    int MinDistanceMeters,
    int UnavailableAfterMinutes,
    bool TrackAllDay,
    IReadOnlyList<string> WorkDays,
    string WorkStart,
    string WorkEnd,
    int LowBatteryPercent,
    int MaxLateDays,
    int MaxBatchPoints,
    int KeepDays);

/// <summary>This phone's acceptance of the policy, as the server stored it.</summary>
internal sealed record TrackingDeviceView(string DeviceId, int PolicyVersion, DateTime PolicyAcceptedAt, DateTime? LastHeartbeatAt);

/// <summary>
/// <c>GET /api/site-location/me</c>: whether the caller is tracked, on which projects, under which policy version,
/// and with which settings. <see cref="Subject"/> is false for anyone who is not an active site engineer.
/// </summary>
/// <param name="Accepted">True when this phone accepted the current policy version.</param>
/// <param name="Viewers">Who may see the positions: <c>ProjectManagerAndExecutives</c>.</param>
internal sealed record TrackingConfigView(
    bool Subject,
    int PolicyVersion,
    bool Accepted,
    TrackingDeviceView? Device,
    IReadOnlyList<TrackedProject> Projects,
    TrackingSettingsView Settings,
    bool InTrackingHours,
    string Viewers);

/// <summary>
/// Automatic tracking on the engineer's company phone (owner's brief of 2026-10-17, evening). There is no daily
/// start button: the engineer accepts the tracking policy once on the phone, and from then on the phone tracks and
/// uploads by itself.
/// <list type="bullet">
/// <item><c>GET /api/site-location/me?deviceId=</c>: the configuration above.</item>
/// <item><c>PUT /api/site-location/me/policy</c> <c>{deviceId, platform, appVersion, policyVersion}</c>: the engineer
/// accepted the policy on this phone. Tracking turns on for every project where they are a site engineer.</item>
/// <item><c>POST /api/site-location/me/batch</c> <c>{deviceId, appVersion, points[], heartbeats[]}</c>: the phone's
/// queue of positions (each with its own id and the time it was taken) and heartbeats.</item>
/// </list>
/// The project manager and Executives see the result through the existing <c>/api/projects/{pid}/site-location</c>
/// endpoints; the engineer never sees anyone else's position.
/// </summary>
internal static class TrackingEndpoints
{
    internal sealed record PolicyRequest(string? DeviceId, string? Platform, string? AppVersion, int? PolicyVersion);

    private static readonly string[] Platforms = ["Android", "iOS", "macOS", "Windows", "Linux"];

    public static void Map(IEndpointRouteBuilder api)
    {
        var tracking = api.MapGroup("/site-location").WithTags("Projects").RequireAuthorization();

        tracking.MapGet("/me", GetConfigAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.View)
            .WithName("GetMyTracking")
            .WithSummary("Whether this user is tracked, on which projects, under which policy version and settings");

        tracking.MapPut("/me/policy", AcceptPolicyAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.View)
            .WithName("AcceptTrackingPolicy")
            .WithSummary("The engineer accepted the location tracking policy on this phone; tracking starts by itself");

        tracking.MapPost("/me/batch", UploadBatchAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.View)
            .WithName("UploadTrackingBatch")
            .WithSummary("The phone's queue of positions and heartbeats, each with the time it was taken (idempotent by position id)");
    }

    internal static async Task<Results<Ok<TrackingConfigView>, ValidationProblem, ProblemHttpResult>> GetConfigAsync(
        HttpContext httpContext,
        SiteLocationTracker tracker,
        ProjectsDbContext db,
        CompanyClock clock,
        IOptions<SiteLocationOptions> options,
        CancellationToken cancellationToken,
        string? deviceId = null)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var device = Input.Clean(deviceId) is { } id
            ? await db.LocationDevices.AsNoTracking().FirstOrDefaultAsync(row => row.UserId == caller.UserId && row.DeviceId == id, cancellationToken)
            : null;
        return TypedResults.Ok(await ConfigAsync(caller.UserId, device, tracker, clock, options.Value, cancellationToken));
    }

    internal static async Task<Results<Ok<TrackingConfigView>, ValidationProblem, ProblemHttpResult>> AcceptPolicyAsync(
        PolicyRequest request,
        HttpContext httpContext,
        SiteLocationTracker tracker,
        CompanyClock clock,
        IOptions<SiteLocationOptions> options,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var settings = options.Value;
        var errors = new ValidationErrors();
        var deviceId = DeviceId(request.DeviceId, errors);
        var platform = Input.Clean(request.Platform);
        if (platform is null || !Platforms.Contains(platform))
        {
            errors.Add("platform", $"platform must be one of {string.Join(", ", Platforms)}.");
        }

        if (request.PolicyVersion != settings.PolicyVersion)
        {
            errors.Add("policyVersion", $"Accept the current policy, version {settings.PolicyVersion}.");
        }

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        var device = await tracker.AcceptPolicyAsync(caller.UserId, deviceId!, platform!, Input.Clean(request.AppVersion), cancellationToken);
        return TypedResults.Ok(await ConfigAsync(caller.UserId, device, tracker, clock, settings, cancellationToken));
    }

    internal static async Task<Results<Ok<TrackingBatchResult>, ValidationProblem, ProblemHttpResult>> UploadBatchAsync(
        TrackingBatchRequest request,
        HttpContext httpContext,
        SiteLocationTracker tracker,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var deviceId = DeviceId(request.DeviceId, errors);
        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        // Without the policy accepted on this phone nothing is kept: tracking is never silent.
        if (await tracker.AcceptedDeviceAsync(caller.UserId, deviceId!, cancellationToken) is not { } device)
        {
            return ProjectProblems.PolicyNotAccepted();
        }

        var result = await tracker.RecordAsync(caller.UserId, device, request, errors, cancellationToken);
        return result is null ? ProjectProblems.Validation(errors) : TypedResults.Ok(result);
    }

    private static string? DeviceId(string? value, ValidationErrors errors)
    {
        var deviceId = Input.Clean(value);
        if (deviceId is null || deviceId.Length > LocationDeviceConfiguration.DeviceIdMaxLength || deviceId.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-'))
        {
            errors.Add("deviceId", $"deviceId is required: letters, digits and dashes, at most {LocationDeviceConfiguration.DeviceIdMaxLength}.");
            return null;
        }

        return deviceId;
    }

    private static async Task<TrackingConfigView> ConfigAsync(
        int userId, LocationDevice? device, SiteLocationTracker tracker, CompanyClock clock, SiteLocationOptions settings, CancellationToken cancellationToken)
    {
        var projects = await tracker.TrackedProjectsAsync(userId, cancellationToken);
        var accepted = device is not null && device.PolicyVersion >= settings.PolicyVersion;
        return new TrackingConfigView(
            projects.Count > 0,
            settings.PolicyVersion,
            accepted,
            device is null ? null : new TrackingDeviceView(device.DeviceId, device.PolicyVersion, device.PolicyAcceptedAt, device.LastHeartbeatAt),
            projects,
            new TrackingSettingsView(
                settings.HeartbeatMinutes,
                settings.SampleSeconds,
                settings.MinDistanceMeters,
                settings.UnavailableAfterMinutes,
                settings.TrackAllDay,
                settings.WorkDays.Select(day => day.ToString()).ToList(),
                settings.WorkStart,
                settings.WorkEnd,
                settings.LowBatteryPercent,
                settings.MaxLateDays,
                settings.MaxBatchPoints,
                settings.KeepDays),
            SiteLocationRules.InWorkingHours(clock.UtcNow, clock.Zone, settings),
            "ProjectManagerAndExecutives");
    }
}
