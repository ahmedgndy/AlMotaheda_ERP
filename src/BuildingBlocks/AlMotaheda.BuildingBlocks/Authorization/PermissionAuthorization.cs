using AlMotaheda.BuildingBlocks.Http;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.BuildingBlocks.Authorization;

/// <summary>
/// Server-side permission checks for endpoints, based on the claims in the access token.
/// </summary>
/// <example>
/// <code>
/// group.MapGet("/", ListProjects.HandleAsync).RequirePermission("projects", PermissionAction.View);
/// </code>
/// </example>
public static class PermissionAuthorization
{
    /// <summary>
    /// Registers the permission checks and makes every authorized endpoint refuse users who still have to
    /// change their password. Call once, from the host.
    /// </summary>
    public static IServiceCollection AddPermissionAuthorization(this IServiceCollection services)
    {
        services.AddAuthorizationBuilder().SetDefaultPolicy(SignedInPolicy().Build());

        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddSingleton<IAuthorizationHandler, CompletedPasswordChangeHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, ProblemDetailsAuthorizationResultHandler>();

        return services;
    }

    /// <summary>Only users whose token grants <paramref name="action"/> on <paramref name="module"/> may call the endpoint.</summary>
    public static TBuilder RequirePermission<TBuilder>(this TBuilder builder, string module, PermissionAction action)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(module);

        return builder.RequireAuthorization(SignedInPolicy()
            .AddRequirements(new PermissionRequirement(module, action))
            .Build());
    }

    /// <summary>
    /// The start of every endpoint-specific policy: a signed-in user with no pending password change.
    /// </summary>
    /// <remarks>
    /// An endpoint that has its own policy object doesn't get the default policy: ASP.NET Core applies the default
    /// policy only to endpoints without one. So every policy built for an endpoint must start here, or a user who
    /// still has to change their password could call it.
    /// </remarks>
    public static AuthorizationPolicyBuilder SignedInPolicy() =>
        new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new CompletedPasswordChangeRequirement());

    /// <summary>
    /// Lets a signed-in user call this endpoint even while they still have to change their password
    /// (changing the password, reading your own profile, signing out).
    /// </summary>
    public static TBuilder AllowDuringPasswordChange<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(AllowDuringPasswordChangeMetadata.Instance);
}

/// <summary>The caller's token must grant one action on one module.</summary>
public sealed record PermissionRequirement(string Module, PermissionAction Action) : IAuthorizationRequirement;

/// <summary>The caller must not have a pending forced password change, unless the endpoint allows it.</summary>
public sealed class CompletedPasswordChangeRequirement : IAuthorizationRequirement;

internal sealed class AllowDuringPasswordChangeMetadata
{
    public static readonly AllowDuringPasswordChangeMetadata Instance = new();
}

internal sealed class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (context.User.HasPermission(requirement.Module, requirement.Action))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

internal sealed class CompletedPasswordChangeHandler : AuthorizationHandler<CompletedPasswordChangeRequirement>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        CompletedPasswordChangeRequirement requirement)
    {
        var endpointAllowsIt = context.Resource is HttpContext httpContext
            && httpContext.GetEndpoint()?.Metadata.GetMetadata<AllowDuringPasswordChangeMetadata>() is not null;

        if (!context.User.IsPasswordChangeRequired() || endpointAllowsIt)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Writes 403 responses as ProblemDetails with a <c>code</c>, so the frontend can tell a missing permission
/// from a pending password change. 401 responses are written by the authentication scheme.
/// </summary>
internal sealed class ProblemDetailsAuthorizationResultHandler(IProblemDetailsService problemDetailsService)
    : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Forbidden)
        {
            await _default.HandleAsync(next, context, policy, authorizeResult);
            return;
        }

        var passwordChangePending = authorizeResult.AuthorizationFailure?.FailedRequirements
            .OfType<CompletedPasswordChangeRequirement>()
            .Any() == true;

        var (code, title) = passwordChangePending
            ? (ApiProblem.PasswordChangeRequired, "Change your password before continuing.")
            : (ApiProblem.Forbidden, "You don't have permission to do this.");

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = title,
                Extensions = { [ApiProblem.CodeKey] = code },
            },
        });
    }
}
