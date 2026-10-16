using AlMotaheda.BuildingBlocks.Modules;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Platform.Common;
using AlMotaheda.Modules.Platform.Data;
using AlMotaheda.Modules.Platform.Features.Audit;
using AlMotaheda.Modules.Platform.Features.Notifications;
using AlMotaheda.Modules.Platform.Outbox;
using AlMotaheda.Modules.Platform.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Platform;

/// <summary>
/// Entry point of the Platform module: the audit trail, the outbox and the stored notifications.
/// This is the only public type in the module.
/// </summary>
/// <remarks>
/// Every other module writes audit entries and outbox messages through <c>AuditTrailInterceptor</c>
/// (BuildingBlocks.Data), in its own transaction. This module owns those tables, handles the outbox after the
/// commit, and serves the notifications and the audit trail. It uses the Access module only through
/// <c>AlMotaheda.Modules.Access.Contracts</c> (user names, the Executive role code).
/// </remarks>
public sealed class PlatformModule : IModule
{
    public string Name => "Platform";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        // Not audited: this module owns the audit and outbox tables, and marking a notification read isn't a business change.
        services.AddModuleDbContext<PlatformDbContext>(configuration, PlatformDatabase.Configure, audited: false);

        services.AddOptions<OutboxOptions>().Bind(configuration.GetSection(OutboxOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<OutboxOptions>, OutboxOptionsValidator>();

        services.AddCompanyClock();
        services.AddSingleton<PlatformLog>();

        // Order matters: hosted services start in registration order, so the migrations run before the dispatcher.
        services.AddHostedService<PlatformStartup>();
        services.AddHostedService<OutboxDispatcher>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Every endpoint of this module needs a signed-in user, whatever the host adds.
        var api = endpoints.MapGroup("").WithTags(Name).RequireAuthorization();

        ListNotifications.Map(api);
        GetUnreadNotificationCount.Map(api);
        MarkNotificationsRead.Map(api);
        ListAuditEntries.Map(api);
    }
}
