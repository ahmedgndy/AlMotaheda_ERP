using AlMotaheda.BuildingBlocks.Data;

namespace AlMotaheda.Modules.Platform.Live;

/// <summary>
/// A "something changed, refetch it" signal. No amounts, no names: the client refetches through the API, which applies
/// the caller's permissions (docs/modernization/04-signalr-and-cache-maps.md §2).
/// </summary>
/// <param name="Version">The outbox message id: always increasing. A client ignores a signal older than its data.</param>
/// <param name="Aggregates">The screens and figures to refetch, for example <c>ProjectOverview</c>.</param>
/// <param name="Scope">The group the signal was sent to.</param>
internal sealed record ChangeSignal(string EntityType, string EntityKey, long Version, string Action, IReadOnlyList<string> Aggregates, int? ProjectId, string Scope);

/// <summary>A stored notification's id and type; its text comes from <c>GET /api/notifications</c>.</summary>
internal sealed record NotificationSignal(long Id, string Type, string EntityType, string EntityKey, int? ProjectId);

/// <summary>One message to one group.</summary>
internal sealed record LiveMessage(string Group, string Method, object Payload);

/// <summary>The hub's method names, as the clients subscribe to them.</summary>
internal static class LiveMethods
{
    public const string Changed = "changed";
    public const string Notification = "notification";
    public const string Resync = "resync";
}

/// <summary>
/// The groups and the dependency map: which screens and figures each changed record affects, and which group
/// hears about it. The one place derived numbers are listed (04-signalr-and-cache-maps.md §3); the cache will use
/// the same map.
/// </summary>
internal static class LiveScopes
{
    public const string Finance = "company:finance";
    public const string Hr = "company:hr";
    public const string Procurement = "company:procurement";

    public static string User(int userId) => $"user:{userId}";

    public static string Project(int projectId) => $"project:{projectId}";

    public static string ProjectMoney(int projectId) => $"project:{projectId}:money";

    /// <param name="Money">The project's money figures change: sent to <c>project:{p}:money</c>, otherwise <c>project:{p}</c>.</param>
    /// <param name="Company">Company-wide groups that also hear about it.</param>
    private sealed record Route(string[] Aggregates, bool Money, string[] Company);

    private static readonly string[] ProjectCost = ["ProjectItems", "ProjectOverview", "CostSummary", "BudgetComparison", "CompanyDashboard"];

