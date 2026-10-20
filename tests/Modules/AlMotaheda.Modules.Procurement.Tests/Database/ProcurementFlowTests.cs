using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database.Core;
using Microsoft.EntityFrameworkCore;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Core.CoreApi;

namespace AlMotaheda.Modules.Procurement.Tests.Database;

/// <summary>
/// The procurement chain on SQL Server: a requisition approved by the policy, quotations on the Executive's request,
/// an order from the selected quotation, partial receipts on site, invoices matched (or resolved) by the Accountant,
/// posted once as the project's supplier cost and paid through the existing supplier payment request.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ProcurementFlowTests(SqlServerFixture database)
{
    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");

    [DatabaseFact]
    public async Task A_requisition_goes_all_the_way_to_a_paid_supplier_cost_and_is_counted_once()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("PRC-FLOW-01");
        var (_, purchasing) = await api.CreateSignedInUserAsync(cast.Executive, "purchasing");

        // Purchasing manages the same supplier list as the project screens.
        var cemex = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var suez = await api.CreateSupplierAsync(purchasing, "Suez Cement");
        var costItem = (await api.CreateItemAsync(cast.Manager, cast.ProjectId, "Foundations")).Id();
        var cement = (await Ok(api, cast.Manager, HttpMethod.Post, $"/api/projects/{cast.ProjectId}/catalog-items", new
        {
            nameAr = "أسمنت", nameEn = "Portland cement", category = "CementAggregates", unit = "Bag", price = 150m,
            effectiveFrom = DateTime.UtcNow.AddDays(-5).ToString("yyyy-MM-dd"),
        }, HttpStatusCode.Created)).GetProperty("item").Id();

        // The engineer asks; the policy approves at once, and the history says so.
        var requisition = await Ok(api, cast.Engineer, HttpMethod.Post, "/api/procurement/requisitions",
            new { projectId = cast.ProjectId, lines = new[] { new { catalogItemId = cement, projectItemId = costItem, quantity = 100m } } }, HttpStatusCode.Created);
        Assert.Equal("Approved", requisition.GetProperty("status").GetString());
        var approval = requisition.GetProperty("history")[1];
        Assert.Equal(("Approve", "System (Auto-Approval Policy)", "Procurement.AutoApproveRequisitions"),
            (approval.GetProperty("action").GetString(), approval.GetProperty("actorLabel").GetString(), approval.GetProperty("policyName").GetString()));
        Assert.Equal(JsonValueKind.Null, requisition.GetProperty("estimatedTotal").ValueKind); // the engineer has no procurement See money
        var prId = requisition.Id();
        var prLine = requisition.GetProperty("lines")[0].Id();
        Assert.Equal(15_000m, (await Ok(api, cast.Executive, HttpMethod.Get, $"/api/procurement/requisitions/{prId}")).Money("estimatedTotal"));

        // The Executive asks for quotations; Purchasing adds two and submits the set.
        var set = await Ok(api, cast.Executive, HttpMethod.Post, $"/api/procurement/requisitions/{prId}/quotation-request", new { quotationsWanted = 2 }, HttpStatusCode.Created);
        var setPath = $"/api/procurement/quotation-requests/{set.Id()}";
        await Ok(api, purchasing, HttpMethod.Post, $"{setPath}/quotations",
            new { supplierId = cemex, deliveryDays = 2, paymentTerms = "30 days", lines = new[] { new { requisitionLineId = prLine, unitPrice = 148m } } }, HttpStatusCode.Created);
        set = await Ok(api, purchasing, HttpMethod.Post, $"{setPath}/quotations",
            new { supplierId = suez, deliveryDays = 10, lines = new[] { new { requisitionLineId = prLine, unitPrice = 145m } } }, HttpStatusCode.Created);
        var cemexQuote = set.GetProperty("quotations")[0].Id();
        Assert.Equal(set.GetProperty("quotations")[1].Id(), set.GetProperty("cheapestQuotationId").GetInt32());

        // No order before the Executive chooses.
        var early = await api.SendAsync(HttpMethod.Post, "/api/procurement/purchase-orders", purchasing,
            new { requisitionId = prId, lines = new[] { new { requisitionLineId = prLine, quantity = 100m } } });
        Assert.Equal("quotations_pending", await CodeOf(early));

        set = await Ok(api, purchasing, HttpMethod.Post, $"{setPath}/submit", new { rowVersion = set.GetProperty("rowVersion").GetString() });
        var noReason = await api.SendAsync(HttpMethod.Post, $"{setPath}/select", cast.Executive, new { quotationId = cemexQuote, rowVersion = set.GetProperty("rowVersion").GetString() });
        Assert.Equal("reason_required", await CodeOf(noReason));
        set = await Ok(api, cast.Executive, HttpMethod.Post, $"{setPath}/select",
            new { quotationId = cemexQuote, reason = "Delivers in two days", rowVersion = set.GetProperty("rowVersion").GetString() });
        Assert.Equal("Selected", set.GetProperty("status").GetString());

        // The order comes from the selected quotation: its supplier and price, whatever is sent.
        var order = await Ok(api, purchasing, HttpMethod.Post, "/api/procurement/purchase-orders",
            new { requisitionId = prId, supplierId = suez, lines = new[] { new { requisitionLineId = prLine, quantity = 100m, unitPrice = 1m } } }, HttpStatusCode.Created);
        Assert.Equal((cemex, 148m, 14_800m), (order.GetProperty("supplier").Id(), order.GetProperty("lines")[0].Money("unitPrice"), order.Money("total")));
        var orderLine = order.GetProperty("lines")[0].Id();
        Assert.Equal("Ordered", (await Ok(api, cast.Executive, HttpMethod.Get, $"/api/procurement/requisitions/{prId}")).GetProperty("status").GetString());
        Assert.Equal(14_800m, await MaterialsCommittedAsync(api, cast));

        // Receipts on site, by the team; Purchasing can't receive goods.
        var receipt = new { receivedOn = Today, deliveryNoteNo = "DN-1", lines = new[] { new { orderLineId = orderLine, quantity = 60m } } };
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/procurement/purchase-orders/{order.Id()}/receipts", purchasing, receipt)).StatusCode);
        await Ok(api, cast.Engineer, HttpMethod.Post, $"/api/procurement/purchase-orders/{order.Id()}/receipts", receipt, HttpStatusCode.Created);

        // Invoicing more than received is refused outright; a matching invoice is posted at once.
        var invoices = $"/api/procurement/purchase-orders/{order.Id()}/invoices";
        var tooMuch = await api.SendAsync(HttpMethod.Post, invoices, cast.Accountant,
            new { invoiceNo = "C-1001", invoiceDate = Today, lines = new[] { new { orderLineId = orderLine, quantity = 70m, unitPrice = 148m } } });
        Assert.Equal("exceeds_received", await CodeOf(tooMuch));
        var first = await Ok(api, cast.Accountant, HttpMethod.Post, invoices,
            new { invoiceNo = "C-1001", invoiceDate = Today, lines = new[] { new { orderLineId = orderLine, quantity = 60m, unitPrice = 148m } } }, HttpStatusCode.Created);
        Assert.Equal(("Posted", "Unpaid"), (first.GetProperty("status").GetString(), first.GetProperty("paymentState").GetString()));
        var firstRecord = first.GetProperty("lines")[0].GetProperty("postedRecordId").GetInt32();
        Assert.Equal(5_920m, await MaterialsCommittedAsync(api, cast));

        var again = await api.SendAsync(HttpMethod.Post, invoices, cast.Accountant,
            new { invoiceNo = "C-1001", invoiceDate = Today, lines = new[] { new { orderLineId = orderLine, quantity = 1m, unitPrice = 148m } } });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode); // nothing left to invoice, and the number is taken

        // The rest arrives; its invoice is 2 EGP a bag too dear: blocked until the Accountant resolves it.
        await Ok(api, cast.Engineer, HttpMethod.Post, $"/api/procurement/purchase-orders/{order.Id()}/receipts",
            new { receivedOn = Today, lines = new[] { new { orderLineId = orderLine, quantity = 40m } } }, HttpStatusCode.Created);
        Assert.Equal("Received", (await Ok(api, purchasing, HttpMethod.Get, $"/api/procurement/purchase-orders/{order.Id()}")).GetProperty("status").GetString());
        var dear = await Ok(api, cast.Accountant, HttpMethod.Post, invoices,
            new { invoiceNo = "C-1002", invoiceDate = Today, lines = new[] { new { orderLineId = orderLine, quantity = 40m, unitPrice = 150m } } }, HttpStatusCode.Created);
        Assert.Equal("Mismatch", dear.GetProperty("status").GetString());
        Assert.False(dear.GetProperty("lines")[0].GetProperty("priceMatches").GetBoolean());
        var resolved = await Ok(api, cast.Accountant, HttpMethod.Post, $"/api/procurement/supplier-invoices/{dear.Id()}/resolve",
            new { resolution = "CreditNote", reason = "Supplier credits the difference", creditNoteNo = "CN-7", rowVersion = dear.GetProperty("rowVersion").GetString() });
        Assert.Equal("Posted", resolved.GetProperty("status").GetString());
        var secondRecord = resolved.GetProperty("lines")[0].GetProperty("postedRecordId").GetInt32();
        Assert.Equal(0m, await MaterialsCommittedAsync(api, cast));

        // The posted records are ordinary, approved supplier cost on the cost item, at the PO price.
        var records = await api.QueryAsync(db => db.ProjectExpenses.AsNoTracking()
            .Where(expense => expense.Id == firstRecord || expense.Id == secondRecord).OrderBy(expense => expense.Id).ToListAsync());
        Assert.All(records, record => Assert.Equal((ExpenseStatus.Approved, PaymentChannel.Credit, costItem, cemex, "SupplierInvoiceLine"),
            (record.Status, record.Channel, record.ProjectItemId!.Value, record.SupplierId!.Value, record.SourceType)));
        Assert.Equal([8_880m, 5_920m], records.Select(record => record.Amount));

        // Paid through the existing supplier payment request → Executive → paid.
        var request = await api.PayAsync(cast.Accountant, cast.ProjectId, new
        {
            supplierId = cemex, paymentDate = Today, amount = 14_800m, method = "Cash", reference = "RCPT-1", idempotencyKey = Guid.NewGuid().ToString(),
            allocations = new[] { new { expenseId = firstRecord, amount = 8_880m }, new { expenseId = secondRecord, amount = 5_920m } },
        });
        await EnsureStatus(request, HttpStatusCode.Created);
        await api.SettleAsync(cast.Accountant, await Json(request));
        Assert.Equal("Paid", (await Ok(api, cast.Accountant, HttpMethod.Get, $"/api/procurement/supplier-invoices/{first.Id()}")).GetProperty("paymentState").GetString());

        // The whole trail from the requisition.
        var chain = await Ok(api, cast.Manager, HttpMethod.Get, $"/api/procurement/requisitions/{prId}/chain");
        var chainOrder = chain.GetProperty("orders")[0];
        Assert.Equal((2, 2), (chainOrder.GetProperty("receipts").GetArrayLength(), chainOrder.GetProperty("invoices").GetArrayLength()));
        Assert.Equal("Selected", chain.GetProperty("quotationRequest").GetProperty("status").GetString());
    }

    [DatabaseFact]
    public async Task With_the_policy_off_the_project_manager_then_an_accountant_approve_and_nobody_their_own()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync("PRC-FLOW-02");
        var (_, purchasing) = await api.CreateSignedInUserAsync(cast.Executive, "purchasing");

        var policy = await Ok(api, cast.Executive, HttpMethod.Get, "/api/procurement/policy");
        Assert.True(policy.GetProperty("autoApproveRequisitions").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Put, "/api/procurement/policy", purchasing,
            new { autoApproveRequisitions = false, priceTolerancePercent = 0, rowVersion = policy.GetProperty("rowVersion").GetString() })).StatusCode);
        await Ok(api, cast.Executive, HttpMethod.Put, "/api/procurement/policy",
            new { autoApproveRequisitions = false, priceTolerancePercent = 0, rowVersion = policy.GetProperty("rowVersion").GetString() });

        var requisition = await Ok(api, cast.Engineer, HttpMethod.Post, "/api/procurement/requisitions",
            new { projectId = cast.ProjectId, lines = new[] { new { description = "Rebar 12 mm", unit = "Ton", quantity = 5m } } }, HttpStatusCode.Created);
        Assert.Equal("Submitted", requisition.GetProperty("status").GetString());
        var path = $"/api/procurement/requisitions/{requisition.Id()}";

        // Purchasing can't approve (no procurement Approve); the Accountant isn't the first step; the manager is.
        Assert.Equal(HttpStatusCode.Forbidden, (await Step(api, purchasing, path, "approve")).StatusCode);
        Assert.Equal("not_project_manager", await CodeOf(await Step(api, cast.Accountant, path, "approve")));
        var managed = await Step(api, cast.Manager, path, "approve");
        await EnsureStatus(managed, HttpStatusCode.OK);
        Assert.Equal("ManagerApproved", (await Json(managed)).GetProperty("status").GetString());
        Assert.Equal("accountant_step", await CodeOf(await Step(api, cast.Manager, path, "approve")));
        var approved = await Json(await Step(api, cast.Accountant, path, "approve"));
        Assert.Equal("Approved", approved.GetProperty("status").GetString());
        Assert.Equal(["Submit", "ManagerApprove", "AccountantApprove"],
            approved.GetProperty("history").EnumerateArray().Select(step => step.GetProperty("action").GetString()));

        // The manager's own requisition can't be approved by the manager.
        var own = await Ok(api, cast.Manager, HttpMethod.Post, "/api/procurement/requisitions",
            new { projectId = cast.ProjectId, lines = new[] { new { description = "Timber", unit = "M3", quantity = 2m } } }, HttpStatusCode.Created);
        Assert.Equal("own_record", await CodeOf(await Step(api, cast.Manager, $"/api/procurement/requisitions/{own.Id()}", "approve")));

        // Without quotations Purchasing picks the supplier and prices; it may not order more than requested.
        var supplier = await api.CreateSupplierAsync(purchasing, "Ezz Steel");
        var prLine = approved.GetProperty("lines")[0].Id();
        var tooMuch = await api.SendAsync(HttpMethod.Post, "/api/procurement/purchase-orders", purchasing,
            new { requisitionId = requisition.Id(), supplierId = supplier, lines = new[] { new { requisitionLineId = prLine, quantity = 6m, unitPrice = 38_000m } } });
        Assert.Equal("exceeds_requisition", await CodeOf(tooMuch));
        var order = await Ok(api, purchasing, HttpMethod.Post, "/api/procurement/purchase-orders",
            new { requisitionId = requisition.Id(), supplierId = supplier, lines = new[] { new { requisitionLineId = prLine, quantity = 5m, unitPrice = 38_000m } } }, HttpStatusCode.Created);

        // A change is a revision with a reason; the old lines are kept. Nothing received yet, so it can be cancelled.
        var revised = await Ok(api, purchasing, HttpMethod.Post, $"/api/procurement/purchase-orders/{order.Id()}/revise", new
        {
            reason = "Supplier price list changed",
            lines = new[] { new { lineId = order.GetProperty("lines")[0].Id(), quantity = 4m, unitPrice = 37_500m } },
            rowVersion = order.GetProperty("rowVersion").GetString(),
        });
        Assert.Equal((1, 150_000m), (revised.GetProperty("revision").GetInt32(), revised.Money("total")));
        Assert.Contains("38000", revised.GetProperty("revisions")[0].GetProperty("previousLines").GetString());
        Assert.Equal("Approved", (await Ok(api, cast.Executive, HttpMethod.Get, path)).GetProperty("status").GetString()); // 1 ton still needed

        var cancelled = await Ok(api, purchasing, HttpMethod.Post, $"/api/procurement/purchase-orders/{order.Id()}/cancel",
            new { reason = "Ordered by mistake", rowVersion = revised.GetProperty("rowVersion").GetString() });
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());

        // Visibility: Purchasing sees every project's requisitions; another project's engineer sees none of them.
        Assert.Equal(2, (await Ok(api, purchasing, HttpMethod.Get, "/api/procurement/requisitions")).GetProperty("totalCount").GetInt32());
        var (_, stranger) = await api.CreateSignedInUserAsync(cast.Executive, "engineer");
        Assert.Equal(0, (await Ok(api, stranger, HttpMethod.Get, "/api/procurement/requisitions")).GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, path, stranger)).StatusCode);
    }

    private static async Task<JsonElement> Ok(ProjectsApi api, Session session, HttpMethod method, string path, object? body = null, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var response = await api.SendAsync(method, path, session, body);
        await EnsureStatus(response, expected);
        return await Json(response);
    }

    private static async Task<HttpResponseMessage> Step(ProjectsApi api, Session session, string path, string step)
    {
        var current = await Json(await api.SendAsync(HttpMethod.Get, path, (await api.SignInExecutiveAsync())));
        return await api.SendAsync(HttpMethod.Post, $"{path}/{step}", session, new { rowVersion = current.GetProperty("rowVersion").GetString() });
    }

    [DatabaseFact]
    public async Task An_invoice_line_above_its_price_control_range_is_held_for_the_executive_and_posted_once_approved()
    {
        await using var api = await ProjectsApi.StartAsync(database, new Dictionary<string, string?> { ["Projects:PriceCheckPolicy"] = "ExecutiveApproval" });
        var cast = await api.CastAsync("PRC-PRICE-01");
        var (_, purchasing) = await api.CreateSignedInUserAsync(cast.Executive, "purchasing");
        var cemex = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var costItem = (await api.CreateItemAsync(cast.Manager, cast.ProjectId, "Foundations")).Id();
        var cement = (await Ok(api, cast.Manager, HttpMethod.Post, $"/api/projects/{cast.ProjectId}/catalog-items", new
        {
            nameAr = "أسمنت", nameEn = "Portland cement", category = "CementAggregates", unit = "Bag", price = 150m,
            effectiveFrom = DateTime.UtcNow.AddDays(-5).ToString("yyyy-MM-dd"),
        }, HttpStatusCode.Created)).GetProperty("item").Id();

        // The company's reference for a bag of cement: 100 to 150. The requisition line names it; a mismatched unit is refused.
        var priceItem = (await Ok(api, cast.Executive, HttpMethod.Post, "/api/price-items",
            new { nameAr = "أسمنت 42.5", nameEn = "Cement 42.5", classification = "CementAggregates", unit = "Bag", minPrice = 100m, maxPrice = 150m }, HttpStatusCode.Created)).GetProperty("item").Id();
        var perTon = (await Ok(api, cast.Executive, HttpMethod.Post, "/api/price-items",
            new { nameAr = "أسمنت طن", nameEn = "Cement by the ton", classification = "CementAggregates", unit = "Ton", minPrice = 2_000m, maxPrice = 3_000m }, HttpStatusCode.Created)).GetProperty("item").Id();
        var wrongUnit = await api.SendAsync(HttpMethod.Post, "/api/procurement/requisitions", cast.Engineer,
            new { projectId = cast.ProjectId, lines = new[] { new { catalogItemId = cement, projectItemId = costItem, quantity = 100m, priceItemId = perTon } } });
        Assert.Equal(HttpStatusCode.BadRequest, wrongUnit.StatusCode);
        var requisition = await Ok(api, cast.Engineer, HttpMethod.Post, "/api/procurement/requisitions",
            new { projectId = cast.ProjectId, lines = new[] { new { catalogItemId = cement, projectItemId = costItem, quantity = 100m, priceItemId = priceItem } } }, HttpStatusCode.Created);
        var prId = requisition.Id();
        var prLine = requisition.GetProperty("lines")[0].Id();
        Assert.Equal(priceItem, requisition.GetProperty("lines")[0].GetProperty("priceItemId").GetInt32());

        // One quotation at 160 a bag, selected; the order carries the price item on to the invoice.
        var set = await Ok(api, cast.Executive, HttpMethod.Post, $"/api/procurement/requisitions/{prId}/quotation-request", new { quotationsWanted = 1 }, HttpStatusCode.Created);
        var setPath = $"/api/procurement/quotation-requests/{set.Id()}";
        set = await Ok(api, purchasing, HttpMethod.Post, $"{setPath}/quotations",
            new { supplierId = cemex, deliveryDays = 2, lines = new[] { new { requisitionLineId = prLine, unitPrice = 160m } } }, HttpStatusCode.Created);
        var quote = set.GetProperty("quotations")[0].Id();
        set = await Ok(api, purchasing, HttpMethod.Post, $"{setPath}/submit", new { rowVersion = set.GetProperty("rowVersion").GetString() });
        await Ok(api, cast.Executive, HttpMethod.Post, $"{setPath}/select", new { quotationId = quote, reason = "Only bidder", rowVersion = set.GetProperty("rowVersion").GetString() });
        var order = await Ok(api, purchasing, HttpMethod.Post, "/api/procurement/purchase-orders",
            new { requisitionId = prId, lines = new[] { new { requisitionLineId = prLine, quantity = 100m } } }, HttpStatusCode.Created);
        var orderLine = order.GetProperty("lines")[0].Id();
        Assert.Equal(priceItem, order.GetProperty("lines")[0].GetProperty("priceItemId").GetInt32());
        await Ok(api, cast.Engineer, HttpMethod.Post, $"/api/procurement/purchase-orders/{order.Id()}/receipts",
            new { receivedOn = Today, deliveryNoteNo = "DN-9", lines = new[] { new { orderLineId = orderLine, quantity = 100m } } }, HttpStatusCode.Created);

        // The invoice matches the order (160) but not the company's reference (150): held, unposted, and a case for the Executive.
        var invoices = $"/api/procurement/purchase-orders/{order.Id()}/invoices";
        var held = await Ok(api, cast.Accountant, HttpMethod.Post, invoices,
            new { invoiceNo = "C-2001", invoiceDate = Today, priceReason = "Bidder's price", lines = new[] { new { orderLineId = orderLine, quantity = 100m, unitPrice = 160m } } }, HttpStatusCode.Created);
        var heldLine = held.GetProperty("lines")[0];
        Assert.Equal(("PriceHold", "AboveRange", true), (held.GetProperty("status").GetString(), heldLine.GetProperty("priceCheck").GetString(), heldLine.GetProperty("priceMatches").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, heldLine.GetProperty("postedRecordId").ValueKind);
        var exceptionId = heldLine.GetProperty("priceExceptionId").GetInt32();
        Assert.Equal(0, await api.QueryAsync(db => db.ProjectExpenses.CountAsync()));

        var exception = await Ok(api, cast.Executive, HttpMethod.Get, $"/api/price-exceptions/{exceptionId}");
        Assert.Equal(("SupplierInvoiceLine", heldLine.Id(), 160m, 150m, 1_000m, "Bidder's price"),
            (exception.GetProperty("sourceType").GetString(), exception.GetProperty("sourceId").GetInt32(), exception.Money("unitPrice"), exception.Money("referenceMaxPrice"),
                exception.Money("varianceTotal"), exception.GetProperty("reason").GetString()));

        // The Accountant has no way around it: a held invoice is not resolved, and nothing is owed to pay.
        var resolve = await api.SendAsync(HttpMethod.Post, $"/api/procurement/supplier-invoices/{held.Id()}/resolve", cast.Accountant,
            new { resolution = "Accept", reason = "Fine", rowVersion = held.GetProperty("rowVersion").GetString() });
        Assert.Equal(HttpStatusCode.Conflict, resolve.StatusCode);

        // The Executive approves the price once: the invoice posts as the project's approved supplier cost, with the comparison on the record.
        await Ok(api, cast.Executive, HttpMethod.Post, $"/api/price-exceptions/{exceptionId}/approve", new { note = "Accepted for this order", rowVersion = exception.GetProperty("rowVersion").GetString() });
        var posted = await Ok(api, cast.Accountant, HttpMethod.Get, $"/api/procurement/supplier-invoices/{held.Id()}");
        Assert.Equal("Posted", posted.GetProperty("status").GetString());
        var recordId = posted.GetProperty("lines")[0].GetProperty("postedRecordId").GetInt32();
        var record = await api.QueryAsync(db => db.ProjectExpenses.AsNoTracking().SingleAsync(expense => expense.Id == recordId));
        Assert.Equal((ExpenseStatus.Approved, 16_000m, priceItem, PriceCheckResult.AboveRange, 150m),
            (record.Status, record.Amount, record.PriceItemId!.Value, record.PriceCheck!.Value, record.ReferenceMaxPrice!.Value));
    }

    [DatabaseFact]
    public async Task A_rejected_invoice_price_sends_the_invoice_to_the_accountant_s_mismatch_queue_unposted()
    {
        await using var api = await ProjectsApi.StartAsync(database, new Dictionary<string, string?> { ["Projects:PriceCheckPolicy"] = "ExecutiveApproval" });
        var cast = await api.CastAsync("PRC-PRICE-02");
        var (_, purchasing) = await api.CreateSignedInUserAsync(cast.Executive, "purchasing");
        var cemex = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var cement = (await Ok(api, cast.Manager, HttpMethod.Post, $"/api/projects/{cast.ProjectId}/catalog-items", new
        {
            nameAr = "أسمنت", nameEn = "Portland cement", category = "CementAggregates", unit = "Bag", price = 150m,
            effectiveFrom = DateTime.UtcNow.AddDays(-5).ToString("yyyy-MM-dd"),
        }, HttpStatusCode.Created)).GetProperty("item").Id();
        var priceItem = (await Ok(api, cast.Executive, HttpMethod.Post, "/api/price-items",
            new { nameAr = "أسمنت 42.5", nameEn = "Cement 42.5", classification = "CementAggregates", unit = "Bag", minPrice = 100m, maxPrice = 150m }, HttpStatusCode.Created)).GetProperty("item").Id();
        var requisition = await Ok(api, cast.Engineer, HttpMethod.Post, "/api/procurement/requisitions",
            new { projectId = cast.ProjectId, lines = new[] { new { catalogItemId = cement, quantity = 10m, priceItemId = priceItem } } }, HttpStatusCode.Created);
        var prLine = requisition.GetProperty("lines")[0].Id();
        var set = await Ok(api, cast.Executive, HttpMethod.Post, $"/api/procurement/requisitions/{requisition.Id()}/quotation-request", new { quotationsWanted = 1 }, HttpStatusCode.Created);
        var setPath = $"/api/procurement/quotation-requests/{set.Id()}";
        set = await Ok(api, purchasing, HttpMethod.Post, $"{setPath}/quotations",
            new { supplierId = cemex, deliveryDays = 2, lines = new[] { new { requisitionLineId = prLine, unitPrice = 140m } } }, HttpStatusCode.Created);
        var quote = set.GetProperty("quotations")[0].Id();
        set = await Ok(api, purchasing, HttpMethod.Post, $"{setPath}/submit", new { rowVersion = set.GetProperty("rowVersion").GetString() });
        await Ok(api, cast.Executive, HttpMethod.Post, $"{setPath}/select", new { quotationId = quote, reason = "Only bidder", rowVersion = set.GetProperty("rowVersion").GetString() });
        var order = await Ok(api, purchasing, HttpMethod.Post, "/api/procurement/purchase-orders",
            new { requisitionId = requisition.Id(), lines = new[] { new { requisitionLineId = prLine, quantity = 10m } } }, HttpStatusCode.Created);
        var orderLine = order.GetProperty("lines")[0].Id();
        await Ok(api, cast.Engineer, HttpMethod.Post, $"/api/procurement/purchase-orders/{order.Id()}/receipts",
            new { receivedOn = Today, lines = new[] { new { orderLineId = orderLine, quantity = 10m } } }, HttpStatusCode.Created);

        // Invoiced at 170 a bag: above the order (140) and above the reference (150). Held first; the Executive refuses the price.
        var held = await Ok(api, cast.Accountant, HttpMethod.Post, $"/api/procurement/purchase-orders/{order.Id()}/invoices",
            new { invoiceNo = "C-3001", invoiceDate = Today, lines = new[] { new { orderLineId = orderLine, quantity = 10m, unitPrice = 170m } } }, HttpStatusCode.Created);
        Assert.Equal("PriceHold", held.GetProperty("status").GetString());
        var exceptionId = held.GetProperty("lines")[0].GetProperty("priceExceptionId").GetInt32();
        var exception = await Ok(api, cast.Executive, HttpMethod.Get, $"/api/price-exceptions/{exceptionId}");
        await Ok(api, cast.Executive, HttpMethod.Post, $"/api/price-exceptions/{exceptionId}/reject", new { note = "Not at this price", rowVersion = exception.GetProperty("rowVersion").GetString() });

        // Back in the Accountant's queue: the supplier's price cannot be accepted; a credit note to the order price posts at 140.
        var mismatch = await Ok(api, cast.Accountant, HttpMethod.Get, $"/api/procurement/supplier-invoices/{held.Id()}");
        Assert.Equal("Mismatch", mismatch.GetProperty("status").GetString());
        var accept = await api.SendAsync(HttpMethod.Post, $"/api/procurement/supplier-invoices/{held.Id()}/resolve", cast.Accountant,
            new { resolution = "Accept", reason = "Pay it anyway", rowVersion = mismatch.GetProperty("rowVersion").GetString() });
        Assert.Equal((HttpStatusCode.Conflict, "price_exception_rejected"), (accept.StatusCode, await CodeOf(accept)));
        var credited = await Ok(api, cast.Accountant, HttpMethod.Post, $"/api/procurement/supplier-invoices/{held.Id()}/resolve",
            new { resolution = "CreditNote", reason = "Supplier credits the difference", creditNoteNo = "CN-9", rowVersion = mismatch.GetProperty("rowVersion").GetString() });
        Assert.Equal("Posted", credited.GetProperty("status").GetString());
        var recordId = credited.GetProperty("lines")[0].GetProperty("postedRecordId").GetInt32();
        Assert.Equal(1_400m, await api.QueryAsync(db => db.ProjectExpenses.Where(expense => expense.Id == recordId).Select(expense => expense.Amount).SingleAsync()));
    }

    private static async Task<decimal> MaterialsCommittedAsync(ProjectsApi api, CoreApi.Cast cast)
    {
        var overview = await Ok(api, cast.Executive, HttpMethod.Get, $"/api/projects/{cast.ProjectId}/overview");
        return overview.GetProperty("groups").EnumerateArray().Single(group => group.GetProperty("costGroup").GetString() == "Materials").Money("committed");
    }
}
