using AlMotaheda.Modules.Procurement.Data;
using AlMotaheda.Modules.Procurement.Features;

namespace AlMotaheda.Modules.Procurement.Tests.Unit;

/// <summary>The chain's quantity and price rules and the documents' status flows, with no database.</summary>
public sealed class ProcurementRulesTests
{
    [Theory]
    [InlineData(100, 100, 0, true)]
    [InlineData(100, 100.01, 0, false)]
    [InlineData(100, 99.99, 0, false)]
    [InlineData(100, 102, 2, true)]
    [InlineData(100, 102.01, 2, false)]
    [InlineData(100, 98, 2, true)]
    public void A_price_matches_within_the_tolerance_only(decimal orderPrice, decimal invoicePrice, decimal tolerance, bool matches) =>
        Assert.Equal(matches, ProcurementRules.PriceMatches(orderPrice, invoicePrice, tolerance));

    [Fact]
    public void Quantities_never_go_below_zero_and_never_allow_more_than_left()
    {
        Assert.Equal(40m, ProcurementRules.StillNeeded(100m, 60m));
        Assert.Equal(0m, ProcurementRules.StillNeeded(100m, 120m));
        Assert.Equal(25m, ProcurementRules.StillDue(100m, 75m));
        Assert.Equal(10m, ProcurementRules.Invoiceable(received: 30m, invoiced: 20m));
        Assert.Equal(0m, ProcurementRules.Invoiceable(received: 20m, invoiced: 20m));
    }

    [Fact]
    public void Committed_is_what_is_ordered_and_not_invoiced_at_the_order_price()
    {
        Assert.Equal(5_000m, ProcurementRules.Committed(ordered: 100m, invoiced: 50m, unitPrice: 100m));
        Assert.Equal(0m, ProcurementRules.Committed(ordered: 100m, invoiced: 100m, unitPrice: 100m));
        Assert.Equal(33.33m, ProcurementRules.Amount(3.333m, 10m));
    }

    [Fact]
    public void Auto_approval_is_an_approval_step_recorded_as_the_system_under_the_policy()
    {
        var now = DateTime.UtcNow;
        var requisition = new Requisition();
        requisition.Record("Submit", 5, null, null, now, null);

        requisition.ApproveByPolicy(now);

        Assert.Equal(RequisitionStatus.Approved, requisition.Status);
        var step = requisition.Events[^1];
        Assert.Equal(("Approve", null, "System (Auto-Approval Policy)", ProcurementPolicy.AutoApprovalPolicyName),
            (step.Action, step.ActorUserId, step.ActorLabel, step.PolicyName));
    }

    [Fact]
    public void People_approve_in_two_steps_and_a_decided_requisition_stays_decided()
    {
        var now = DateTime.UtcNow;
        var requisition = new Requisition();

        requisition.Approve(7, now, null);
        Assert.Equal(RequisitionStatus.ManagerApproved, requisition.Status);
        requisition.Approve(8, now, "Budget checked");
        Assert.Equal(RequisitionStatus.Approved, requisition.Status);
        Assert.Equal(["ManagerApprove", "AccountantApprove"], requisition.Events.Select(step => step.Action));

        Assert.Throws<InvalidOperationException>(() => requisition.Reject(9, now, "Too late"));
        requisition.MarkOrdered(true, 3, now);
        Assert.Equal(RequisitionStatus.Ordered, requisition.Status);
        requisition.MarkOrdered(false, 3, now);
        Assert.Equal(RequisitionStatus.Approved, requisition.Status);
    }

    [Fact]
    public void An_order_follows_its_receipts_and_is_cancelled_only_before_any()
    {
        var order = new PurchaseOrder();
        order.Received(anything: true, everything: false);
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, order.Status);
        Assert.Throws<InvalidOperationException>(() => order.Cancel(1, DateTime.UtcNow, "No"));
        order.Received(anything: true, everything: true);
        Assert.Equal(PurchaseOrderStatus.Received, order.Status);

        order.Revise("[]", "More steel", 1, DateTime.UtcNow);
        Assert.Equal((1, 1), (order.Revision, order.Revisions.Single().RevisionNo));
    }

    [Fact]
    public void A_mismatch_is_posted_or_returned_and_a_match_can_not_be_returned()
    {
        var mismatch = new SupplierInvoice { Status = SupplierInvoiceStatus.Mismatch };
        mismatch.Resolve(InvoiceResolution.CreditNote, "Supplier credits 2 EGP a bag", "CN-9", 4, DateTime.UtcNow);
        mismatch.MarkPosted(4, DateTime.UtcNow);
        Assert.Equal(SupplierInvoiceStatus.Posted, mismatch.Status);

        var matched = new SupplierInvoice { Status = SupplierInvoiceStatus.Matched };
        Assert.Throws<InvalidOperationException>(() => matched.Resolve(InvoiceResolution.ReturnToSupplier, "No", null, 4, DateTime.UtcNow));
    }
}
