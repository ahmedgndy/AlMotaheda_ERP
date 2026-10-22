using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Features.Assignments;
using AlMotaheda.Modules.Fleet.Features.Attachments;
using AlMotaheda.Modules.Fleet.Features.Claims;
using AlMotaheda.Modules.Fleet.Features.Costs;
using AlMotaheda.Modules.Fleet.Features.Gps;
using AlMotaheda.Modules.Fleet.Features.Register;
using AlMotaheda.Modules.Fleet.Features.Vehicles;
using AlMotaheda.Modules.Fleet.Features.Violations;
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

        // Vehicles: views with their lookups.
        services.AddScoped<VehicleViews>();
        services.AddScoped<VehicleExpenseViews>();
        services.AddScoped<ViolationViews>();

        // Vehicle GPS from the driver's phone: ingestion, the monitoring views, the stale alerts.
        services.AddScoped<DriverTracking>();
        services.AddScoped<FleetMonitor>();
        services.AddScoped<VehicleTrackingAlerts>();
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

        // Vehicles: register, drivers, custody, evidence, claims, violations, GPS.
        VehicleEndpoints.Map(api);
        FleetAttachmentEndpoints.Map(api);
        VehicleExpenseEndpoints.Map(api);
        VehicleExpenseReview.Map(api);
        VehicleViolationEndpoints.Map(api);
        VehicleLocationEndpoints.Map(api);
        DriverTrackingEndpoints.Map(api);
    }
}
