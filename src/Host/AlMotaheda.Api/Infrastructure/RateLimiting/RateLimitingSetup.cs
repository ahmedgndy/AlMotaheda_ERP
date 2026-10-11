using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using AlMotaheda.BuildingBlocks.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AlMotaheda.Api.Infrastructure.RateLimiting;

/// <summary>
/// The rate-limit policies modules attach to endpoints (names in <see cref="RateLimitPolicies"/>).
/// Built into ASP.NET Core; counts are kept in memory per server.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><see cref="RateLimitPolicies.Authentication"/>: sign-in and password change, a small budget per client
///   address (Q6, default 10 a minute) to slow guessing across many accounts. Each account also locks after
///   5 wrong passwords, whatever the address.</item>
///   <item><see cref="RateLimitPolicies.TokenRefresh"/>: a separate, larger budget (default 60 a minute), because
///   every page load refreshes and a whole office may share one address.</item>
/// </list>
/// IPv6 clients are counted per /64 network, the block one customer usually gets, so switching addresses
/// inside it doesn't reset the count. With several servers, each keeps its own count.
/// </remarks>
internal static class RateLimitingSetup
{
    public static IServiceCollection AddRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var authentication = Read(configuration, "RateLimiting:Authentication", defaultPermits: 10);
        var refresh = Read(configuration, "RateLimiting:TokenRefresh", defaultPermits: 60);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = WriteRejectionAsync;

            options.AddPolicy(RateLimitPolicies.Authentication, httpContext => PerClient(httpContext, authentication));
            options.AddPolicy(RateLimitPolicies.TokenRefresh, httpContext => PerClient(httpContext, refresh));
        });

        return services;
    }

    private static RateLimitPartition<string> PerClient(HttpContext httpContext, Limit limit) =>
        RateLimitPartition.GetFixedWindowLimiter(
            ClientKey(httpContext.Connection.RemoteIpAddress),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limit.PermitLimit, Window = limit.Window, QueueLimit = 0 });

    /// <summary>The address itself for IPv4, the /64 network for IPv6.</summary>
    internal static string ClientKey(IPAddress? address)
    {
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes) + "/64";
    }

    private static Limit Read(IConfiguration configuration, string section, int defaultPermits)
    {
        var limit = configuration.GetSection(section).Get<Limit>() ?? new Limit { PermitLimit = defaultPermits };
        if (limit.PermitLimit < 1 || limit.Window <= TimeSpan.Zero)
        {
            throw new InvalidOperationException($"{section}: PermitLimit must be at least 1 and Window positive.");
        }

        return limit;
    }

    private static async ValueTask WriteRejectionAsync(OnRejectedContext context, CancellationToken cancellationToken)
    {
        var response = context.HttpContext.Response;

        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        }

        var problemDetails = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context.HttpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Title = "Too many attempts. Wait a moment and try again.",
                Extensions = { [ApiProblem.CodeKey] = ApiProblem.TooManyRequests },
            },
        });
    }

    private sealed class Limit
    {
        public int PermitLimit { get; set; } = 10;
        public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
    }
}
