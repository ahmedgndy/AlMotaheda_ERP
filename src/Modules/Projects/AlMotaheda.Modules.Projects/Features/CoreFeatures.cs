using AlMotaheda.Modules.Projects.Features.Expenses;
using AlMotaheda.Modules.Projects.Features.Items;
using AlMotaheda.Modules.Projects.Features.SupplierPayments;
using AlMotaheda.Modules.Projects.Features.Suppliers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Projects.Features;

/// <summary>
/// Wires the first part of the project workspace: suppliers, project items, expenses and supplies, and supplier
/// payments. See docs/project-workspace/01-projects-core.md.
/// </summary>
internal static class CoreFeatures
{
    public static void AddServices(IServiceCollection services)
    {
        // The items ledger is loaded per request (it reads the database and asks the other modules), so scoped.
        services.AddScoped<ItemsLedgerSource>();
        services.AddScoped<ItemDetails>();
        services.AddScoped<IProjectItemsSummary, ProjectItemsSummaryProvider>();
        services.AddScoped<CostAlerts>();
    }

    /// <param name="api">The shared <c>/api</c> group, for company-wide routes such as <c>/suppliers</c>.</param>
    /// <param name="projects">The <c>/api/projects</c> group, for routes under one project.</param>
    public static void Map(IEndpointRouteBuilder api, IEndpointRouteBuilder projects)
    {
        ListSuppliers.Map(api);
        CreateSupplier.Map(api);
        UpdateSupplier.Map(api);

        ListProjectItems.Map(projects);
        ListProjectItemOptions.Map(projects);
        GetUnallocatedItems.Map(projects);
        GetProjectItem.Map(projects);
        GetItemCostBreakdown.Map(projects);
        CreateProjectItem.Map(projects);
        UpdateProjectItem.Map(projects);
        MoveProjectItem.Map(projects);
        DeactivateProjectItem.Map(projects);
        ReactivateProjectItem.Map(projects);
        DeleteProjectItem.Map(projects);

        ListExpenses.Map(projects);
        GetExpense.Map(projects);
        CreateExpense.Map(projects);
        UpdateExpense.Map(projects);
        ApproveExpense.Map(projects);
        ReturnExpense.Map(projects);
        RejectExpense.Map(projects);
        LinkExpenseItem.Map(projects);
        RequestExpenseAdjustment.Map(projects);
        DecideExpenseAdjustment.Map(projects);

        ListSupplierPayments.Map(projects);
        RecordSupplierPayment.Map(projects);
        VoidSupplierPayment.Map(projects);
        GetSupplierBalances.Map(projects);
    }
}
