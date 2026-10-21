using System.Text.Json.Nodes;

namespace AlMotaheda.DemoSeed;

internal sealed partial class Demo
{
    /// <summary>
    /// Price proposals (a range is never edited: a new one is proposed and an Executive decides), and the purchasing
    /// chain: with auto-approval off, the site engineer's requisition is approved by the project manager and then the
    /// Accountant; the Executive asks for quotations and selects one; Purchasing issues the order; the engineer
    /// receives part of it on site; the Accountant records the supplier's invoice for what arrived.
    /// </summary>
    private async Task PricesAndProcurementAsync()
    {
        Log.Section("Price proposals and procurement");
        await RangeProposalAsync("rebar", 39000, 42500, "ارتفاع أسعار البليت عالميًا / World billet prices went up", approve: true);
        await RangeProposalAsync("cement", 230, 265, "زيادة رسوم النقل من المصنع / Higher haulage from the works", approve: false);

        var ceo = As(Cast.Ceo);
        var policy = await ceo.GetAsync("/api/procurement/policy");
        if ((bool?)policy["autoApproveRequisitions"] == true)
        {
            await ceo.PutAsync("/api/procurement/policy", new { autoApproveRequisitions = false, priceTolerancePercent = (decimal?)policy["priceTolerancePercent"] ?? 0m, rowVersion = policy.RowVersion() });
            Log.New("procurement policy: requisitions need the manager's and the Accountant's approval");
        }
        else
        {
            Log.Kept("procurement policy: approvals on");
        }

        // The list rows carry no notes: read each requisition to find the seed's own by its marker.
        var requisitions = new List<JsonNode>();
        foreach (var row in (await ceo.GetAsync("/api/procurement/requisitions?pageSize=100")).Items())
        {
            var detail = await ceo.GetAsync($"/api/procurement/requisitions/{row.Id()}");
            requisitions.Add(detail["requisition"] ?? detail);
        }

        // The full chain for 300 bags of cement on the Nile Tower.
        var full = await RequisitionAsync(requisitions, Sites.NileTower, Cast.Engineer, "pr-cement",
            "أسمنت لأعمال مباني الدور الرابع / Cement for the fourth-floor masonry", "Bag", 300, "nt.masonry", "cement", until: "Approved");
        await ChainAsync(full);

        // Waiting for the project manager.
        await RequisitionAsync(requisitions, Sites.NileTower, Cast.Engineer, "pr-membrane",
            "لفائف عزل بيتوميني 4 مم / 4 mm bitumen membrane rolls", "Roll", 50, "nt.waterproofing", null, until: "Submitted");

        // Approved by the manager, waiting for the Accountant.
        await RequisitionAsync(requisitions, Sites.ShoroukSchools, Cast.Engineer2, "pr-bricks",
            "طوب أحمر لمبنى ب / Red bricks for building B", "Brick", 10000, "sh.masonry", "bricks", until: "ManagerApproved");
    }

    private async Task RangeProposalAsync(string item, decimal min, decimal max, string reason, bool approve)
    {
        var detail = await As(Cast.Ceo).GetAsync($"/api/price-items/{_priceItemIds[item]}");
        var ranges = detail.Items("ranges").ToList();
        var proposal = ranges.FirstOrDefault(range => (decimal?)range["minPrice"] == min && (decimal?)range["maxPrice"] == max);
        if (proposal is null)
        {
            proposal = await As(Cast.Manager).PostAsync($"/api/price-items/{_priceItemIds[item]}/ranges", new
            {
                minPrice = min,
                maxPrice = max,
                currency = "EGP",
                effectiveFrom = Iso(today.AddDays(approve ? -15 : 1)),
                kind = "Range",
                reason = $"{Marker} {reason}",
                sourceNote = "عرض أسعار من موردين اثنين / Quotes from two suppliers",
            });
            proposal = proposal["range"] ?? proposal;
            Log.New($"price proposal {item} {min:N0}–{max:N0}: {proposal.Str("status")}");
        }

        if (approve && proposal.Str("status") == "Pending")
        {
            await As(Cast.Ceo).PostAsync($"/api/price-ranges/{proposal.Id()}/approve", new { note = $"{Marker} market prices checked" });
            Log.New($"price proposal {item}: Pending -> Approved (the old range is kept as history)");
        }
        else
        {
            Log.Kept($"price proposal {item} {min:N0}–{max:N0}: {proposal.Str("status")}");
        }
    }

