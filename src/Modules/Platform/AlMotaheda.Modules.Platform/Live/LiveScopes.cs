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
/// hears about it. The one place derived numbers are listed (04-signalr-and-cache-maps.md §3): live signals
/// (<see cref="For"/>) and cache removal (<see cref="CacheTagsFor"/>) both come from it, so they can't disagree.
/// </summary>
internal static class LiveScopes
{
    public const string Finance = "company:finance";
    public const string Hr = "company:hr";
    public const string Procurement = "company:procurement";

    /// <summary>The company office's cash custody and expenses (Office module): custodians and finance.</summary>
    public const string Office = "company:office";

    public static string User(int userId) => $"user:{userId}";

    public static string Project(int projectId) => $"project:{projectId}";

    public static string ProjectMoney(int projectId) => $"project:{projectId}:money";

    /// <param name="Money">The project's money figures change: sent to <c>project:{p}:money</c>, otherwise <c>project:{p}</c>.</param>
    /// <param name="Company">Company-wide groups that also hear about it.</param>
    private sealed record Route(string[] Aggregates, bool Money, string[] Company);

    private static readonly string[] ProjectCost = ["ProjectItems", "ProjectOverview", "CostSummary", "BudgetComparison", "CompanyDashboard"];

    private static readonly Dictionary<string, Route> Routes = new(StringComparer.Ordinal)
    {
        ["ProjectExpense"] = new(["Expenses", "SupplierBalances", "ProjectSuppliers", "Custody", .. ProjectCost], true, [Finance]),
        ["ExpenseAdjustment"] = new(["Expenses", "SupplierBalances", "ProjectSuppliers", "Custody", .. ProjectCost], true, [Finance]),
        ["SupplierPayment"] = new(["SupplierPayments", "SupplierBalances", "ProjectSuppliers", .. ProjectCost], true, [Finance]),
        ["DailySheet"] = new(["LaborSheets", "LaborSummary", .. ProjectCost], true, [Finance]),
        ["LaborAllocation"] = new(["LaborSheets", "LaborSummary", .. ProjectCost], true, [Finance]),
        // A worker payment settles days on several projects: it has no project, so the overview of every project is refreshed.
        ["WorkerPayment"] = new(["WorkerAccount", "LaborSummary", "LaborPayable", "ProjectOverview", "CompanyDashboard"], true, [Finance]),
        ["SubContract"] = new(["Subcontracts", "Obligations", .. ProjectCost], true, [Finance]),
        ["ProgressMeasurement"] = new(["Subcontracts", "Obligations", .. ProjectCost], true, [Finance]),
        ["ManpowerSheet"] = new(["Subcontracts", "Obligations", .. ProjectCost], true, [Finance]),
        ["SubPayment"] = new(["Subcontracts", "Obligations", .. ProjectCost], true, [Finance]),
        ["EquipmentCost"] = new(["EquipmentCosts", "EquipmentSummary", .. ProjectCost], true, [Finance]),
        ["EquipmentCostAdjustment"] = new(["EquipmentCosts", "EquipmentSummary", .. ProjectCost], true, [Finance]),
        ["Budget"] = new(["Budgets", "BudgetComparison", "ProjectOverview"], true, []),
        ["CatalogItem"] = new(["Catalog"], false, [Procurement]),
        ["ItemPrice"] = new(["Catalog", "PriceChanges"], false, [Procurement]),
        // The company price list has no project: it is heard by purchasing and refetched by the price screens.
        ["PriceItem"] = new(["PriceManagement"], false, [Procurement]),
        ["PriceRange"] = new(["PriceManagement", "PriceRangeRequests"], false, [Procurement]),
        // Price Control: a case changes the inbox, the item's screen and the record it is about (its project's lists).
        ["PriceException"] = new(["PriceExceptions", "PriceManagement", "Expenses", "SupplierInvoices"], false, [Procurement, Finance]),
        ["PriceCategory"] = new(["PriceManagement"], false, [Procurement]),
        ["PriceUnit"] = new(["PriceManagement"], false, [Procurement]),
        // Moving an item under another main item changes the roll-ups of the items table.
        ["ProjectItem"] = new(["ProjectItems", "ProjectOverview", "BudgetComparison"], true, []),
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
        // Cash custody: the custody screens and, through payroll, the labor figures.
        ["CustodyEntry"] = new(["Custody", "ProjectOverview", "CompanyDashboard"], true, [Finance]),
        ["PayrollSettlement"] = new(["LaborSettlements", "LaborPayable", "LaborSummary", "WorkerAccount", "Custody", .. ProjectCost], true, [Finance]),
        ["SupplyAgreement"] = new(["SupplyAgreements", "ProjectSuppliers"], true, [Procurement]),
        ["ContractVariation"] = new(["Subcontracts", "Obligations", .. ProjectCost], true, [Finance]),
        // The treasury and the custody hierarchy (Finance): company-wide money screens; an engineer's transfer also refreshes their project's custody.
        ["TreasuryAccount"] = new(["Treasury", "CompanyDashboard"], true, [Finance]),
        ["TreasuryDeposit"] = new(["Treasury", "TreasuryDeposits", "CompanyDashboard"], true, [Finance]),
        ["CustodyAccount"] = new(["CustodyAccounts", "Treasury"], true, [Finance]),
        ["CustodyMovement"] = new(["CustodyAccounts", "CustodyStatement", "PayrollBatches"], true, [Finance]),
        ["CustodyTransfer"] = new(["CustodyTransfers", "CustodyAccounts", "Treasury", "Custody", "PayrollBatches"], true, [Finance]),
        ["PayrollBatch"] = new(["PayrollBatches"], true, [Finance]),
        ["CustodyCashCount"] = new(["CustodyAccounts", "CustodyStatement"], true, [Finance]),
        ["CustodyAdjustment"] = new(["CustodyAccounts", "CustodyStatement"], true, [Finance]),
        ["CustodyPeriodClose"] = new(["CustodyAccounts"], true, [Finance]),
        // Vehicles (Fleet): the register, a vehicle's custody and claims; a claim on a project also refreshes its equipment figures.
        ["Vehicle"] = new(["Vehicles"], false, []),
        ["VehicleDriverAssignment"] = new(["Vehicles", "VehicleDrivers"], false, []),
        ["VehicleCustodyEntry"] = new(["VehicleCustody", "Vehicles", "CustodyAccounts"], true, [Finance]),
        ["VehicleExpense"] = new(["VehicleExpenses", "VehicleCustody", "EquipmentSummary", .. ProjectCost], true, [Finance]),
        ["VehicleViolation"] = new(["VehicleViolations"], false, []),
        // HR: assignments and referrals.
        ["EmployeeAssignment"] = new(["EmployeeAssignments", "Employees"], false, [Hr]),
        ["HrReferral"] = new(["HrReferrals"], false, [Hr]),
        ["HrRequest"] = new(["HrRequests"], false, [Hr]),
        // Office management: company overhead, no project. Office screens refetch; Finance hears of what moves cash.
        ["OfficeCustody"] = new(["OfficeCustody"], false, [Office]),
        ["OfficeLedgerEntry"] = new(["OfficeCustody", "OfficeLedger", "OfficeSummary"], false, [Office, Finance]),
        ["OfficeExpense"] = new(["OfficeExpenses", "OfficeCustody", "OfficeSummary"], false, [Office]),
        ["OfficeFundingRequest"] = new(["OfficeFunding", "OfficeCustody", "OfficeSummary"], false, [Office, Finance]),
        ["OfficeSpendRequest"] = new(["OfficeSpendRequests", "OfficeCustody"], false, [Office]),
        ["OfficeCashCount"] = new(["OfficeCashCounts", "OfficeCustody", "OfficeSummary"], false, [Office]),
    };

