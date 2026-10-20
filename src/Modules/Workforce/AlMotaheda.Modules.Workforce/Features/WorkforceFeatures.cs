using AlMotaheda.Modules.Workforce.Features.Assignments;
using AlMotaheda.Modules.Workforce.Features.Ledger;
using AlMotaheda.Modules.Workforce.Features.Payments;
using AlMotaheda.Modules.Workforce.Features.Sheets;
using AlMotaheda.Modules.Workforce.Features.Trades;
using AlMotaheda.Modules.Workforce.Features.Workers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Workforce.Features;

/// <summary>
/// Wires the Workforce module's features: one line per use case. See docs/project-workspace/03-workforce.md.
/// </summary>
internal static class WorkforceFeatures
{
    public static void AddServices(IServiceCollection services)
    {
        // The ledger reads the database per request, so scoped. The contracts the Projects module calls use it.
        services.AddScoped<LaborLedgerSource>();
    }

    /// <param name="api">The shared <c>/api</c> group. Routes under one project start with <c>/projects/{projectId:int}</c>.</param>
    public static void Map(IEndpointRouteBuilder api)
    {
        ListTrades.Map(api);
        CreateTrade.Map(api);
        UpdateTrade.Map(api);

        ListWorkers.Map(api);
        GetWorker.Map(api);
        CreateWorker.Map(api);
        UpdateWorker.Map(api);

        ListProjectWorkers.Map(api);
        AssignWorker.Map(api);
        EndAssignment.Map(api);

        ListSheets.Map(api);
        GetSheet.Map(api);
        CreateSheet.Map(api);
        UpdateSheet.Map(api);
        SubmitSheet.Map(api);
        ApproveSheet.Map(api);
        ReturnSheet.Map(api);
        RejectSheet.Map(api);
        SetSheetAllocations.Map(api);
        SetLineAllocations.Map(api);
        GetLaborSummary.Map(api);

        GetWorkerAccount.Map(api);
        ListWorkerPayments.Map(api);
        RecordWorkerPayment.Map(api);
        VoidWorkerPayment.Map(api);

        Settlements.SettlementEndpoints.Map(api);
    }
}
