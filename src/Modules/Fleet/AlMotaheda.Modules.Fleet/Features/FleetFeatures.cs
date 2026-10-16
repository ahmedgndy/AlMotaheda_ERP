using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Features.Assignments;
using AlMotaheda.Modules.Fleet.Features.Costs;
using AlMotaheda.Modules.Fleet.Features.Register;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Fleet.Features;

/// <summary>
/// Wires the Fleet module's features: one line per use case. See docs/project-workspace/05-fleet.md.
/// </summary>
internal static class FleetFeatures
{
    public static void AddServices(IServiceCollection services)
    {
        // Stateless: the logger it wraps is a singleton too.
        services.AddSingleton<FleetLog>();
    }

    /// <param name="api">The shared <c>/api</c> group. Routes under one project start with <c>/projects/{projectId:int}</c>.</param>
    public static void Map(IEndpointRouteBuilder api)
    {
        // The company register.
        ListEquipment.Map(api);
        GetEquipment.Map(api);
        CreateEquipment.Map(api);
        UpdateEquipment.Map(api);
        AddEquipmentRental.Map(api);

        // Machines on a project, over time.
        ListProjectEquipment.Map(api);
        AssignEquipment.Map(api);
        EndEquipmentAssignment.Map(api);

        // Equipment costs of a project.
        ListEquipmentCosts.Map(api);
        GetEquipmentCost.Map(api);
        CreateEquipmentCost.Map(api);
        UpdateEquipmentCost.Map(api);
        ApproveEquipmentCost.Map(api);
        ReturnEquipmentCost.Map(api);
        RejectEquipmentCost.Map(api);
        RequestEquipmentCostAdjustment.Map(api);
        DecideEquipmentCostAdjustment.Map(api);
        RecordEquipmentCostPayment.Map(api);
        VoidEquipmentCostPayment.Map(api);
        ListVoidedEquipmentPayments.Map(api);
        GetEquipmentSummary.Map(api);
    }
}
