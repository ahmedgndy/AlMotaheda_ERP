using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features.Costs;
using AlMotaheda.Modules.Projects.Contracts;

namespace AlMotaheda.Modules.Fleet.Tests.Unit;

/// <summary>The amount rules, what is recognized and paid, and the review and payment workflow, with real numbers.</summary>
public sealed class EquipmentCostRulesTests
{
    private static readonly DateOnly Today = new(2026, 1, 31);

    [Fact]
    public void Fuel_is_litres_times_the_price_per_litre_rounded_to_the_piastre()
    {
        // 182.5 L × 15.25 = 2,783.125 → 2,783.13 (halves away from zero).
        var fields = Valid(new() { Type = "Fuel", Liters = 182.5m, PricePerLiter = 15.25m, Amount = 1m, Description = null });

        Assert.Equal(2_783.13m, fields.Amount);
        Assert.Equal(182.5m, fields.Liters);
        Assert.Equal(15.25m, fields.PricePerLiter);
        Assert.Null(fields.Basis);
        Assert.Null(fields.Quantity);
        Assert.Null(fields.Rate);
        Assert.Equal("182.5 L × 15.25", fields.Description);
    }

    [Fact]
    public void A_per_day_cost_is_days_times_the_daily_rate()
    {
        // 15 days × 3,500 = 52,500. The typed amount is ignored.
        var fields = Valid(new() { Type = "Rental", Basis = "Day", Quantity = 15m, Rate = 3_500m, Amount = 1m, SupplierId = 4 });

        Assert.Equal(52_500m, fields.Amount);
        Assert.Equal(CostBasis.Day, fields.Basis);
        Assert.Equal(3_500m, fields.Rate);
    }

    [Fact]
    public void A_per_month_cost_is_months_times_the_monthly_rate()
    {
        // 1.5 months × 85,000 = 127,500.
        var fields = Valid(new() { Type = "Rental", Basis = "month", Quantity = 1.5m, Rate = 85_000m, SupplierId = 4 });

        Assert.Equal(127_500m, fields.Amount);
        Assert.Equal(CostBasis.Month, fields.Basis);
    }

    [Fact]
    public void A_per_hour_cost_is_hours_times_the_hourly_rate_rounded_to_the_piastre()
    {
        // 7.5 h × 333.33 = 2,499.975 → 2,499.98.
        var fields = Valid(new() { Type = "Transport", Basis = "Hour", Quantity = 7.5m, Rate = 333.33m });

        Assert.Equal(2_499.98m, fields.Amount);
    }

    [Fact]
    public void A_lump_sum_is_the_typed_amount()
    {
        var fields = Valid(new() { Type = "Repair", Basis = "Lump", Amount = 1_250.50m });

        Assert.Equal(1_250.50m, fields.Amount);
        Assert.Null(fields.Quantity);
        Assert.Null(fields.Rate);
    }

    [Fact]
    public void The_amount_helpers_agree_with_the_form()
    {
        Assert.Equal(2_783.13m, EquipmentCostAmounts.Fuel(182.5m, 15.25m));
        Assert.Equal(52_500m, EquipmentCostAmounts.ByRate(15m, 3_500m));
        Assert.Equal(0.01m, EquipmentCostAmounts.ByRate(0.001m, 5m));
        Assert.Equal(900m, EquipmentCostAmounts.Compute(EquipmentCostType.Other, CostBasis.Lump, null, null, null, null, 900m));
    }

    [Theory]
    [InlineData("Fuel", null, "liters,pricePerLiter")]
    [InlineData("Maintenance", null, "basis,description")]
    [InlineData("Maintenance", "Day", "description,quantity,rate")]
    [InlineData("Maintenance", "Lump", "amount,description")]
    public void Missing_inputs_for_the_type_and_basis_are_all_reported(string type, string? basis, string fields)
    {
        var errors = new ValidationErrors();

        var result = EquipmentCostFields.Validate(Request(new() { Type = type, Basis = basis, Description = null }), Today, errors);

        Assert.Null(result);
        Assert.Equal(fields.Split(','), errors.ToDictionary().Keys.Order());
    }

    [Fact]
    public void Inputs_that_do_not_apply_to_the_type_are_refused()
    {
        var errors = new ValidationErrors();

        EquipmentCostFields.Validate(Request(new() { Type = "Repair", Basis = "Lump", Amount = 100m, Quantity = 2m, Liters = 5m }), Today, errors);

        Assert.Equal(["liters", "quantity"], errors.ToDictionary().Keys.Order());
    }

