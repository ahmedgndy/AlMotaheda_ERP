using AlMotaheda.BuildingBlocks.Modules;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Finance.Bank;
using AlMotaheda.Modules.Finance.Common;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Finance.Data;
using AlMotaheda.Modules.Finance.Features.Custody;
using AlMotaheda.Modules.Finance.Features.Ledger;
using AlMotaheda.Modules.Finance.Features.PaymentRequests;
using AlMotaheda.Modules.Finance.Features.Payroll;
using AlMotaheda.Modules.Finance.Features.Treasury;
using AlMotaheda.Modules.Finance.Integration;
using AlMotaheda.Modules.Finance.Requests;
using AlMotaheda.Modules.Finance.Startup;
using AlMotaheda.Modules.Reports.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Finance;

/// <summary>
/// Entry point of the Finance module: payment requests for every payee, the Executive's decision, the bank step and
/// the ledger (docs/modernization/07-finance-design.md). This is the only public type in the module.
/// </summary>
/// <remarks>
/// The source modules create requests and write paid payments through <c>AlMotaheda.Modules.Finance.Contracts</c>
/// (<see cref="IPaymentRequests"/>, <see cref="IPaymentSource"/>). Finance uses the Access module only through
/// <c>Access.Contracts</c> (the permission re-check, user names) and the Projects module only through
/// <c>Projects.Contracts</c> (which projects a caller may see).
/// </remarks>
public sealed class FinanceModule : IModule
{
    public string Name => "Finance";

    public void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<FinanceDbContext>(configuration, FinanceDatabase.Configure);

        services.AddOptions<BankOptions>().Bind(configuration.GetSection(BankOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<BankOptions>, BankOptionsValidator>();
        services.AddSingleton<IBankPaymentProvider, MockBankPaymentProvider>();

        services.AddOptions<FinanceOptions>().Bind(configuration.GetSection(FinanceOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<FinanceOptions>, FinanceOptionsValidator>();

        services.AddCompanyClock();
        services.AddSingleton<FinanceLog>();
        services.AddSingleton<CustodyBalancePolicy>();
        services.AddScoped<PaymentRequestViews>();
        services.AddScoped<PaymentRequestWorkflow>();
        services.AddScoped<IPaymentRequests, PaymentRequestsContract>();
        services.AddScoped<IReportSource, FinanceReports>();

        // The treasury and the custody hierarchy.
        services.AddScoped<CustodyLedgers>();
        services.AddScoped<CustodyAccountViews>();
        services.AddScoped<CustodyTransferWorkflow>();
        services.AddScoped<PayrollBatchRules>();
        services.AddScoped<PayrollBatchViews>();
        services.AddScoped<IReportSource, CustodyReports>();
        services.AddScoped<Projects.Contracts.IWorkInboxSource, Integration.FinanceInboxSource>();
        services.AddScoped<Projects.Contracts.IPersonalOverviewSource, Integration.FinancePersonalOverview>();

        services.AddHostedService<FinanceStartup>();
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        // Every endpoint of this module needs a signed-in user, whatever the host adds.
        var api = endpoints.MapGroup("").WithTags(Name).RequireAuthorization();

        ListPaymentRequests.Map(api);
        GetPaymentRequest.Map(api);
        MovePaymentRequest.Map(api);
        ListLedgerEntries.Map(api);

        TreasuryEndpoints.Map(api);
        CustodyAccountEndpoints.Map(api);
        CustodyControlEndpoints.Map(api);
        CustodyTransferEndpoints.Map(api);
        PayrollBatchEndpoints.Map(api);
        CustodyMonitorEndpoints.Map(api);
    }
}
