using AlMotaheda.Modules.Procurement.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Custody;
using AlMotaheda.Modules.Projects.Features.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Tests.Unit.Workspace;

/// <summary>
/// The custody and supplier arithmetic with no database (formulas C to F of the financial workspace), and the
/// queries behind them translated to SQL Server SQL without a connection.
/// </summary>
public sealed class CustodyAndSupplierRulesTests
{
    private static readonly DateTime At = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);

    private static CustodyMovement Move(int day, CustodyEntryKind? kind, decimal amount, bool pending = false, int id = 1) =>
        new(new DateOnly(2026, 1, day), kind, kind?.ToString() ?? CustodyMovementTypes.Purchase, amount, pending, "Test", id, null, null, "Posted", 1, At);

    [Fact]
    public void A_custody_holds_fundings_less_returns_less_every_outflow_not_released_and_a_funding_is_never_a_cost()
    {
        var figures = CustodyBalance.Compute(
        [
            Move(1, CustodyEntryKind.Funding, 1_000m),
            Move(2, null, 200m),                                      // sand bought and approved
            Move(3, CustodyEntryKind.Payroll, 100m, pending: true),   // wages paid, settlement not reviewed yet
            Move(4, CustodyEntryKind.Return, 300m),
        ]);

        Assert.Equal((1_000m, 300m, 200m, 100m), (figures.Received, figures.Returned, figures.SpentApproved, figures.SpentPending));
        Assert.Equal(400m, figures.Balance);
        Assert.Equal(1, figures.PendingCount);
    }

    [Fact]
    public void Paying_more_than_the_custody_holds_shows_a_negative_balance_the_company_owes_the_holder()
    {
        Assert.Equal(-50m, CustodyBalance.Compute([Move(1, CustodyEntryKind.Funding, 150m), Move(2, null, 200m)]).Balance);
    }

    [Fact]
    public void The_statement_runs_oldest_first_whatever_order_the_movements_come_in()
    {
        var lines = CustodyBalance.Run([Move(5, null, 200m, id: 2), Move(1, CustodyEntryKind.Funding, 1_000m, id: 1), Move(9, CustodyEntryKind.Return, 300m, id: 3)]);

        Assert.Equal([1_000m, 800m, 500m], lines.Select(line => line.Balance));
    }

    private static SupplierRecordRow Record(int id, ExpenseStatus status, PaymentChannel channel, decimal amount, decimal allocated = 0, int? item = 1, decimal? quantity = 1) =>
        new(id, 1, 7, null, item, ExpenseKind.Supply, status, channel, new DateOnly(2026, 1, 10), "Cement", "Bag", quantity, amount, allocated, null, 1, null);

    private static SupplierOrderLine Order(decimal committed, string status = "Issued") =>
        new(1, "PO-1", 1, 7, 1, 1, "Cement", "Bag", 10m, 50m, 500m, 0m, 0m, committed, status, null, new DateOnly(2026, 1, 1));

    [Fact]
    public void A_suppliers_cost_payments_debt_pending_and_commitments_are_kept_apart()
    {
        var figures = SupplierFigures.Of(
        [
            Record(1, ExpenseStatus.Approved, PaymentChannel.Credit, 1_000m, allocated: 400m),
            Record(2, ExpenseStatus.Approved, PaymentChannel.Custody, 200m),
            Record(3, ExpenseStatus.Recorded, PaymentChannel.Credit, 300m),
        ],
        [Order(500m)]);

        Assert.Equal(1_200m, figures.Recognized);
        Assert.Equal((1_000m, 200m), (figures.CreditRecognized, figures.CustodyRecognized));
        Assert.Equal(600m, figures.Paid);
        Assert.Equal(600m, figures.Outstanding);
        Assert.Equal((300m, 1), (figures.PendingReview, figures.PendingCount));
        Assert.Equal(1, figures.OpenInvoices);
        Assert.Equal(500m, figures.Committed);
    }

    [Fact]
    public void An_agreement_shows_what_is_delivered_and_what_is_left_without_counting_pending_supplies()
    {
        var agreement = new SupplyAgreement { MaterialName = "Cement", Unit = "Bag", AgreedQuantity = 100m, AgreedUnitPrice = 50m };

        var progress = AgreementProgress.Of(agreement,
        [
            Record(1, ExpenseStatus.Approved, PaymentChannel.Credit, 2_000m, allocated: 1_000m, quantity: 40m),
            Record(2, ExpenseStatus.Returned, PaymentChannel.Credit, 500m, quantity: 10m),
        ]);

        Assert.Equal((40m, 10m, 60m), (progress.DeliveredQuantity, progress.PendingQuantity, progress.RemainingQuantity));
        Assert.Equal((2_000m, 2_000m, 1_000m, 1_000m), (progress.Recognized, progress.Invoiced, progress.Paid, progress.Outstanding));
        Assert.Equal(3_000m, progress.RemainingValue);
    }

    [Fact]
    public void The_supplier_ledger_owes_approved_credit_less_payments_and_ignores_orders_and_custody_purchases_in_the_balance()
    {
        var payments = new[]
        {
            new SupplierPaymentRow(1, 1, 7, new DateOnly(2026, 1, 12), 400m, PaymentMethod.BankTransfer, "TRF", 1, null, null, null),
            new SupplierPaymentRow(2, 1, 7, new DateOnly(2026, 1, 13), 100m, PaymentMethod.Cash, null, 1, new DateTime(2026, 1, 14, 9, 0, 0, DateTimeKind.Utc), 2, "Twice"),
        };
        var facts = SupplierLedger.Facts(
            [Record(1, ExpenseStatus.Approved, PaymentChannel.Credit, 1_000m), Record(2, ExpenseStatus.Approved, PaymentChannel.Custody, 200m),
             Record(3, ExpenseStatus.Recorded, PaymentChannel.Credit, 999m)],
            payments,
            [Order(500m)]);

        var lines = SupplierLedger.Run(facts);

        Assert.Equal(
            [SupplierLedgerTypes.Order, SupplierLedgerTypes.Invoice, SupplierLedgerTypes.CustodyPurchase, SupplierLedgerTypes.Payment, SupplierLedgerTypes.Payment, SupplierLedgerTypes.PaymentVoided],
            lines.Select(line => line.Fact.Type));
        Assert.Equal(600m, lines[^1].Balance);
    }

    [Fact]
    public void The_custody_and_supplier_queries_translate_and_filter_in_sql()
    {
        using var db = new ProjectsDbContextDesignTimeFactory().CreateDbContext([]);

        var entries = CustodyQueries.Entries(db, 3, 9).ToQueryString();
        Assert.Contains("[projects].[CustodyEntry]", entries);
        Assert.Contains("'Released'", entries);
        var expenses = CustodyQueries.Expenses(db, 3, 9).ToQueryString();
        Assert.Contains("'Custody'", expenses);
        Assert.Contains("[CreatedBy] = @", expenses);

        var records = SupplierLedger.Records(db, 3, null, [4, 5]).ToQueryString();
        Assert.Contains("[SupplyAgreementId]", records);
        Assert.Contains("[projects].[SupplierPaymentAllocation]", records);
        Assert.Contains("[projects].[SupplierPayment]", SupplierLedger.Payments(db, null, 7).ToQueryString());
    }
}