    [Fact]
    public void A_cost_dated_after_today_is_refused()
    {
        var errors = new ValidationErrors();

        EquipmentCostFields.Validate(Request(new() { Type = "Fuel", Liters = 10m, PricePerLiter = 15m, CostDate = "2026-02-01" }), Today, errors);

        Assert.Equal(["costDate"], errors.ToDictionary().Keys);
    }

    [Fact]
    public void Only_an_approved_cost_is_recognized_and_custody_is_paid_at_approval()
    {
        Assert.Equal(0m, EquipmentCostSettlement.Recognized(EquipmentCostStatus.Recorded, 1_000m));
        Assert.Equal(0m, EquipmentCostSettlement.Recognized(EquipmentCostStatus.Returned, 1_000m));
        Assert.Equal(0m, EquipmentCostSettlement.Recognized(EquipmentCostStatus.Rejected, 1_000m));
        Assert.Equal(1_000m, EquipmentCostSettlement.Recognized(EquipmentCostStatus.Approved, 1_000m));

        Assert.Equal(0m, EquipmentCostSettlement.Paid(EquipmentCostStatus.Recorded, CostChannel.Custody, 1_000m, paymentRecorded: false));
        Assert.Equal(1_000m, EquipmentCostSettlement.Paid(EquipmentCostStatus.Approved, CostChannel.Custody, 1_000m, paymentRecorded: false));
    }

    [Fact]
    public void Credit_is_paid_only_after_its_payment_is_recorded()
    {
        var unpaid = Row(1, EquipmentCostType.Rental, EquipmentCostStatus.Approved, CostChannel.Credit, 52_500m, paid: false);
        var paid = unpaid with { PaymentRecorded = true };
        var notApproved = unpaid with { Status = EquipmentCostStatus.Recorded, PaymentRecorded = true };

        Assert.Equal(0m, EquipmentCostSettlement.Paid(unpaid));
        Assert.Equal(52_500m, EquipmentCostSettlement.Outstanding(unpaid));
        Assert.Equal(PaymentStates.Unpaid, EquipmentCostSettlement.PaymentState(unpaid));

        Assert.Equal(52_500m, EquipmentCostSettlement.Paid(paid));
        Assert.Equal(0m, EquipmentCostSettlement.Outstanding(paid));
        Assert.Equal(PaymentStates.Paid, EquipmentCostSettlement.PaymentState(paid));

        Assert.Equal(0m, EquipmentCostSettlement.Paid(notApproved));
        Assert.Null(EquipmentCostSettlement.PaymentState(notApproved));
    }

    [Fact]
    public void The_figures_for_the_projects_module_count_approved_costs_only()
    {
        ProjectCostFigures figures = EquipmentCostSettlement.Figures(
        [
            Row(1, EquipmentCostType.Fuel, EquipmentCostStatus.Approved, CostChannel.Custody, 2_783.13m, paid: false),
            Row(2, EquipmentCostType.Rental, EquipmentCostStatus.Approved, CostChannel.Credit, 52_500m, paid: false),
            Row(3, EquipmentCostType.Repair, EquipmentCostStatus.Approved, CostChannel.Credit, 4_000m, paid: true),
            Row(4, EquipmentCostType.Fuel, EquipmentCostStatus.Recorded, CostChannel.Custody, 1_525m, paid: false),
            Row(5, EquipmentCostType.Other, EquipmentCostStatus.Returned, CostChannel.Custody, 300m, paid: false),
            Row(6, EquipmentCostType.Transport, EquipmentCostStatus.Rejected, CostChannel.Custody, 700m, paid: false),
        ]);

        Assert.Equal(59_283.13m, figures.Recognized);
        Assert.Equal(6_783.13m, figures.Paid);
        Assert.Equal(52_500m, figures.Outstanding);
        Assert.Equal(3, figures.RecognizedRecords);
        Assert.Equal(1_525m, figures.PendingReview);
        Assert.Equal(1, figures.PendingRecords);
        Assert.Equal(0m, figures.Committed);
    }

