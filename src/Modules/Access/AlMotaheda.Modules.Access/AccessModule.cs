using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Modules;
using AlMotaheda.Modules.Access.Common;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Access.Data;
using AlMotaheda.Modules.Access.Features.Auth;
using AlMotaheda.Modules.Access.Features.Roles;
using AlMotaheda.Modules.Access.Features.UserPermissions;
using AlMotaheda.Modules.Access.Features.Users;
using AlMotaheda.Modules.Access.Integration;
using AlMotaheda.Modules.Access.Security;
using AlMotaheda.Modules.Access.Startup;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Access;

/// <summary>
/// Entry point of the Access module: sign-in, users, roles and permissions. This is the only public type in the module.
/// </summary>
/// <remarks>
/// The module issues the access tokens, so it also registers how they are validated (the JWT bearer scheme).
/// Other modules never reference it: they check permissions through BuildingBlocks' <c>RequirePermission</c>.
/// </remarks>
public sealed class AccessModule : IModule
{
    public string Name => "Access";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(AccessDatabase.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{AccessDatabase.ConnectionStringName} is missing. Set it with user secrets in development "
                + $"or the ConnectionStrings__{AccessDatabase.ConnectionStringName} environment variable on the server.");
        }

        services.AddDbContext<AccessDbContext>(options => options
            .UseSqlServer(connectionString, AccessDatabase.Configure)
            // Failures are logged once, by the host's exception handler. EF's own error logs would repeat them,
            // and its "save failed" entry would copy the duplicate key value (an email address) into the logs
            // even when the slice handles the error as a normal 409.
            .ConfigureWarnings(warnings => warnings.Ignore(
                CoreEventId.SaveChangesFailed,
                CoreEventId.QueryIterationFailed,
                RelationalEventId.CommandError,
                RelationalEventId.ConnectionError)));

        services.AddOptions<AccessTokenOptions>().Bind(configuration.GetSection(AccessTokenOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AccessTokenOptions>, AccessTokenOptionsValidator>();
        services.AddOptions<AccessSessionOptions>().Bind(configuration.GetSection(AccessSessionOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AccessSessionOptions>, AccessSessionOptionsValidator>();
        services.AddOptions<AccessLockoutOptions>().Bind(configuration.GetSection(AccessLockoutOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AccessLockoutOptions>, AccessLockoutOptionsValidator>();
        services.AddOptions<AccessBootstrapOptions>().Bind(configuration.GetSection(AccessBootstrapOptions.SectionName));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<AccessTokenOptions>>((bearer, tokens) =>
            {
                // Keep the token's own claim names ("sub", "role", "perm") instead of the long WS-* names.
                bearer.MapInboundClaims = false;
                bearer.TokenValidationParameters = AccessTokenIssuer.CreateValidationParameters(tokens.Value);
                bearer.Events = new JwtBearerEvents { OnChallenge = WriteUnauthenticatedProblemAsync };
            });

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<PasswordService>();
        services.AddSingleton<AccessTokenIssuer>();
        services.AddSingleton<SecurityLog>();
        services.AddScoped<PermissionResolver>();
        services.AddScoped<SessionFactory>();
        services.AddScoped<SignInAttempts>();
        services.AddScoped<UserManagementGate>();

        // What other modules may ask the Access module (AlMotaheda.Modules.Access.Contracts).
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<IExecutiveCheck, ExecutiveCheck>();

        services.AddHostedService<AccessStartup>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/auth").WithTags("Authentication");
        Login.Map(auth);
        Refresh.Map(auth);
        Logout.Map(auth);
        ChangePassword.Map(auth);
        GetMe.Map(endpoints);

        // Every users and roles endpoint needs a signed-in user, whatever the host adds.
        var users = endpoints.MapGroup("/users").WithTags("Users").RequireAuthorization();
        ListUsers.Map(users);
        GetUser.Map(users);
        CreateUser.Map(users);
        UpdateUser.Map(users);
        SetUserStatus.Map(users);
        ResetPassword.Map(users);
        GetUserPermissions.Map(users);
        SetUserModulePermissions.Map(users);
        ResetUserPermissions.Map(users);

        var roles = endpoints.MapGroup("/roles").WithTags("Roles").RequireAuthorization();
        ListRoles.Map(roles);
        UpdateRolePermissions.Map(roles);
    }

    /// <summary>401 as ProblemDetails with <c>code: "unauthenticated"</c>, the same shape as every other error.</summary>
    private static async Task WriteUnauthenticatedProblemAsync(JwtBearerChallengeContext context)
    {
        context.HandleResponse();

        var response = context.Response;
        response.StatusCode = StatusCodes.Status401Unauthorized;
        response.Headers.WWWAuthenticate = context.AuthenticateFailure is null ? "Bearer" : "Bearer error=\"invalid_token\"";

        var problemDetails = context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>();
        await problemDetails.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = context.HttpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Sign in to continue.",
                Extensions = { [ApiProblem.CodeKey] = ApiProblem.Unauthenticated },
            },
        });
    }
}
