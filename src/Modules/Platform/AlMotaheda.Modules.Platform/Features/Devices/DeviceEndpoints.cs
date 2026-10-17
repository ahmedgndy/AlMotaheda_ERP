using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Validation;
using AlMotaheda.Modules.Platform.Common;
using AlMotaheda.Modules.Platform.Data;
using AlMotaheda.Modules.Platform.Data.Configurations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Platform.Features.Devices;

/// <summary>
/// <c>POST /api/devices</c>: the app registers the phone's push token for the signed-in user, at every sign-in and
/// whenever Firebase gives it a new token. Registering the same token again only refreshes it, and a token that was
/// registered by someone else moves to the caller (one phone, one user at a time).
/// </summary>
internal static class RegisterDevice
{
    internal sealed record Request(string? Platform, string? Token);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/devices", HandleAsync)
            .WithName(nameof(RegisterDevice))
            .WithSummary("Register this phone's push token for the signed-in user (Android, Firebase Cloud Messaging)");

    internal static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> HandleAsync(
        Request request, HttpContext httpContext, PlatformDbContext db, TimeProvider time, PlatformLog log, CancellationToken cancellationToken)
    {
        if (httpContext.User.GetUserId() is not { } userId)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var platform = DevicePlatforms.All.FirstOrDefault(known => string.Equals(known, request.Platform?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (platform is null)
        {
            errors.Add("platform", $"platform must be one of: {string.Join(", ", DevicePlatforms.All)}.");
        }

        var token = request.Token?.Trim();
        if (string.IsNullOrEmpty(token) || token.Length > DeviceTokenConfiguration.TokenMaxLength)
        {
            errors.Add("token", $"token is required, at most {DeviceTokenConfiguration.TokenMaxLength} characters.");
        }

        if (errors.HasErrors)
        {
            return PlatformProblems.Validation(errors);
        }

        var now = time.GetUtcNow().UtcDateTime;
        // Two quick registrations of the same new token race on the unique index: the loser updates instead.
        for (var attempt = 0; ; attempt++)
        {
            var device = await db.DeviceTokens.SingleOrDefaultAsync(existing => existing.Token == token, cancellationToken);
            if (device is null)
            {
                device = new DeviceToken { Token = token!, CreatedAt = now };
                db.DeviceTokens.Add(device);
            }
            else if (device.UserId != userId && device.UserId != 0)
            {
                log.DeviceMoved(device.Id, device.UserId, userId);
            }

            device.UserId = userId;
            device.Platform = platform!;
            device.LastSeenAt = now;
            device.DisabledAt = null;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                log.DeviceRegistered(device.Id, userId);
                return TypedResults.NoContent();
            }
            catch (DbUpdateException) when (attempt == 0)
            {
                db.ChangeTracker.Clear();
            }
        }
    }
}

/// <summary>
/// <c>POST /api/devices/unregister</c>: the app removes the phone's token when the user signs out, so that phone gets
/// no more pushes for them. Only the caller's own token is removed; an unknown token answers 204 all the same.
/// </summary>
internal static class UnregisterDevice
{
    internal sealed record Request(string? Token);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapPost("/devices/unregister", HandleAsync)
            .WithName(nameof(UnregisterDevice))
            .WithSummary("Stop push notifications on this phone for the signed-in user");

    internal static async Task<Results<NoContent, ValidationProblem, ProblemHttpResult>> HandleAsync(
        Request request, HttpContext httpContext, PlatformDbContext db, PlatformLog log, CancellationToken cancellationToken)
    {
        if (httpContext.User.GetUserId() is not { } userId)
        {
            return ApiProblem.ForbiddenResult();
        }

        var token = request.Token?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            var errors = new ValidationErrors();
            errors.Add("token", "token is required.");
            return PlatformProblems.Validation(errors);
        }

        var removed = await db.DeviceTokens.Where(device => device.Token == token && device.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        if (removed > 0)
        {
            log.DeviceUnregistered(userId);
        }

        return TypedResults.NoContent();
    }
}