    [Fact]
    public void Repair_counts_with_maintenance_and_transport_with_other()
    {
        Assert.Equal(CostGroup.Rental, EquipmentCostGroups.Of(EquipmentCostType.Rental));
        Assert.Equal(CostGroup.Fuel, EquipmentCostGroups.Of(EquipmentCostType.Fuel));
        Assert.Equal(CostGroup.Maintenance, EquipmentCostGroups.Of(EquipmentCostType.Maintenance));
        Assert.Equal(CostGroup.Maintenance, EquipmentCostGroups.Of(EquipmentCostType.Repair));
        Assert.Equal(CostGroup.Other, EquipmentCostGroups.Of(EquipmentCostType.Transport));
        Assert.Equal(CostGroup.Other, EquipmentCostGroups.Of(EquipmentCostType.Other));
    }

    [Fact]
    public void The_summary_splits_approved_costs_per_machine_and_per_month()
    {
        var monthStart = new DateOnly(2026, 1, 1);
        var december = new DateOnly(2025, 12, 20);
        var january = new DateOnly(2026, 1, 10);

        var figures = EquipmentSummary.Compute(
        [
            Row(1, EquipmentCostType.Rental, EquipmentCostStatus.Approved, CostChannel.Credit, 52_500m, paid: true, equipmentId: 10, date: december),
            Row(2, EquipmentCostType.Rental, EquipmentCostStatus.Approved, CostChannel.Credit, 35_000m, paid: false, equipmentId: 10, date: january),
            Row(3, EquipmentCostType.Fuel, EquipmentCostStatus.Approved, CostChannel.Custody, 2_783.13m, paid: false, equipmentId: 10, date: january, liters: 182.5m),
            Row(4, EquipmentCostType.Repair, EquipmentCostStatus.Approved, CostChannel.Custody, 1_200m, paid: false, equipmentId: 11, date: december),
            Row(5, EquipmentCostType.Maintenance, EquipmentCostStatus.Approved, CostChannel.Custody, 800m, paid: false, equipmentId: 11, date: january),
            Row(6, EquipmentCostType.Transport, EquipmentCostStatus.Approved, CostChannel.Custody, 1_500m, paid: false, equipmentId: 11, date: january),
            Row(7, EquipmentCostType.Fuel, EquipmentCostStatus.Recorded, CostChannel.Custody, 610m, paid: false, equipmentId: 11, date: january, liters: 40m),
            Row(8, EquipmentCostType.Fuel, EquipmentCostStatus.Returned, CostChannel.Custody, 305m, paid: false, equipmentId: 11, date: january, liters: 20m),
            Row(9, EquipmentCostType.Rental, EquipmentCostStatus.Rejected, CostChannel.Credit, 9_999m, paid: false, equipmentId: 12, date: january),
        ],
        monthStart);

        Assert.Equal(new EquipmentCostBreakdown(87_500m, 2_783.13m, 2_000m, 1_500m), figures.ToDate);
        Assert.Equal(93_783.13m, figures.ToDate.Total);
        Assert.Equal(new EquipmentCostBreakdown(35_000m, 2_783.13m, 800m, 1_500m), figures.ThisMonth);
        Assert.Equal(182.5m, figures.Liters);
        Assert.Equal(52_500m + 2_783.13m + 1_200m + 800m + 1_500m, figures.Paid);
        Assert.Equal(35_000m, figures.Outstanding);
        Assert.Equal(610m, figures.PendingReviewAmount);
        Assert.Equal(1, figures.PendingReviewCount);
        Assert.Equal(1, figures.ReturnedCount);

        // Biggest first; a machine with only a rejected cost is not listed.
        Assert.Equal([10, 11], figures.Machines.Select(machine => machine.EquipmentId));
        var excavator = figures.Machines[0];
        Assert.Equal(new EquipmentCostBreakdown(87_500m, 2_783.13m, 0m, 0m), excavator.ToDate);
        Assert.Equal(37_783.13m, excavator.ThisMonth);
        Assert.Equal(182.5m, excavator.Liters);
        Assert.Equal(55_283.13m, excavator.Paid);
        Assert.Equal(35_000m, excavator.Outstanding);
        var generator = figures.Machines[1];
        Assert.Equal(new EquipmentCostBreakdown(0m, 0m, 2_000m, 1_500m), generator.ToDate);
        Assert.Equal(2_300m, generator.ThisMonth);
        Assert.Equal(0m, generator.Liters);
        Assert.Equal(0m, generator.Outstanding);
    }

