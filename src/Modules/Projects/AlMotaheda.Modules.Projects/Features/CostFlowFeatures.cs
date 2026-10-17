using AlMotaheda.Modules.Projects.Features.Items;
using AlMotaheda.Modules.Projects.Features.MasterCatalog;
using AlMotaheda.Modules.Projects.Features.PriceVariances;
using AlMotaheda.Modules.Projects.Features.Suppliers;
using Microsoft.AspNetCore.Routing;

namespace AlMotaheda.Modules.Projects.Features;

/// <summary>
/// Wires the cost-flow features (docs/cost-flow/01-inventory-and-design.md): the company master catalog and its
/// controlled prices, project selection, price variances, item components and the supplier profile.
/// </summary>
internal static class CostFlowFeatures
{
    /// <param name="api">The shared <c>/api</c> group.</param>
    /// <param name="projects">The <c>/api/projects</c> group, for routes under one project.</param>
    public static void Map(IEndpointRouteBuilder api, IEndpointRouteBuilder projects)
    {
        ListMasterItems.Map(api);
        GetMasterItem.Map(api);
        CreateMasterItem.Map(api);
        UpdateMasterItem.Map(api);
        RequestMasterPriceChange.Map(api);
        ListMasterPriceChanges.Map(api);
        DecideMasterPriceChange.Map(api);

        GetProjectSelection.Map(projects);
        SelectMasterItems.Map(projects);

        GetPricingPolicy.Map(api);
        UpdatePricingPolicy.Map(api);
        ListPriceVariances.Map(api);
        ListProjectPriceVariances.Map(projects);
        DecidePriceVariance.Map(projects);

        SetItemComponents.Map(projects);

        GetSupplier.Map(api);
        GetSupplierHistory.Map(api);
    }
}
