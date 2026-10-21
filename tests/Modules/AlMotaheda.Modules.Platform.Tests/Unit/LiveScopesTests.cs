using AlMotaheda.BuildingBlocks.Data;
using AlMotaheda.Modules.Platform.Live;

namespace AlMotaheda.Modules.Platform.Tests.Unit;

/// <summary>The dependency map: which group hears about which change, with no amounts in any signal.</summary>
public sealed class LiveScopesTests
{
    private static EntityChange Change(string entity, int? projectId, string action = "Modified", string? statusAfter = null, Dictionary<string, int>? people = null) =>
        new("Module", entity, "17", action, projectId, null, statusAfter, people ?? [], ["Status"]);

    private static List<(string Group, ChangeSignal Signal)> Signals(EntityChange change) =>
        LiveScopes.For(change, 900).Where(message => message.Method == "changed").Select(message => (message.Group, (ChangeSignal)message.Payload)).ToList();

    [Fact]
    public void A_money_record_tells_the_projects_money_group_its_list_and_the_company_finance_group()
    {
        var signals = Signals(Change("ProjectExpense", projectId: 5));

        Assert.Equal(["project:5:money", "project:5", "company:finance"], signals.Select(signal => signal.Group));
        Assert.Contains("ProjectOverview", signals[0].Signal.Aggregates);
        Assert.Contains("CompanyDashboard", signals[0].Signal.Aggregates);
        Assert.Equal(["Expenses"], signals[1].Signal.Aggregates); // the plain project group hears "the list changed", nothing about totals
        Assert.Equal((900L, "17", "ProjectExpense"), (signals[0].Signal.Version, signals[0].Signal.EntityKey, signals[0].Signal.EntityType));
    }

    [Fact]
    public void Hr_records_reach_only_the_hr_group_until_a_sheet_is_paid()
    {
        Assert.Equal(["company:hr"], Signals(Change("Employee", null)).Select(signal => signal.Group));
        Assert.Equal(["company:hr"], Signals(Change("SalarySheet", null, statusAfter: "Approved")).Select(signal => signal.Group));
        Assert.Equal(["company:hr", "company:finance"], Signals(Change("SalarySheet", null, statusAfter: "Paid")).Select(signal => signal.Group));
    }

    [Fact]
    public void Procurement_documents_reach_purchasing_and_the_project_and_orders_also_the_projects_money()
    {
        Assert.Equal(["project:3", "company:procurement"], Signals(Change("Requisition", 3)).Select(signal => signal.Group));
        Assert.Equal(["project:3:money", "project:3", "company:procurement"], Signals(Change("PurchaseOrder", 3)).Select(signal => signal.Group));
    }

    [Fact]
    public void Treasury_and_custody_records_reach_the_finance_group_and_a_transfer_also_its_engineers_project_money()
    {
        Assert.Equal(["company:finance"], Signals(Change("TreasuryAccount", null)).Select(signal => signal.Group));
        Assert.Equal(["company:finance"], Signals(Change("CustodyMovement", null)).Select(signal => signal.Group));
        var transfer = Signals(Change("CustodyTransfer", 5, statusAfter: "Received"));
        Assert.Equal(["project:5:money", "project:5", "company:finance"], transfer.Select(signal => signal.Group));
        Assert.Contains("Custody", transfer[0].Signal.Aggregates);
        Assert.Contains("Treasury", transfer[0].Signal.Aggregates);
        Assert.Empty(LiveScopes.For(Change("CustodyTransferEvent", null), 1));
        Assert.Empty(LiveScopes.For(Change("VehicleExpenseFlag", null), 1));
        var claim = Signals(Change("VehicleExpense", 5, statusAfter: "Approved"));
        Assert.Contains("ProjectOverview", claim[0].Signal.Aggregates);
        Assert.Equal(["company:hr"], Signals(Change("HrReferral", null)).Select(signal => signal.Group));
    }

    [Fact]
    public void Detail_rows_send_nothing_and_unknown_records_reach_only_their_project()
    {
        Assert.Empty(LiveScopes.For(Change("AttendanceLine", 3), 1));
        Assert.Empty(LiveScopes.For(Change("SupplierInvoiceLine", null), 1));
        Assert.Equal(["project:3"], Signals(Change("DailyReport", 3)).Select(signal => signal.Group));
        Assert.Empty(Signals(Change("Trade", null)));
    }

    [Fact]
    public void A_membership_or_permission_change_tells_that_user_to_start_again()
    {
        var messages = LiveScopes.For(Change("ProjectMember", 3, people: new() { ["UserId"] = 41 }), 1).ToList();

        Assert.Contains(messages, message => message is { Group: "user:41", Method: "resync" });
        Assert.Contains(LiveScopes.For(Change("UserPermissionOverride", null, people: new() { ["UserId"] = 7 }), 1), message => message is { Group: "user:7", Method: "resync" });
    }

    [Fact]
    public void No_signal_can_carry_an_amount_or_a_name()
    {
        var properties = typeof(ChangeSignal).GetProperties().Concat(typeof(NotificationSignal).GetProperties());

        Assert.DoesNotContain(properties, property => property.PropertyType == typeof(decimal) || property.PropertyType == typeof(decimal?));
        Assert.DoesNotContain(properties, property => property.Name.Contains("Name", StringComparison.Ordinal) || property.Name.Contains("Title", StringComparison.Ordinal));
    }

    [Fact]
    public void Custody_and_settlement_changes_refresh_the_custody_labor_and_cost_screens_of_their_project_and_finance()
    {
        var custody = Signals(Change("CustodyEntry", projectId: 5));
        Assert.Equal(["project:5:money", "project:5", "company:finance"], custody.Select(signal => signal.Group));
        Assert.Contains("Custody", custody[0].Signal.Aggregates);

        var settlement = Signals(Change("PayrollSettlement", projectId: 5, statusAfter: "Confirmed"));
        Assert.Contains("LaborPayable", settlement[0].Signal.Aggregates);
        Assert.Contains("ProjectItems", settlement[0].Signal.Aggregates);
        Assert.Contains("Custody", settlement[0].Signal.Aggregates);

        Assert.Contains("ProjectSuppliers", Signals(Change("SupplyAgreement", projectId: 5))[0].Signal.Aggregates);
        Assert.Contains("Custody", Signals(Change("ProjectExpense", projectId: 5))[0].Signal.Aggregates);

        // The alert rows and the detail rows of a settlement send nothing of their own.
        Assert.Empty(LiveScopes.For(Change("SettlementAlert", 5, "Added"), 1));
        Assert.Empty(LiveScopes.For(Change("PayrollSettlementAllocation", 5, "Added"), 1));
    }
}
