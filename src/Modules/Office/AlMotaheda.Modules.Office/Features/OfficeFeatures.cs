using AlMotaheda.Modules.Office.Common;
using AlMotaheda.Modules.Office.Features.Alerts;
using AlMotaheda.Modules.Office.Features.Attachments;
using AlMotaheda.Modules.Office.Features.CashCounts;
using AlMotaheda.Modules.Office.Features.Custody;
using AlMotaheda.Modules.Office.Features.Expenses;
using AlMotaheda.Modules.Office.Features.Funding;
using AlMotaheda.Modules.Office.Features.Reports;
using AlMotaheda.Modules.Office.Features.SpendRequests;
using AlMotaheda.Modules.Office.Storage;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Office.Features;

/// <summary>Wires the Office module's features. See the module README.</summary>
internal static class OfficeFeatures
{
    public static void AddServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OfficeOptions>().Bind(configuration.GetSection(OfficeOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<OfficeOptions>, OfficeOptionsValidator>();

        services.AddOptions<OfficeStorageOptions>().Bind(configuration.GetSection(OfficeStorageOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IPostConfigureOptions<OfficeStorageOptions>, OfficeStorageOptionsSetup>();
        services.AddSingleton<IValidateOptions<OfficeStorageOptions>, OfficeStorageOptionsValidator>();

        services.AddSingleton<IOfficeFileStore, LocalOfficeFileStore>();
        services.AddSingleton<OfficeLog>();
        services.AddScoped<OfficeAlerts>();
    }

    /// <param name="api">The shared <c>/api</c> group. Every route starts with <c>/office</c>.</param>
    public static void Map(IEndpointRouteBuilder api)
    {
        OfficeOverviewEndpoints.Map(api);
        OfficeCustodyEndpoints.Map(api);
        OfficeAttachmentEndpoints.Map(api);
        OfficePriceEndpoints.Map(api);
        OfficeExpenseEndpoints.Map(api);
        OfficeFundingEndpoints.Map(api);
        OfficeSpendRequestEndpoints.Map(api);
        OfficeCashCountEndpoints.Map(api);
    }
}