    private static readonly Dictionary<string, Route> Routes = new(StringComparer.Ordinal)
    {
        ["ProjectExpense"] = new(["Expenses", "SupplierBalances", .. ProjectCost], true, [Finance]),
        ["ExpenseAdjustment"] = new(["Expenses", "SupplierBalances", .. ProjectCost], true, [Finance]),
        ["SupplierPayment"] = new(["SupplierPayments", "SupplierBalances", .. ProjectCost], true, [Finance]),
        ["DailySheet"] = new(["LaborSheets", "LaborSummary", .. ProjectCost], true, [Finance]),
        ["LaborAllocation"] = new(["LaborSheets", "LaborSummary", .. ProjectCost], true, [Finance]),
        ["WorkerPayment"] = new(["WorkerAccount", "LaborSummary", "CompanyDashboard"], true, [Finance]),
        ["SubContract"] = new(["Subcontracts", "Obligations", .. ProjectCost], true, [Finance]),
        ["ProgressMeasurement"] = new(["Subcontracts", "Obligations", .. ProjectCost], true, [Finance]),
        ["ManpowerSheet"] = new(["Subcontracts", "Obligations", .. ProjectCost], true, [Finance]),
        ["SubPayment"] = new(["Subcontracts", "Obligations", .. ProjectCost], true, [Finance]),
        ["EquipmentCost"] = new(["EquipmentCosts", "EquipmentSummary", .. ProjectCost], true, [Finance]),
        ["EquipmentCostAdjustment"] = new(["EquipmentCosts", "EquipmentSummary", .. ProjectCost], true, [Finance]),
        ["Budget"] = new(["Budgets", "BudgetComparison", "ProjectOverview"], true, []),
        ["CatalogItem"] = new(["Catalog"], false, [Procurement]),
        ["ItemPrice"] = new(["Catalog", "PriceChanges"], false, [Procurement]),
        ["PaymentRequest"] = new(["PaymentRequests", "CompanyDashboard"], true, [Finance]),
        ["LedgerEntry"] = new(["Ledger", "CashMovement", "Reconciliation", "CompanyDashboard"], true, [Finance]),
        ["SalarySheet"] = new(["SalarySheets"], false, [Hr]),
        ["Employee"] = new(["Employees"], false, [Hr]),
        ["Department"] = new(["Departments"], false, [Hr]),
        ["EmployeeDocument"] = new(["Employees"], false, [Hr]),
        ["SalaryStructure"] = new(["Employees", "SalarySheets"], false, [Hr]),
        ["PayrollInput"] = new(["PayrollInputs", "SalarySheets"], false, [Hr]),
        ["Requisition"] = new(["Requisitions"], false, [Procurement]),
        ["QuotationRequest"] = new(["Quotations"], false, [Procurement]),
        ["Quotation"] = new(["Quotations"], false, [Procurement]),
        ["GoodsReceipt"] = new(["Receipts", "PurchaseOrders"], false, [Procurement]),
        ["PurchaseOrder"] = new(["PurchaseOrders", "CommittedCost", "ProjectOverview"], true, [Procurement]),
        ["SupplierInvoice"] = new(["SupplierInvoices", "CommittedCost", .. ProjectCost], true, [Procurement, Finance]),
    };

    /// <summary>Records that only add detail to a parent that changes in the same save; they send nothing alone.</summary>
    private static readonly HashSet<string> Silent = new(StringComparer.Ordinal)
    {
        "AttendanceLine", "LineAdjustment", "SupplierPaymentAllocation", "WorkerPaymentAllocation", "SubPaymentAllocation",
        "PaymentRequestLine", "PaymentRequestEvent", "RequisitionLine", "RequisitionEvent", "QuotationLine", "PurchaseOrderLine",
        "PurchaseOrderRevision", "GoodsReceiptLine", "SupplierInvoiceLine", "SalarySheetLine", "BudgetLine", "VoidedEquipmentCostPayment",
        "ReportRun", "RefreshToken", "UserSignInState",
    };

    /// <summary>The signals one changed record sends.</summary>
    public static IEnumerable<LiveMessage> For(EntityChange change, long version)
    {
        // Someone's projects or permissions changed: their open screens must start again (re-join, refetch).
        if (change.Entity is "ProjectMember" or "UserPermissionOverride" && change.People.TryGetValue("UserId", out var member))
        {
            yield return new LiveMessage(User(member), LiveMethods.Resync, new { reason = change.Entity });
        }

        if (Silent.Contains(change.Entity))
        {
            yield break;
        }

        var route = Routes.GetValueOrDefault(change.Entity) ?? new Route([change.Entity], false, []);
        var company = route.Company.ToList();
        if (change.Entity == "SalarySheet" && change.StatusAfter == "Paid")
        {
            company.Add(Finance); // a paid salary sheet is money out
        }

        if (change.ProjectId is { } projectId)
        {
            var scope = route.Money ? ProjectMoney(projectId) : Project(projectId);
            yield return Signal(change, version, route, scope);

            // Money records are also records on the project's lists, without their amounts.
            if (route.Money)
            {
                yield return Signal(change, version, route with { Aggregates = [route.Aggregates[0]] }, Project(projectId));
            }
        }

        foreach (var group in company.Distinct())
        {
            yield return Signal(change, version, route, group);
        }
    }

    private static LiveMessage Signal(EntityChange change, long version, Route route, string scope) =>
        new(scope, LiveMethods.Changed, new ChangeSignal(change.Entity, change.Key, version, change.Action, route.Aggregates, change.ProjectId, scope));
}
