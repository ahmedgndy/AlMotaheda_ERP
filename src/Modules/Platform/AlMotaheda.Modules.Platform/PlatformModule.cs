using AlMotaheda.BuildingBlocks.Data;
using AlMotaheda.BuildingBlocks.Modules;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Platform.DatabaseBackup;
using AlMotaheda.Modules.Platform.Common;
using AlMotaheda.Modules.Platform.Data;
using AlMotaheda.Modules.Platform.Features.Audit;
using AlMotaheda.Modules.Platform.Features.Devices;
using AlMotaheda.Modules.Platform.Features.Notifications;
using AlMotaheda.Modules.Platform.Live;
using AlMotaheda.Modules.Platform.Notifications;
using AlMotaheda.Modules.Platform.Outbox;
using AlMotaheda.Modules.Platform.Push;
using AlMotaheda.Modules.Platform.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Platform;

/// <summary>
/// Entry point of the Platform module: the audit trail, the outbox, the stored notifications and the live-update hub
/// (SignalR, docs/modernization/16-signalr-changes.md).
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

        // "Waits for your review" recipients: permission holders who may see the project (docs/cost-flow §2.8).
        services.AddScoped<ReviewRecipients>();

        // Live updates: signals only (no amounts), sent after commit by the outbox dispatcher.
        services.AddSignalR(hub => hub.MaximumReceiveMessageSize = 4 * 1024);
        services.AddSingleton<ILiveUpdates, SignalRLiveUpdates>();

        // Push notifications (Firebase Cloud Messaging): off until a service-account file is configured.
        services.AddOptions<PushOptions>().Bind(configuration.GetSection(PushOptions.SectionName));
        services.AddHttpClient(FcmPushSender.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));
        services.AddSingleton<IPushSender, FcmPushSender>();
        services.AddScoped<PushDelivery>();

        // Cache removal: the same dependency map as the live signals.
        services.AddSingleton<IChangeObserver, CacheInvalidation>();

        // Database backup (Windows): daily schedule, "Run now", verification, retention, failure notification.
        services.AddOptions<BackupOptions>().Bind(configuration.GetSection(BackupOptions.SectionName));
        services.AddSingleton<BackupSignal>();
        services.AddSingleton<IBackupFiles, LocalBackupFiles>();
        services.AddScoped<BackupRunner>();

        // Order matters: hosted services start in registration order, so the migrations run before the dispatcher.
        services.AddHostedService<PlatformStartup>();
        services.AddHostedService<OutboxDispatcher>();
        services.AddHostedService<BackupService>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Every endpoint of this module needs a signed-in user, whatever the host adds.
        var api = endpoints.MapGroup("").WithTags(Name).RequireAuthorization();

        ListNotifications.Map(api);
        GetUnreadNotificationCount.Map(api);
        MarkNotificationsRead.Map(api);
        RegisterDevice.Map(api);
        UnregisterDevice.Map(api);
        ListAuditEntries.Map(api);
        BackupEndpoints.Map(api);

        // The connection closes when its access token expires: the client reconnects with a fresh token and every
        // join is checked again, so a removed permission or project stops the signals within the token's lifetime.
        api.MapHub<ErpHub>("/hubs/erp", hub => hub.CloseOnAuthenticationExpiration = true);
    }
}
