using AlMotaheda.Modules.Subcontractors.Common;
using AlMotaheda.Modules.Subcontractors.Features.Companies;
using AlMotaheda.Modules.Subcontractors.Features.ContractItems;
using AlMotaheda.Modules.Subcontractors.Features.Contracts;
using AlMotaheda.Modules.Subcontractors.Features.Payments;
using AlMotaheda.Modules.Subcontractors.Features.Work;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Subcontractors.Features;

/// <summary>
/// Wires the Subcontractors module's features: one line per use case. See docs/project-workspace/04-subcontractors.md.
/// </summary>
internal static class SubcontractorsFeatures
{
    public static void AddServices(IServiceCollection services)
    {
        services.AddSingleton<SubcontractorsLog>();

        // Reads the database and asks Access and Projects for names, so scoped.
        services.AddScoped<ContractDetails>();
    }

    /// <param name="api">The shared <c>/api</c> group. Routes under one project start with <c>/projects/{projectId:int}</c>.</param>
    public static void Map(IEndpointRouteBuilder api)
    {
        ListSubcontractors.Map(api);
        GetSubcontractor.Map(api);
        CreateSubcontractor.Map(api);
        UpdateSubcontractor.Map(api);

        ListSubcontracts.Map(api);
        GetSubcontract.Map(api);
        CreateSubcontract.Map(api);
        UpdateSubcontract.Map(api);

        CreateContractItem.Map(api);
        UpdateContractItem.Map(api);
        DeleteContractItem.Map(api);
        LinkContractItem.Map(api);

        RecordMeasurement.Map(api);
        ApproveMeasurement.Map(api);
        RejectMeasurement.Map(api);

        RecordManpower.Map(api);
        ApproveManpower.Map(api);
        RejectManpower.Map(api);
        LinkManpowerItem.Map(api);

        RecordSubPayment.Map(api);
        VoidSubPayment.Map(api);

        Variations.RequestVariation.Map(api);
        Variations.ApproveVariation.Map(api);
        Variations.RejectVariation.Map(api);
        Accounts.GetSubcontractorAccount.Map(api);
    }
}
