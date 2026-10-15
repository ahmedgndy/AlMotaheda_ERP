using AlMotaheda.BuildingBlocks.Validation;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Expenses;
using AlMotaheda.Modules.Projects.Features.SupplierPayments;

namespace AlMotaheda.Modules.Projects.Tests.Unit.Core;

/// <summary>The expense workflow, the payment allocation arithmetic and the form rules, with no database.</summary>
public sealed class ExpenseRulesTests
{
    private const int Recorder = 7;
    private const int Reviewer = 8;

    [Fact]
    public void A_reviewer_decides_a_recorded_record_but_never_their_own()
    {
        Assert.Equal(ExpenseDecision.Allowed, ExpenseRules.Review(ExpenseStatus.Recorded, Recorder, Reviewer));
        Assert.Equal(ExpenseDecision.OwnRecord, ExpenseRules.Review(ExpenseStatus.Recorded, Recorder, Recorder));
        Assert.Equal(ExpenseDecision.WrongStatus, ExpenseRules.Review(ExpenseStatus.Approved, Recorder, Reviewer));
        Assert.Equal(ExpenseDecision.WrongStatus, ExpenseRules.Review(ExpenseStatus.Returned, Recorder, Reviewer));
        Assert.Equal(ExpenseDecision.WrongStatus, ExpenseRules.Review(ExpenseStatus.Rejected, Recorder, Reviewer));
    }

    [Fact]
    public void Only_the_recorder_resubmits_and_only_while_recorded_or_returned()
    {
        Assert.Equal(ExpenseDecision.Allowed, ExpenseRules.Resubmit(ExpenseStatus.Recorded, Recorder, Recorder));
        Assert.Equal(ExpenseDecision.Allowed, ExpenseRules.Resubmit(ExpenseStatus.Returned, Recorder, Recorder));
        Assert.Equal(ExpenseDecision.NotRecorder, ExpenseRules.Resubmit(ExpenseStatus.Returned, Recorder, Reviewer));
        Assert.Equal(ExpenseDecision.WrongStatus, ExpenseRules.Resubmit(ExpenseStatus.Approved, Recorder, Recorder));
        Assert.Equal(ExpenseDecision.WrongStatus, ExpenseRules.Resubmit(ExpenseStatus.Rejected, Recorder, Recorder));
    }

    [Fact]
    public void A_rejected_record_cannot_be_linked_and_only_approved_credit_can_be_paid()
    {
        Assert.True(ExpenseRules.CanLink(ExpenseStatus.Approved));
        Assert.True(ExpenseRules.CanLink(ExpenseStatus.Recorded));
        Assert.False(ExpenseRules.CanLink(ExpenseStatus.Rejected));

        Assert.True(ExpenseRules.CanBePaid(ExpenseStatus.Approved, PaymentChannel.Credit));
        Assert.False(ExpenseRules.CanBePaid(ExpenseStatus.Approved, PaymentChannel.Custody));
        Assert.False(ExpenseRules.CanBePaid(ExpenseStatus.Recorded, PaymentChannel.Credit));
    }

    [Fact]
    public void Fuel_and_equipment_suppliers_are_refused_because_their_costs_belong_in_site_equipment()
    {
        Assert.Equal(
            [SupplierKind.Equipment, SupplierKind.Fuel],
            Enum.GetValues<SupplierKind>().Where(ExpenseRules.IsEquipmentSupplier));

        Assert.True(new ExpenseReferences(null, null, SupplierKind.Fuel).IsEquipmentCost);
        Assert.True(new ExpenseReferences(null, null, SupplierKind.Equipment).IsEquipmentCost);
        Assert.False(new ExpenseReferences(null, null, SupplierKind.Materials).IsEquipmentCost);
        Assert.False(new ExpenseReferences(null, null).IsEquipmentCost);

        var problem = CoreProblems.UseEquipmentCosts();
        Assert.Equal(409, problem.StatusCode);
        Assert.Equal("use_equipment_costs", problem.ProblemDetails.Extensions["code"]);
    }

    [Fact]
    public void Allocations_must_name_each_record_once_with_positive_amounts_that_add_up_to_the_payment()
    {
        var errors = new ValidationErrors();

        var lines = PaymentAllocations.Validate([(1, 600m), (2, 400m)], 1_000m, errors);

        Assert.False(errors.HasErrors);
        Assert.Equal([new AllocationInput(1, 600m), new AllocationInput(2, 400m)], lines);
    }

    [Fact]
    public void Allocation_problems_are_reported_together_on_the_allocations_field()
    {
        var errors = new ValidationErrors();

        var lines = PaymentAllocations.Validate([(1, 600m), (1, 100m), (null, 0m), (3, 12.345m)], 1_000m, errors);

        Assert.Null(lines);
        var fields = errors.ToDictionary();
        Assert.Contains("allocations[1]", fields.Keys);   // the same record twice
        Assert.Contains("allocations[2]", fields.Keys);   // no record id, zero amount
        Assert.Contains("allocations[3]", fields.Keys);   // three decimals
    }