    private async Task<JsonNode> RequisitionAsync(List<JsonNode> existing, ProjectSpec project, Person engineer, string key, string description, string unit, decimal quantity, string item, string? priceItem, string until)
    {
        var requisition = existing.Where(row => row.Str("notes") == $"{Marker} {key}").OrderBy(row => row.Id()).LastOrDefault();
        if (requisition is null)
        {
            requisition = await As(engineer).PostAsync("/api/procurement/requisitions", new
            {
                projectId = ProjectId(project),
                neededBy = Iso(today.AddDays(10)),
                notes = $"{Marker} {key}",
                lines = new[] { new { projectItemId = _itemIds[item], description, unit, quantity, priceItemId = priceItem is null ? (int?)null : _priceItemIds[priceItem] } },
            });
            requisition = requisition["requisition"] ?? requisition;
            Log.New($"requisition {requisition.Str("number")} {description}: {requisition.Str("status")}");
        }

        var path = $"/api/procurement/requisitions/{requisition.Id()}";
        if (requisition.Str("status") == "Submitted" && until is "ManagerApproved" or "Approved")
        {
            requisition = await As(Cast.Manager).PostAsync($"{path}/approve", new { note = $"{Marker} needed this week", rowVersion = requisition.RowVersion() });
            requisition = requisition["requisition"] ?? requisition;
            Log.New($"requisition {requisition.Str("number")}: manager approved -> {requisition.Str("status")}");
        }

        if (requisition.Str("status") == "ManagerApproved" && until == "Approved")
        {
            requisition = await As(Cast.Accountant).PostAsync($"{path}/approve", new { note = $"{Marker} within the budget", rowVersion = requisition.RowVersion() });
            requisition = requisition["requisition"] ?? requisition;
            Log.New($"requisition {requisition.Str("number")}: Accountant approved -> {requisition.Str("status")}");
        }

        return requisition;
    }

