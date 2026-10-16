using AlMotaheda.Modules.Procurement.Common;
using AlMotaheda.Modules.Procurement.Features.Invoices;
using AlMotaheda.Modules.Procurement.Features.Orders;
using AlMotaheda.Modules.Procurement.Features.Policy;
using AlMotaheda.Modules.Procurement.Features.Quotations;
using AlMotaheda.Modules.Procurement.Features.Receipts;
using AlMotaheda.Modules.Procurement.Features.Requisitions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace AlMotaheda.Modules.Procurement.Features;

/// <summary>Wires the Procurement module's features. See docs/modernization/12-procurement-design.md.</summary>
internal static class ProcurementFeatures
{
    public static void AddServices(IServiceCollection services)
    {
        services.AddSingleton<ProcurementLog>();
        services.AddScoped<ProcurementViews>();
    }

    /// <param name="api">The shared <c>/api</c> group. Every route starts with <c>/procurement</c>.</param>
    public static void Map(IEndpointRouteBuilder api)
    {
        ProcurementPolicyEndpoints.Map(api);

        CreateRequisition.Map(api);
        ReadRequisitions.Map(api);
        DecideRequisition.Map(api);
        GetRequisitionChain.Map(api);

        QuotationEndpoints.Map(api);

        ReadOrders.Map(api);
        CreateOrder.Map(api);
        ChangeOrder.Map(api);

        ReceiptEndpoints.Map(api);

        ReadInvoices.Map(api);
        InvoiceEndpoints.Map(api);
    }
}