    [Fact]
    public void A_sum_that_differs_from_the_payment_is_refused()
    {
        var errors = new ValidationErrors();

        Assert.Null(PaymentAllocations.Validate([(1, 600m), (2, 400m)], 1_000.01m, errors));
        Assert.Contains("allocations", errors.ToDictionary().Keys);

        var empty = new ValidationErrors();
        Assert.Null(PaymentAllocations.Validate([], 10m, empty));
        Assert.Null(PaymentAllocations.Validate(null, 10m, empty));
        Assert.True(empty.HasErrors);
    }

    [Fact]
    public void A_payment_cannot_allocate_more_than_what_is_left_on_a_record()
    {
        var targets = new Dictionary<int, AllocationTarget>
        {
            [1] = new(1, Amount: 1_000m, AlreadyAllocated: 700m),   // 300 left
            [2] = new(2, Amount: 500m, AlreadyAllocated: 0m),       // 500 left
        };

        Assert.Null(PaymentAllocations.FindOverAllocation([new(1, 300m), new(2, 500m)], targets));
        Assert.Equal(1, PaymentAllocations.FindOverAllocation([new(1, 300.01m), new(2, 100m)], targets));
        Assert.Equal(2, PaymentAllocations.FindOverAllocation([new(1, 100m), new(2, 500.01m)], targets));
        Assert.Equal(9, PaymentAllocations.FindOverAllocation([new(9, 1m)], targets));
    }

    [Fact]
    public void A_supply_form_computes_its_amount_on_the_server_and_ignores_the_clients_amount()
    {
        var errors = new ValidationErrors();
        var today = new DateOnly(2026, 10, 13);

        var fields = ExpenseFields.Validate(
            new Form(Kind: "supply", ExpenseDate: "2026-10-10", SupplierId: 3, ItemName: "Cement 42.5", Unit: "bag",
                Quantity: 333.333m, UnitPrice: 2.95m, Amount: 1m, Channel: "credit", InvoiceNo: "INV-9"),
            today,
            errors);

        Assert.False(errors.HasErrors);
        Assert.Equal(ExpenseKind.Supply, fields!.Kind);
        Assert.Equal(983.33m, fields.Amount);                      // 333.333 × 2.95 = 983.33235 → 983.33
        Assert.Equal("Bag", fields.Unit);                           // the list's spelling
        Assert.Equal(PaymentChannel.Credit, fields.Channel);
    }

    [Fact]
    public void A_supply_form_reports_every_missing_field_at_once()
    {
        var errors = new ValidationErrors();

        var fields = ExpenseFields.Validate(
            new Form(Kind: "Supply", ExpenseDate: "2999-01-01", SupplierId: null, ItemName: "", Unit: "cubit",
                Quantity: 0m, UnitPrice: null, Channel: "Credit", Category: "Lab", InvoiceNo: null),
            new DateOnly(2026, 10, 13),
            errors);

        Assert.Null(fields);
        Assert.Equal(
            ["category", "expenseDate", "invoiceNo", "itemName", "quantity", "supplierId", "unit", "unitPrice"],
            errors.ToDictionary().Keys.Order());
    }

    [Fact]
    public void A_site_expense_form_needs_a_category_a_description_and_an_amount_and_refuses_supply_fields()
    {
        var errors = new ValidationErrors();

        var fields = ExpenseFields.Validate(
            new Form(Kind: "Expense", ExpenseDate: "2026-10-01", Channel: "Custody", Category: "Snacks", Description: " ",
                Amount: 0.001m, Quantity: 2m, CatalogItemId: 4),
            new DateOnly(2026, 10, 13),
            errors);

        Assert.Null(fields);
        Assert.Equal(["amount", "catalogItemId", "category", "description", "kind"], errors.ToDictionary().Keys.Order());

        var ok = new ValidationErrors();
        var expense = ExpenseFields.Validate(
            new Form(Kind: "Expense", ExpenseDate: "2026-10-01", Channel: "Custody", Category: "lab", Description: "Concrete cubes", Amount: 450m),
            new DateOnly(2026, 10, 13),
            ok);

        Assert.False(ok.HasErrors);
        Assert.Equal(ExpenseCategory.Lab, expense!.Category);
        Assert.Equal(450m, expense.Amount);
        Assert.Null(expense.SupplierId);
    }

    [Fact]
    public void Buying_on_credit_needs_a_supplier_even_for_a_site_expense()
    {
        var errors = new ValidationErrors();

        ExpenseFields.Validate(
            new Form(Kind: "Expense", ExpenseDate: "2026-10-01", Channel: "Credit", Category: "Guards", Description: "Night guard", Amount: 900m),
            new DateOnly(2026, 10, 13),
            errors);

        Assert.Equal(["supplierId"], errors.ToDictionary().Keys);
    }

    private sealed record Form(
        string? Kind = null,
        string? ExpenseDate = null,
        int? SupplierId = null,
        string? ItemName = null,
        string? Unit = null,
        decimal? Quantity = null,
        decimal? UnitPrice = null,
        string? Category = null,
        string? Description = null,
        decimal? Amount = null,
        string? Channel = null,
        string? InvoiceNo = null,
        string? DeliveryNoteNo = null,
        int? CatalogItemId = null,
        int? ProjectItemId = null,
        string? Notes = null) : IExpenseFieldsRequest;
}