    /// <summary>Quotations, selection, order, a partial receipt and the invoice for what arrived.</summary>
    private async Task ChainAsync(JsonNode requisition)
    {
        var ceo = As(Cast.Ceo);
        var purchasing = As(Cast.Purchasing);
        var chain = await ceo.GetAsync($"/api/procurement/requisitions/{requisition.Id()}/chain");
        var line = (await ceo.GetAsync($"/api/procurement/requisitions/{requisition.Id()}")) is var details ? (details["requisition"] ?? details).Items("lines").First() : null!;

        var rfq = chain["quotationRequest"] ?? chain["rfq"];
        if (rfq is null || rfq.GetValueKind() == System.Text.Json.JsonValueKind.Null)
        {
            if (requisition.Str("status") != "Approved")
            {
                Log.Kept($"requisition {requisition.Str("number")}: {requisition.Str("status")} (no chain yet)");
                return;
            }

            rfq = await ceo.PostAsync($"/api/procurement/requisitions/{requisition.Id()}/quotation-request", new { quotationsWanted = 2, note = $"{Marker} two quotes please" });
            rfq = rfq["request"] ?? rfq;
            Log.New($"quotation request {rfq.Str("number")}");
        }

        var rfqPath = $"/api/procurement/quotation-requests/{rfq.Id()}";
        rfq = await ceo.GetAsync(rfqPath);
        rfq = rfq["request"] ?? rfq;
        var quotations = rfq.Items("quotations").ToList();
        foreach (var (supplier, price, days) in new[] { ("cement", 238m, 2), ("readymix", 246m, 5) })
        {
            if (quotations.Any(quote => (int?)quote["supplier"]?["id"] == _supplierIds[supplier]) || rfq.Str("status") != "Open")
            {
                continue;
            }

            await purchasing.PostAsync($"{rfqPath}/quotations", new
            {
                supplierId = _supplierIds[supplier],
                supplierReference = $"Q-DEMO-{supplier}",
                deliveryDays = days,
                paymentTerms = "30 يومًا / 30 days",
                validUntil = Iso(today.AddDays(14)),
                notes = Marker,
                lines = new[] { new { requisitionLineId = line.Id(), unitPrice = price } },
            });
            Log.New($"quotation from {supplier} at {price:N2}");
        }

        rfq = await ceo.GetAsync(rfqPath);
        rfq = rfq["request"] ?? rfq;
        var cheapest = rfq.Items("quotations").OrderBy(quote => (decimal?)quote["total"] ?? 0).First();
        if (rfq.Str("status") == "Open")
        {
            rfq = await purchasing.PostAsync($"{rfqPath}/recommend", new { quotationId = cheapest.Id(), reason = "الأقل سعرًا والأسرع توريدًا / Cheapest and fastest", rowVersion = rfq.RowVersion() });
            rfq = rfq["request"] ?? rfq;
            rfq = await purchasing.PostAsync($"{rfqPath}/submit", new { note = Marker, rowVersion = rfq.RowVersion() });
            rfq = rfq["request"] ?? rfq;
            Log.New($"quotations submitted to the Executive ({rfq.Str("status")})");
        }

        if (rfq.Str("status") == "Submitted")
        {
            rfq = await ceo.PostAsync($"{rfqPath}/select", new { quotationId = cheapest.Id(), note = $"{Marker} as recommended", rowVersion = rfq.RowVersion() });
            rfq = rfq["request"] ?? rfq;
            Log.New($"quotation selected ({rfq.Str("status")})");
        }

        var orders = (await ceo.GetAsync($"/api/procurement/purchase-orders?projectId={ProjectId(Sites.NileTower)}&pageSize=50")).Items().ToList();
        var order = orders.FirstOrDefault(row => (int?)row["requisitionId"] == requisition.Id());
        if (order is null)
        {
            order = await purchasing.PostAsync("/api/procurement/purchase-orders", new
            {
                requisitionId = requisition.Id(),
                supplierId = (int)rfq.Items("quotations").Single(quote => quote.Id() == ((int?)rfq["selectedQuotationId"] ?? cheapest.Id()))["supplier"]!["id"]!,
                expectedDelivery = Iso(today.AddDays(3)),
                paymentTerms = "30 يومًا / 30 days",
                notes = Marker,
                lines = new[] { new { requisitionLineId = line.Id(), quantity = (decimal)line["quantity"]!, unitPrice = 238m } },
            });
            order = order["order"] ?? order;
            Log.New($"purchase order {order.Str("number")}: {order.Str("status")}");
        }

        order = await ceo.GetAsync($"/api/procurement/purchase-orders/{order.Id()}");
        order = order["order"] ?? order;
        var orderLine = order.Items("lines").First();
        if (order.Str("status") == "Issued")
        {
            await As(Cast.Engineer).PostAsync($"/api/procurement/purchase-orders/{order.Id()}/receipts", new
            {
                receivedOn = Iso(today.AddDays(-1)),
                deliveryNoteNo = "WC-DN-5690",
                notes = $"{Marker} first truck: 200 bags",
                lines = new[] { new { orderLineId = orderLine.Id(), quantity = 200m } },
            });
            Log.New("goods receipt: 200 of 300 bags");
        }
        else
        {
            Log.Kept($"purchase order {order.Str("number")}: {order.Str("status")}");
        }

        var invoices = (await ceo.GetAsync($"/api/procurement/supplier-invoices?projectId={ProjectId(Sites.NileTower)}&pageSize=50")).Items().ToList();
        if (!invoices.Any(row => row.Str("invoiceNo") == "WC-INV-1101"))
        {
            var invoice = await As(Cast.Accountant).PostAsync($"/api/procurement/purchase-orders/{order.Id()}/invoices", new
            {
                invoiceNo = "WC-INV-1101",
                invoiceDate = Iso(today),
                deliveryNoteNo = "WC-DN-5690",
                lines = new[] { new { orderLineId = orderLine.Id(), quantity = 200m, unitPrice = 238m } },
            });
            invoice = invoice["invoice"] ?? invoice;
            Log.New($"supplier invoice WC-INV-1101 (200 bags, 3-way match): {invoice.Str("status")}");
        }
        else
        {
            Log.Kept("supplier invoice WC-INV-1101");
        }
    }
}
