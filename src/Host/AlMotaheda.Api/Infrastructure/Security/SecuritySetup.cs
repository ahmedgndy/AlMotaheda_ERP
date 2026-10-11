using System.Net;
using AlMotaheda.BuildingBlocks.Authorization;
using Microsoft.AspNetCore.HttpOverrides;

namespace AlMotaheda.Api.Infrastructure.Security;

/// <summary>
/// Authentication, authorization and the real client address.
/// </summary>
/// <remarks>
/// <para>The authentication scheme itself (JWT bearer) is registered by the Access module, which issues the tokens.
/// The host adds the permission checks from BuildingBlocks and requires a signed-in user on every <c>/api</c>
/// endpoint unless the endpoint says otherwise (sign-in, refresh, sign-out).</para>
/// <para>Behind a reverse proxy, the client address arrives in <c>X-Forwarded-For</c>. Only proxies listed in
/// <c>ForwardedHeaders:KnownProxies</c> (plus this machine) are trusted, so a client can't fake its address
/// to get around the sign-in rate limit.</para>
/// </remarks>
internal static class SecuritySetup
{
    private const string KnownProxiesKey = "ForwardedHeaders:KnownProxies";

    public static IServiceCollection AddSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddAuthorization();
        services.AddPermissionAuthorization();

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            foreach (var proxy in configuration.GetSection(KnownProxiesKey).Get<string[]>() ?? [])
            {
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            }
        });

        return services;
    }

    /// <summary>Must run first, so logging, rate limiting and the cookie's Secure flag see the real client.</summary>
    public static WebApplication UseClientAddress(this WebApplication app)
    {
        app.UseForwardedHeaders();

        // A trusted proxy's X-Forwarded-For is consumed above. One that is still here came from an address that
        // isn't trusted: either a client trying to fake its address (ignored, correctly) or a real proxy missing
        // from ForwardedHeaders:KnownProxies, which makes every user share the proxy's rate-limit budget.
        // Warn once, so the second case is noticed.
        var warned = 0;
        app.Use((context, next) =>
        {
            if (context.Request.Headers.ContainsKey("X-Forwarded-For") && Interlocked.Exchange(ref warned, 1) == 0)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(SecuritySetup))
                    .LogWarning(
                        "Ignored X-Forwarded-For from untrusted address {RemoteIp}. If the API runs behind a proxy on "
                        + "another machine, add the proxy's address to {Setting}.",
                        context.Connection.RemoteIpAddress,
                        KnownProxiesKey);
            }

            return next(context);
        });

        return app;
    }

    public static WebApplication UseSecurity(this WebApplication app)
    {
        app.UseAuthentication();
        app.UseRateLimiter();
        app.UseAuthorization();
        return app;
    }
}