    /// <summary>Records that only add detail to a parent that changes in the same save; they send nothing alone.</summary>
    private static readonly HashSet<string> Silent = new(StringComparer.Ordinal)
    {
        "AttendanceLine", "LineAdjustment", "SupplierPaymentAllocation", "WorkerPaymentAllocation", "SubPaymentAllocation",
        "PaymentRequestLine", "PaymentRequestEvent", "RequisitionLine", "RequisitionEvent", "QuotationLine", "PurchaseOrderLine",
        "PurchaseOrderRevision", "GoodsReceiptLine", "SupplierInvoiceLine", "SalarySheetLine", "BudgetLine", "VoidedEquipmentCostPayment",
        "ReportRun", "RefreshToken", "UserSignInState", "PriceAboveRangeAlert", "CostRecordedAlert", "PriceExceptionAlert",
        "PayrollSettlementLine", "PayrollSettlementAllocation", "SettlementAlert",
        "CustodyTransferEvent", "CustodyTransferAlert", "HrRequestAlert", "HrRequestMessage", "HrRequestAttachment", "PayrollBatchAllocation", "VehicleExpenseFlag", "VehicleExpenseAlert", "FleetAttachment",
        "OfficeExpenseApproval", "OfficeAttachment", "OfficeExpenseSubmittedAlert", "OfficeExpensePostedAlert", "OfficeFundingRequestedAlert",
        "OfficeFundsDisbursedAlert", "OfficeLowBalanceAlert", "OfficeDiscrepancyAlert", "OfficeSpendRequestedAlert",
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

    /// <summary>
    /// The cache tags one changed record removes: each aggregate it affects, for its project, or for every project when
    /// the record has none (a worker payment, a labor allocation).
    /// </summary>
    public static IEnumerable<string> CacheTagsFor(EntityChange change)
    {
        if (Silent.Contains(change.Entity))
        {
            return [];
        }

        var route = Routes.GetValueOrDefault(change.Entity) ?? new Route([change.Entity], false, []);
        return route.Aggregates.Select(aggregate => change.ProjectId is { } projectId
            ? CacheTags.Aggregate(aggregate, projectId)
            : CacheTags.Aggregate(aggregate));
    }

    private static LiveMessage Signal(EntityChange change, long version, Route route, string scope) =>
        new(scope, LiveMethods.Changed, new ChangeSignal(change.Entity, change.Key, version, change.Action, route.Aggregates, change.ProjectId, scope));
}