    [Fact]
    public void The_recorder_never_reviews_and_only_a_recorded_cost_is_reviewed()
    {
        Assert.Equal(CostDecision.OwnRecord, EquipmentCostRules.Review(EquipmentCostStatus.Recorded, recorderUserId: 7, callerUserId: 7));
        Assert.Equal(CostDecision.Allowed, EquipmentCostRules.Review(EquipmentCostStatus.Recorded, 7, 8));
        Assert.Equal(CostDecision.WrongStatus, EquipmentCostRules.Review(EquipmentCostStatus.Approved, 7, 8));
        Assert.Equal(CostDecision.WrongStatus, EquipmentCostRules.Review(EquipmentCostStatus.Returned, 7, 8));
        Assert.Equal(CostDecision.WrongStatus, EquipmentCostRules.Review(EquipmentCostStatus.Rejected, 7, 8));
    }

    [Fact]
    public void Only_the_recorder_fixes_a_recorded_or_returned_cost_and_an_approved_one_is_frozen()
    {
        Assert.Equal(CostDecision.NotRecorder, EquipmentCostRules.Resubmit(EquipmentCostStatus.Returned, 7, 8));
        Assert.Equal(CostDecision.Allowed, EquipmentCostRules.Resubmit(EquipmentCostStatus.Returned, 7, 7));
        Assert.Equal(CostDecision.Allowed, EquipmentCostRules.Resubmit(EquipmentCostStatus.Recorded, 7, 7));
        Assert.Equal(CostDecision.WrongStatus, EquipmentCostRules.Resubmit(EquipmentCostStatus.Approved, 7, 7));
        Assert.Equal(CostDecision.WrongStatus, EquipmentCostRules.Resubmit(EquipmentCostStatus.Rejected, 7, 7));
    }

    [Fact]
    public void Only_an_approved_credit_cost_takes_one_payment_and_only_a_paid_one_can_be_voided()
    {
        Assert.Equal(CostDecision.Allowed, EquipmentCostRules.Pay(EquipmentCostStatus.Approved, CostChannel.Credit, paymentRecorded: false));
        Assert.Equal(CostDecision.AlreadyPaid, EquipmentCostRules.Pay(EquipmentCostStatus.Approved, CostChannel.Credit, paymentRecorded: true));
        Assert.Equal(CostDecision.WrongStatus, EquipmentCostRules.Pay(EquipmentCostStatus.Approved, CostChannel.Custody, paymentRecorded: false));
        Assert.Equal(CostDecision.WrongStatus, EquipmentCostRules.Pay(EquipmentCostStatus.Recorded, CostChannel.Credit, paymentRecorded: false));

        Assert.Equal(CostDecision.Allowed, EquipmentCostRules.VoidPayment(paymentRecorded: true));
        Assert.Equal(CostDecision.NotPaid, EquipmentCostRules.VoidPayment(paymentRecorded: false));
    }

    private static EquipmentCostSettlementRow Row(
        int id, EquipmentCostType type, EquipmentCostStatus status, CostChannel channel, decimal amount, bool paid,
        int equipmentId = 10, DateOnly? date = null, decimal? liters = null) =>
        new(id, equipmentId, type, status, channel, date ?? Today, amount, liters, paid);

    private static EquipmentCostFields Valid(Form form)
    {
        var errors = new ValidationErrors();
        var fields = EquipmentCostFields.Validate(Request(form), Today, errors);
        Assert.False(errors.HasErrors, string.Join("; ", errors.ToDictionary().Select(entry => $"{entry.Key}: {string.Join(" ", entry.Value)}")));
        return fields!;
    }

    private static CreateEquipmentCost.Request Request(Form form) => new(
        form.EquipmentId, form.CostDate, form.Type, form.Basis, form.Quantity, form.Rate, form.Liters, form.PricePerLiter, form.Amount,
        form.Description, form.SupplierId, form.Channel, form.InvoiceNo);

    /// <summary>A cost form with sensible defaults; each test sets what it is about.</summary>
    private sealed class Form
    {
        public int? EquipmentId { get; init; } = 10;
        public string? CostDate { get; init; } = "2026-01-15";
        public string? Type { get; init; }
        public string? Basis { get; init; }
        public decimal? Quantity { get; init; }
        public decimal? Rate { get; init; }
        public decimal? Liters { get; init; }
        public decimal? PricePerLiter { get; init; }
        public decimal? Amount { get; init; }
        public string? Description { get; init; } = "Excavator EX-104";
        public int? SupplierId { get; init; }
        public string? Channel { get; init; } = "Custody";
        public string? InvoiceNo { get; init; }
    }
}
