using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Fleet.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database;
using AlMotaheda.Modules.Projects.Tests.Database.Core;
using AlMotaheda.Modules.Workforce.Tests.Database;
using static AlMotaheda.Integration.Tests.CostFlowCalls;

namespace AlMotaheda.Integration.Tests;

/// <summary>
/// Purchases, labor, salaries, equipment and suppliers, end to end on SQL Server with the real seeded roles: tests B,
/// F, G, H, I, J and K of the brief (docs/cost-flow).
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class CostFlowRecordTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task B_a_2000_cement_purchase_is_one_record_reviewed_by_finance_counted_once_and_paid_separately()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var people = await PeopleAsync(api);
        var project = people.ProjectId;
        var cement = Id(await MasterItemAsync(api, people.Executive, "Cement", "Material", "CementAggregates", "Bag", price: 20m));
        var concrete = Id(await MasterItemAsync(api, people.Executive, "Concrete work", "Composite", "Concrete", "M3"));
        var on = await SelectAsync(api, people.Manager, project, cement, concrete);
        var supplier = await CoreApi.CreateSupplierAsync(api, people.Executive, "Cemex");

        var recorded = await CoreApi.RecordAsync(api, people.Engineer, project, CoreApi.SupplyBody(supplier, 100m, 20m, "Credit", new()
        {
            ["catalogItemId"] = on[cement], ["projectItemId"] = on[concrete],
        }));
        Assert.Equal((2_000m, "Recorded"), (Money(recorded, "amount"), recorded.GetProperty("status").GetString()));

        // Waiting for review: not a cost yet, visible as pending.
        var overview = await Get(api, people.Accountant, $"/api/projects/{project}/overview");
        Assert.Equal((0m, 2_000m), (Money(Group(overview, "Materials"), "recognized"), Money(Group(overview, "Materials"), "pendingReview")));
        Assert.Equal(0m, Money(ItemRow(await CoreApi.ItemsAsync(api, people.Accountant, project), on[concrete]), "totalCost"));

        // The reviewers hear about it (projects Approve and the project in scope): the Accountant and the Executive;
        // the Financial Manager can read it too. The engineer who recorded it isn't told about their own record.
        await WaitForNotificationAsync(api, people.Accountant, About("ReviewRequested", "ProjectExpense", Id(recorded)));
        await WaitForNotificationAsync(api, people.Executive, About("ReviewRequested", "ProjectExpense", Id(recorded)));
        Assert.DoesNotContain(await NotificationsAsync(api, people.Engineer), notification => About("ReviewRequested", "ProjectExpense", Id(recorded))(notification));
        var seenByFinancialManager = await CoreApi.GetExpenseAsync(api, people.FinancialManager, project, Id(recorded));
        Assert.Equal(2_000m, Money(seenByFinancialManager, "amount"));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/projects/{project}/expenses/{Id(recorded)}/approve",
            people.FinancialManager, new { rowVersion = Version(recorded) })).StatusCode);

        // Approved: one recognized cost of 2,000 on the concrete item and in Materials, unpaid.
        var approved = await CoreApi.ApproveAsync(api, people.Accountant, project, recorded);
        await WaitForNotificationAsync(api, people.Engineer, About("RecordApproved", "ProjectExpense", Id(recorded)));
        overview = await Get(api, people.Accountant, $"/api/projects/{project}/overview");
        Assert.Equal((2_000m, 0m, 0m), (Money(Group(overview, "Materials"), "recognized"), Money(Group(overview, "Materials"), "paid"), Money(Group(overview, "SiteExpenses"), "recognized")));
        var item = ItemRow(await CoreApi.ItemsAsync(api, people.Accountant, project), on[concrete]);
        Assert.Equal((2_000m, 0m, 2_000m), (Money(item, "totalCost"), Money(item, "paid"), Money(item, "outstanding")));
        Assert.Equal(2_000m, Money((await CoreApi.BalancesAsync(api, people.Accountant, project)).GetProperty("items")[0], "outstanding"));

        // Paid through Finance: paid moves, cost doesn't, and there is still exactly one record.
        await CoreApi.PaidAsync(api, people.Accountant, project, supplier, 2_000m, (Id(approved), 2_000m));
        overview = await Get(api, people.Accountant, $"/api/projects/{project}/overview");
        Assert.Equal((2_000m, 2_000m), (Money(overview.GetProperty("totals"), "recognized"), Money(overview.GetProperty("totals"), "paid")));
        item = ItemRow(await CoreApi.ItemsAsync(api, people.Accountant, project), on[concrete]);
        Assert.Equal((2_000m, 2_000m, 0m), (Money(item, "totalCost"), Money(item, "paid"), Money(item, "outstanding")));
        Assert.Equal(1, (await CoreApi.ListExpensesAsync(api, people.Accountant, project)).GetProperty("items").GetArrayLength());
        Assert.Equal(1, await api.QueryAsync(db => db.ProjectExpenses.CountAsync(expense => expense.ProjectId == project)));
        Assert.Equal(0m, Money((await CoreApi.ItemsAsync(api, people.Accountant, project)).GetProperty("unallocated"), "totalCost"));
    }

    [DatabaseFact]
    public async Task F_twenty_workers_split_over_items_are_costed_once_after_approval()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var people = await PeopleAsync(api);
        var project = people.ProjectId;
        var concrete = await WorkforceApi.CreateItemAsync(api, people.Executive, project, "Concrete work", "Concrete");
        var plumbing = await WorkforceApi.CreateItemAsync(api, people.Executive, project, "Plumbing");
        var cast = new WorkforceApi.Cast(people.Executive, people.Manager, people.Engineer, people.EngineerId, people.Accountant, project, people.ManagerId);

        var workers = new List<int>();
        for (var index = 1; index <= 20; index++)
        {
            workers.Add(await WorkforceApi.HireAsync(api, people.Executive, project, WorkforceApi.Mason, $"Worker {index:00}", dailyRate: 400m));
        }

        var day = WorkforceApi.Today(api).AddDays(-1);
        var lines = new List<object>
        {
            // Worker 1: concrete, 2 hours overtime (400 / 8 × 1.5 × 2 = 150), a 50 meal allowance and a 20 deduction with its reason.
            WorkforceApi.Line(workers[0], 1m, [(concrete, 1m)], overtimeHours: 2m, adjustments: [("Allowance", "Food", 50m, null), ("Deduction", "Other", 20m, "Late arrival")]),
        };
        lines.AddRange(workers.Skip(1).Take(9).Select(worker => WorkforceApi.Line(worker, 1m, [(concrete, 1m)])));   // 9 more on concrete
        lines.AddRange(workers.Skip(10).Take(7).Select(worker => WorkforceApi.Line(worker, 1m, [(plumbing, 1m)])));  // 7 on plumbing
        lines.AddRange(workers.Skip(17).Select(worker => WorkforceApi.Line(worker, 1m, [(concrete, 0.5m), (plumbing, 0.5m)]))); // 3 split

        // More than one day on a line, or the same worker twice in a day, is refused.
        Assert.Equal(HttpStatusCode.Conflict, (await WorkforceApi.RecordSheetAsync(api, people.Engineer, project, day, false,
            WorkforceApi.Line(workers[0], 1m, [(concrete, 0.7m), (plumbing, 0.5m)]))).StatusCode);
        var sheetResponse = await WorkforceApi.RecordSheetAsync(api, people.Engineer, project, day, true, [.. lines]);
        await AccessApi.EnsureStatus(sheetResponse, HttpStatusCode.Created);
        var sheet = await AccessApi.Json(sheetResponse);
        var twice = await WorkforceApi.RecordSheetAsync(api, people.Manager, project, day, true, WorkforceApi.Line(workers[0]));
        await AccessApi.EnsureStatus(twice, HttpStatusCode.Conflict);
        Assert.Equal("worker_already_recorded", await AccessApi.CodeOf(twice));

        // Submitted, not approved: no labor cost anywhere yet.
        Assert.Equal(0m, Money(Group(await Get(api, people.Accountant, $"/api/projects/{project}/overview"), "Labor"), "recognized"));
        Assert.Equal(0m, Money(ItemRow(await CoreApi.ItemsAsync(api, people.Accountant, project), concrete), "laborCost"));

        // The engineer can't approve labor; the project's manager does.
        Assert.Equal(HttpStatusCode.Forbidden, (await WorkforceApi.ReviewAsync(api, people.Engineer, project, sheet, "approve")).StatusCode);
        await AccessApi.EnsureStatus(await WorkforceApi.ReviewAsync(api, people.Manager, project, sheet, "approve"), HttpStatusCode.OK);

        // Concrete = 9 × 400 + 580 + 3 × 200 = 4,780; plumbing = 7 × 400 + 3 × 200 = 3,400; total 8,180, counted once.
        var items = await CoreApi.ItemsAsync(api, people.Accountant, project);
        Assert.Equal(4_780m, Money(ItemRow(items, concrete), "laborCost"));
        Assert.Equal(3_400m, Money(ItemRow(items, plumbing), "laborCost"));
        Assert.Equal((13, 10), (ItemRow(items, concrete).GetProperty("workerCount").GetInt32(), ItemRow(items, plumbing).GetProperty("workerCount").GetInt32()));
        Assert.Equal(0m, Money(items.GetProperty("unallocated"), "laborCost"));
        var overview = await Get(api, people.Accountant, $"/api/projects/{project}/overview");
        Assert.Equal(8_180m, Money(Group(overview, "Labor"), "recognized"));
        Assert.Equal(8_180m, Money(overview.GetProperty("totals"), "recognized"));

        // Each worker keeps their own line: the first one's overtime, allowance and deduction reason.
        var detail = await CoreApi.ItemDetailAsync(api, people.Accountant, project, concrete);
        var first = detail.GetProperty("laborLines").EnumerateArray().Single(line => line.GetProperty("workerId").GetInt32() == workers[0]);
        Assert.Equal((150m, 50m, 20m, 580m), (Money(first, "overtimeAmount"), Money(first, "allowancesAmount"), Money(first, "deductionsAmount"), Money(first, "lineNet")));
        Assert.Contains("Late arrival", first.GetProperty("deductionReasons").GetString());
        Assert.Equal(13, detail.GetProperty("laborLines").GetArrayLength());
    }

    [DatabaseFact]
    public async Task G_an_engineers_monthly_salary_is_charged_to_the_project_by_approved_days_once_and_paid_once()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var people = await PeopleAsync(api);
        var project = people.ProjectId;
        var (_, hr) = await api.CreateSignedInUserAsync(people.Executive, "hr");
        var month = new DateOnly(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(-1);
        var monthText = month.ToString("yyyy-MM");
        var concrete = await WorkforceApi.CreateItemAsync(api, people.Executive, project, "Concrete work", "Concrete");

        var department = Id(await Send(api, hr, HttpMethod.Post, "/api/hr/departments", new { nameAr = "الهندسة", nameEn = "Engineering" }, HttpStatusCode.Created));
        var engineer = Id(await Send(api, hr, HttpMethod.Post, "/api/hr/employees", new
        {
            nameAr = "مهندس", nameEn = "Karim", nationalId = "28801011234567", jobTitle = "Site engineer", departmentId = department, kind = "Engineer", hireDate = "2024-01-01",
        }, HttpStatusCode.Created));
        await Send(api, hr, HttpMethod.Post, $"/api/hr/employees/{engineer}/salaries", new { effectiveFrom = "2024-01-01", basicSalary = 20_000m, fixedAllowance = 0m }, HttpStatusCode.Created);

        // HR picks the project and item by name only; it never sees the project's figures.
        Assert.Contains((await Get(api, hr, "/api/hr/project-options")).EnumerateArray(), option => Id(option) == project);
        Assert.Equal([concrete], (await Get(api, hr, $"/api/hr/project-options/{project}/items")).EnumerateArray().Select(Id));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, $"/api/projects/{project}/overview", hr)).StatusCode);

        // Five days on the project; HR records them, the Accountant approves.
        var days = await Send(api, hr, HttpMethod.Post, "/api/hr/project-days",
            new { employeeId = engineer, projectId = project, month = monthText, days = 5m, projectItemId = concrete }, HttpStatusCode.Created);
        await WaitForNotificationAsync(api, people.Accountant, About("ReviewRequested", "ProjectWorkDays", Id(days)));
        days = await Send(api, people.Accountant, HttpMethod.Post, $"/api/hr/project-days/{Id(days)}/approve", new { rowVersion = Version(days) });

        // No universal formula: the run waits until the Executive sets the working days of a month.
        Assert.Equal("allocation_policy_not_set", await Refused(api, people.Accountant, HttpMethod.Post, "/api/hr/salary-allocations/run",
            new { month = monthText }, HttpStatusCode.Conflict));
        var policy = await Get(api, hr, "/api/hr/cost-allocation-policy");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Put, "/api/hr/cost-allocation-policy", people.Accountant,
            new { workingDaysPerMonth = 20, rowVersion = Version(policy) })).StatusCode);
        await Send(api, people.Executive, HttpMethod.Put, "/api/hr/cost-allocation-policy", new { workingDaysPerMonth = 20, rowVersion = Version(policy) });
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, "/api/hr/salary-allocations/run", hr, new { month = monthText })).StatusCode);

        // 20,000 / 20 × 5 = 5,000. Running again charges nothing more.
        var run = await Send(api, people.Accountant, HttpMethod.Post, "/api/hr/salary-allocations/run", new { month = monthText });
        Assert.Equal((1, 0, 0, 5_000m), (run.GetProperty("created").GetInt32(), run.GetProperty("reversed").GetInt32(), run.GetProperty("unchanged").GetInt32(), Money(run, "chargedTotal")));
        var again = await Send(api, people.Accountant, HttpMethod.Post, "/api/hr/salary-allocations/run", new { month = monthText });
        Assert.Equal((0, 0, 1), (again.GetProperty("created").GetInt32(), again.GetProperty("reversed").GetInt32(), again.GetProperty("unchanged").GetInt32()));

        var overview = await Get(api, people.Accountant, $"/api/projects/{project}/overview");
        Assert.Equal((5_000m, 0m), (Money(Group(overview, "Staff"), "recognized"), Money(Group(overview, "Staff"), "paid")));
        Assert.Equal(5_000m, Money(ItemRow(await CoreApi.ItemsAsync(api, people.Accountant, project), concrete), "staffCost"));

        // A correction to 6 days supersedes the 5 when approved; the next run reverses 5,000 and charges 6,000.
        var correction = await Send(api, hr, HttpMethod.Post, "/api/hr/project-days",
            new { employeeId = engineer, projectId = project, month = monthText, days = 6m, projectItemId = concrete }, HttpStatusCode.Created);
        await Send(api, people.Accountant, HttpMethod.Post, $"/api/hr/project-days/{Id(correction)}/approve", new { rowVersion = Version(correction) });
        var rerun = await Send(api, people.Accountant, HttpMethod.Post, "/api/hr/salary-allocations/run", new { month = monthText });
        Assert.Equal((1, 1, 6_000m), (rerun.GetProperty("created").GetInt32(), rerun.GetProperty("reversed").GetInt32(), Money(rerun, "chargedTotal")));
        var allocations = await Get(api, people.Accountant, $"/api/hr/salary-allocations?month={monthText}");
        Assert.Equal(6_000m, Money(allocations, "activeTotal"));
        Assert.Equal(["Active", "Reversed"], allocations.GetProperty("items").EnumerateArray().Select(row => row.GetProperty("status").GetString()).Order());

        // More days than the month has, on any projects together, is refused.
        var other = await api.CreateProjectAsync(people.Executive, "CF-002", people.ManagerId);
        Assert.Equal("days_over_month", await Refused(api, hr, HttpMethod.Post, "/api/hr/project-days",
            new { employeeId = engineer, projectId = Id(other), month = monthText, days = 15m }, HttpStatusCode.Conflict));

        // Payroll pays the whole salary as company money; the project's cost stays 6,000 and now shows as paid.
        var sheet = await Send(api, hr, HttpMethod.Post, "/api/hr/salary-sheets", new { month = monthText }, HttpStatusCode.Created);
        sheet = await Send(api, hr, HttpMethod.Post, $"/api/hr/salary-sheets/{Id(sheet)}/submit", new { rowVersion = Version(sheet) });
        sheet = await Send(api, people.Accountant, HttpMethod.Post, $"/api/hr/salary-sheets/{Id(sheet)}/approve", new { rowVersion = Version(sheet) });
        var request = await Send(api, people.Accountant, HttpMethod.Post, $"/api/hr/salary-sheets/{Id(sheet)}/payment-request",
            new { requestedDate = Iso(DateOnly.FromDateTime(DateTime.UtcNow)), method = "Cash", rowVersion = Version(sheet) }, HttpStatusCode.Created);
        await api.SettleAsync(people.Accountant, request);

        overview = await Get(api, people.Accountant, $"/api/projects/{project}/overview");
        Assert.Equal((6_000m, 6_000m), (Money(Group(overview, "Staff"), "recognized"), Money(Group(overview, "Staff"), "paid")));
        Assert.Equal(6_000m, Money(overview.GetProperty("totals"), "recognized"));
        Assert.Equal(-20_000m, (await Get(api, people.Accountant, "/api/ledger?counterpartyType=Employees")).GetProperty("totals").GetProperty("net").GetDecimal());
        Assert.Equal(0m, (await Get(api, people.Accountant, $"/api/ledger?projectId={project}")).GetProperty("totals").GetProperty("net").GetDecimal());
    }

    [DatabaseFact]
    public async Task H_fuel_and_spare_parts_are_counted_once_a_repair_request_is_never_a_cost_and_a_void_keeps_history()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var people = await PeopleAsync(api);
        // Both engineers work on this project; only the first is responsible for the machine.
        var (otherEngineerId, otherEngineer) = await api.CreateSignedInUserAsync(people.Executive, "engineer");
        var project = Id(await api.CreateProjectAsync(people.Executive, "CF-H", people.ManagerId, siteEngineers: [people.EngineerId, otherEngineerId]));
        var concrete = await WorkforceApi.CreateItemAsync(api, people.Executive, project, "Concrete work", "Concrete");
        var parts = await FleetApi.CreateSupplierAsync(api, people.Executive, "Hydraulic Parts Co");
        var fuelStation = await CoreApi.CreateSupplierAsync(api, people.Executive, "Misr Petroleum", "Fuel");

        var loader = await FleetApi.RegisterAsync(api, people.Executive, FleetApi.EquipmentBody("CF-LDR-1"));
        await Send(api, people.Manager, HttpMethod.Post, $"/api/projects/{project}/equipment",
            new { equipmentId = Id(loader), responsibleUserId = people.EngineerId, operatorName = "Hany", startDate = "2026-01-05" }, HttpStatusCode.Created);

        var fuel = await FleetApi.ReviewedAsync(api, people.Accountant, project,
            await FleetApi.RecordedCostAsync(api, people.Manager, project, FleetApi.FuelBody(Id(loader), 100m, 15m)), "approve");
        object SpareParts() => new
        {
            equipmentId = Id(loader), costDate = "2026-01-20", type = "SpareParts", basis = "Lump", amount = 2_000m,
            description = "Hydraulic pump", supplierId = parts, channel = "Credit", invoiceNo = "INV-SP-1",
        };
        var spare = await FleetApi.ReviewedAsync(api, people.Accountant, project, await FleetApi.RecordedCostAsync(api, people.Manager, project, SpareParts()), "approve");

        // The same cost twice, or the fuel again as a project expense, is refused.
        Assert.Equal("duplicate_cost", await Refused(api, people.Manager, HttpMethod.Post, $"/api/projects/{project}/equipment-costs", SpareParts(), HttpStatusCode.Conflict));
        Assert.Equal("use_equipment_costs", await Refused(api, people.Engineer, HttpMethod.Post, $"/api/projects/{project}/expenses",
            CoreApi.ExpenseBody(1_500m, "Custody", fuelStation, new() { ["description"] = "Diesel for the loader" }), HttpStatusCode.Conflict));

        // Equipment 3,500 once; the fuel is linked to the concrete item, the spare parts are not linked yet.
        await Send(api, people.Manager, HttpMethod.Post, $"/api/projects/{project}/equipment-costs/{Id(fuel)}/item", new { projectItemId = concrete, rowVersion = Version(fuel) });
        var items = await CoreApi.ItemsAsync(api, people.Accountant, project);
        Assert.Equal((1_500m, 2_000m), (Money(ItemRow(items, concrete), "equipmentCost"), Money(items.GetProperty("unallocated"), "equipmentCost")));
        Assert.Equal(3_500m, Money(Group(await Get(api, people.Accountant, $"/api/projects/{project}/overview"), "Equipment"), "recognized"));

        // A repair request: by the machine's responsible engineer, not by another engineer; never a cost.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/projects/{project}/repair-requests", otherEngineer,
            new { equipmentId = Id(loader), problem = "Hydraulic leak" })).StatusCode);
        var repair = await Send(api, people.Engineer, HttpMethod.Post, $"/api/projects/{project}/repair-requests",
            new { equipmentId = Id(loader), problem = "Hydraulic leak on the boom cylinder", priority = "Urgent" }, HttpStatusCode.Created);
        await WaitForNotificationAsync(api, people.Accountant, About("ReviewRequested", "RepairRequest", Id(repair)));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/projects/{project}/repair-requests/{Id(repair)}/approve",
            people.Engineer, new { rowVersion = Version(repair) })).StatusCode);
        repair = await Send(api, people.Manager, HttpMethod.Post, $"/api/projects/{project}/repair-requests/{Id(repair)}/approve", new { rowVersion = Version(repair) });
        repair = await Send(api, people.Manager, HttpMethod.Post, $"/api/projects/{project}/repair-requests/{Id(repair)}/complete",
            new { result = "Pump replaced, no leak", equipmentCostId = Id(spare), rowVersion = Version(repair) });
        Assert.Equal(("Completed", Id(spare)), (repair.GetProperty("status").GetString(), repair.GetProperty("equipmentCostId").GetInt32()));
        Assert.Equal(3_500m, Money(Group(await Get(api, people.Accountant, $"/api/projects/{project}/overview"), "Equipment"), "recognized"));

        // Paid, then the payment is voided: history stays, paid goes back to 0, the cost is still 2,000.
        var paid = await FleetApi.PaidCostAsync(api, people.Accountant, project, spare);
        Assert.Equal(3_500m, Money(Group(await Get(api, people.Accountant, $"/api/projects/{project}/overview"), "Equipment"), "paid"));
        await AccessApi.EnsureStatus(await FleetApi.VoidPaymentAsync(api, people.Executive, project, paid), HttpStatusCode.OK);
        var voided = await Get(api, people.Accountant, $"/api/projects/{project}/equipment-costs/{Id(spare)}/voided-payments");
        Assert.Single(voided.EnumerateArray());
        var equipment = Group(await Get(api, people.Accountant, $"/api/projects/{project}/overview"), "Equipment");
        Assert.Equal((3_500m, 1_500m), (Money(equipment, "recognized"), Money(equipment, "paid")));
    }

    [DatabaseFact]
    public async Task I_a_supplier_on_two_projects_has_one_true_history_and_each_project_sees_only_its_own()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var people = await PeopleAsync(api, "CF-A");
        var (otherManagerId, otherManager) = await api.CreateSignedInUserAsync(people.Executive, "manager");
        var projectB = Id(await api.CreateProjectAsync(people.Executive, "CF-B", otherManagerId));
        var supplier = await CoreApi.CreateSupplierAsync(api, people.Executive, "Cemex");

        // A: 1,000 on credit, 600 paid. B: 2,000 on credit and 300 from custody (paid at once); 400 still waiting.
        var a1 = await CoreApi.RecordApprovedAsync(api, people.Engineer, people.Accountant, people.ProjectId, CoreApi.SupplyBody(supplier, 50m, 20m, "Credit"));
        await CoreApi.PaidAsync(api, people.Accountant, people.ProjectId, supplier, 600m, (Id(a1), 600m));
        await CoreApi.RecordApprovedAsync(api, otherManager, people.Accountant, projectB, CoreApi.SupplyBody(supplier, 100m, 20m, "Credit"));
        await CoreApi.RecordApprovedAsync(api, otherManager, people.Accountant, projectB, CoreApi.SupplyBody(supplier, 15m, 20m, "Custody"));
        await CoreApi.RecordAsync(api, otherManager, projectB, CoreApi.SupplyBody(supplier, 20m, 20m, "Credit"));

        var history = await Get(api, people.Accountant, $"/api/suppliers/{supplier}/history");
        var byProject = history.GetProperty("projects").EnumerateArray().ToDictionary(row => row.GetProperty("projectId").GetInt32());
        Assert.Equal((1_000m, 600m, 400m), (Money(byProject[people.ProjectId], "recognized"), Money(byProject[people.ProjectId], "paid"), Money(byProject[people.ProjectId], "outstanding")));
        Assert.Equal((2_300m, 300m, 2_000m), (Money(byProject[projectB], "recognized"), Money(byProject[projectB], "paid"), Money(byProject[projectB], "outstanding")));
        Assert.Equal((1, 400m), (byProject[projectB].GetProperty("pendingRecords").GetInt32(), Money(byProject[projectB], "pendingAmount")));
        Assert.Equal((3_300m, 900m, 2_400m), (Money(history, "recognized"), Money(history, "paid"), Money(history, "outstanding")));
        Assert.Equal(115m, byProject[projectB].GetProperty("deliveries")[0].GetProperty("quantity").GetDecimal());

        // The history agrees with each project's own supplier balances, row by row.
        foreach (var projectId in new[] { people.ProjectId, projectB })
        {
            var balance = (await CoreApi.BalancesAsync(api, people.Accountant, projectId)).GetProperty("items").EnumerateArray()
                .Single(row => row.GetProperty("supplierId").GetInt32() == supplier);
            Assert.Equal((Money(balance, "paid"), Money(balance, "outstanding")), (Money(byProject[projectId], "paid"), Money(byProject[projectId], "outstanding")));
        }

        // A project's manager sees only that project in the supplier's history.
        var managersView = await Get(api, otherManager, $"/api/suppliers/{supplier}/history");
        Assert.Equal([projectB], managersView.GetProperty("projects").EnumerateArray().Select(row => row.GetProperty("projectId").GetInt32()));

        // A supplier document lives once, on its project, and shows on the supplier's profile through its link.
        var document = await UploadPdfAsync(api, people.Manager, people.ProjectId, "Cemex framework contract");
        await Send(api, people.Manager, HttpMethod.Put, $"/api/projects/{people.ProjectId}/documents/{Id(document)}/links",
            new { links = new[] { new { entityType = "Supplier", entityId = supplier } }, rowVersion = Version(document) });
        Assert.Equal([Id(document)], (await Get(api, people.Accountant, $"/api/suppliers/{supplier}/documents")).GetProperty("items").EnumerateArray().Select(Id));
        Assert.Empty((await Get(api, otherManager, $"/api/suppliers/{supplier}/documents")).GetProperty("items").EnumerateArray());
    }

    [DatabaseFact]
    public async Task J_records_never_cross_projects_and_money_or_prices_need_their_permission()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var people = await PeopleAsync(api, "CF-A");
        var (otherManagerId, otherManager) = await api.CreateSignedInUserAsync(people.Executive, "manager");
        var a = people.ProjectId;
        var b = Id(await api.CreateProjectAsync(people.Executive, "CF-B", otherManagerId));
        var itemA = await WorkforceApi.CreateItemAsync(api, people.Executive, a, "Concrete A", "Concrete");
        var itemB = await WorkforceApi.CreateItemAsync(api, people.Executive, b, "Concrete B", "Concrete");
        var supplier = await CoreApi.CreateSupplierAsync(api, people.Executive, "Cemex");
        var cement = Id(await MasterItemAsync(api, people.Executive, "Cement", "Material", "CementAggregates", "Bag", price: 20m));
        var cementOnB = (await SelectAsync(api, otherManager, b, cement))[cement];

        var supplyA = await CoreApi.RecordAsync(api, people.Engineer, a, CoreApi.SupplyBody(supplier, 10m, 20m, "Custody"));
        var supplyB = await CoreApi.RecordAsync(api, otherManager, b, CoreApi.SupplyBody(supplier, 10m, 20m, "Custody"));
        var before = await api.QueryAsync(db => db.ProjectExpenses.Select(expense => new { expense.Id, expense.ProjectItemId, expense.CatalogItemId }).ToListAsync());

        // A's purchase can't be linked to B's item, nor name B's catalog item.
        Assert.Equal(HttpStatusCode.BadRequest, (await CoreApi.LinkAsync(api, people.Manager, a, supplyA, itemB)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await api.SendAsync(HttpMethod.Post, $"/api/projects/{a}/expenses", people.Engineer,
            CoreApi.SupplyBody(supplier, 1m, 20m, "Custody", new() { ["catalogItemId"] = cementOnB }))).StatusCode);

        // A's labor can't be allocated to B's item.
        var worker = await WorkforceApi.HireAsync(api, people.Executive, a, WorkforceApi.Mason, "Saad", dailyRate: 400m);
        Assert.Equal(HttpStatusCode.BadRequest, (await WorkforceApi.RecordSheetAsync(api, people.Engineer, a, WorkforceApi.Today(api).AddDays(-1), true,
            WorkforceApi.Line(worker, 1m, [(itemB, 1m)]))).StatusCode);

        // A's document can't point at B's record; B's records can't be read through A or by A's people.
        var document = await UploadPdfAsync(api, people.Manager, a, "Delivery note");
        Assert.Equal(HttpStatusCode.BadRequest, (await api.SendAsync(HttpMethod.Put, $"/api/projects/{a}/documents/{Id(document)}/links", people.Manager,
            new { links = new[] { new { entityType = "Expense", entityId = Id(supplyB) } }, rowVersion = Version(document) })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, $"/api/projects/{a}/expenses/{Id(supplyB)}", people.Manager)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, $"/api/projects/{b}/expenses/{Id(supplyB)}", people.Engineer)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, $"/api/projects/{b}/catalog-selection", people.Engineer)).StatusCode);

        // A component of A's item can't name B's catalog item.
        Assert.Equal(HttpStatusCode.BadRequest, (await api.SendAsync(HttpMethod.Put, $"/api/projects/{a}/items/{itemA}/components", people.Manager,
            new { components = new[] { new { kind = "Material", catalogItemId = cementOnB } } })).StatusCode);

        // Prices and policies: only their holders.
        var price = await Send(api, people.Accountant, HttpMethod.Post, $"/api/catalog/items/{cement}/price-changes",
            new { price = 25m, effectiveFrom = "2026-06-01", reason = "New list" }, HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/catalog/price-changes/{Id(price)}/approve", people.Manager, new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/catalog/items/{cement}/price-changes", people.Engineer,
            new { price = 1m, reason = "cheaper" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, "/api/catalog/items", people.Accountant,
            new { nameAr = "س", nameEn = "Sand", kind = "Material", category = "CementAggregates", unit = "M3", price = 1m })).StatusCode);

        // Money needs See money: the engineer can't open the items table, the overview or the supplier history's amounts.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, $"/api/projects/{a}/items", people.Engineer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, $"/api/projects/{a}/overview", people.Engineer)).StatusCode);
        var engineersHistory = await Get(api, people.Engineer, $"/api/suppliers/{supplier}/history");
        Assert.Equal(JsonValueKind.Null, engineersHistory.GetProperty("recognized").ValueKind);
        Assert.Equal([a], engineersHistory.GetProperty("projects").EnumerateArray().Select(row => row.GetProperty("projectId").GetInt32()));

        // Nothing changed.
        var after = await api.QueryAsync(db => db.ProjectExpenses.Select(expense => new { expense.Id, expense.ProjectItemId, expense.CatalogItemId }).ToListAsync());
        Assert.Equal(before, after);
        Assert.Equal(20m, Money((await Get(api, people.Accountant, $"/api/catalog/items/{cement}")).GetProperty("item"), "currentPrice"));
    }

    [DatabaseFact]
    public async Task K_every_line_and_total_of_a_project_reconciles_with_its_source_records()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var people = await PeopleAsync(api);
        var project = people.ProjectId;
        var ex = people.Executive;
        var concrete = await WorkforceApi.CreateItemAsync(api, ex, project, "Concrete work", "Concrete");
        var finishing = await WorkforceApi.CreateItemAsync(api, ex, project, "Finishing", "Tiling");
        var cemex = await CoreApi.CreateSupplierAsync(api, ex, "Cemex");
        var ezz = await CoreApi.CreateSupplierAsync(api, ex, "Ezz Steel");

        // Supplies: 2,000 credit on concrete (1,500 paid), 1,500 custody on finishing, 1,000 credit not linked, 500 waiting.
        var s1 = await CoreApi.RecordApprovedAsync(api, people.Engineer, people.Accountant, project, CoreApi.SupplyBody(cemex, 100m, 20m, "Credit", new() { ["projectItemId"] = concrete }));
        await CoreApi.RecordApprovedAsync(api, people.Engineer, people.Accountant, project, CoreApi.SupplyBody(cemex, 10m, 150m, "Custody", new() { ["projectItemId"] = finishing }));
        await CoreApi.RecordApprovedAsync(api, people.Engineer, people.Accountant, project, CoreApi.SupplyBody(ezz, 1m, 1_000m, "Credit", new() { ["itemName"] = "Steel 12 mm", ["unit"] = "Ton" }));
        await CoreApi.RecordAsync(api, people.Engineer, project, CoreApi.SupplyBody(cemex, 25m, 20m, "Credit"));
        await CoreApi.PaidAsync(api, people.Accountant, project, cemex, 1_500m, (Id(s1), 1_500m));
        // A site expense with no supplier and no item: 300 from custody.
        await CoreApi.RecordApprovedAsync(api, people.Engineer, people.Accountant, project, CoreApi.ExpenseBody(300m, "Custody"));

        // Labor: two masons on concrete (800); one is paid (400).
        var cast = new WorkforceApi.Cast(ex, people.Manager, people.Engineer, people.EngineerId, people.Accountant, project, people.ManagerId);
        var mason1 = await WorkforceApi.HireAsync(api, ex, project, WorkforceApi.Mason, "Saad", dailyRate: 400m);
        var mason2 = await WorkforceApi.HireAsync(api, ex, project, WorkforceApi.Mason, "Fathy", dailyRate: 400m);
        await WorkforceApi.RecordApprovedSheetAsync(api, cast, project, WorkforceApi.Today(api).AddDays(-1),
            WorkforceApi.Line(mason1, 1m, [(concrete, 1m)]), WorkforceApi.Line(mason2, 1m, [(concrete, 1m)]));
        await WorkforceApi.PaidAsync(api, people.Accountant, mason1, 400m);

        // Equipment: 1,500 fuel on concrete (custody, paid), 2,000 spare parts not linked (credit, unpaid).
        var machine = await FleetApi.RegisterAsync(api, ex, FleetApi.EquipmentBody("CF-EX-9"));
        await FleetApi.AssignedAsync(api, people.Manager, project, Id(machine), "2026-01-05");
        var fuel = await FleetApi.ReviewedAsync(api, people.Accountant, project, await FleetApi.RecordedCostAsync(api, people.Manager, project, FleetApi.FuelBody(Id(machine), 100m, 15m)), "approve");
        await Send(api, people.Manager, HttpMethod.Post, $"/api/projects/{project}/equipment-costs/{Id(fuel)}/item", new { projectItemId = concrete, rowVersion = Version(fuel) });
        var partsSupplier = await FleetApi.CreateSupplierAsync(api, ex, "Parts Co");
        await FleetApi.ReviewedAsync(api, people.Accountant, project, await FleetApi.RecordedCostAsync(api, people.Manager, project, new
        {
            equipmentId = Id(machine), costDate = "2026-01-20", type = "SpareParts", basis = "Lump", amount = 2_000m,
            description = "Filters and belts", supplierId = partsSupplier, channel = "Credit", invoiceNo = "INV-K-1",
        }), "approve");

        // Subcontract work: 3 m³ × 1,000 measured on finishing, approved.
        var company = Id(await Send(api, ex, HttpMethod.Post, "/api/subcontractors",
            new { nameAr = "شركة البناء", nameEn = "Builders Co", specialty = "Civil", contactName = "Eng. Sameh", phone = "+20 100 733 1188" }, HttpStatusCode.Created));
        var contract = await Send(api, people.Manager, HttpMethod.Post, $"/api/projects/{project}/subcontracts", new
        {
            subcontractorId = company, model = "UnitRate", value = 50_000m, retentionPercent = 0m, startDate = "2026-01-01", endDate = "2026-12-31", status = "Active", scope = "Tiling",
        }, HttpStatusCode.Created);
        var contractId = Id(contract.GetProperty("contract"));
        var line = await Send(api, people.Manager, HttpMethod.Post, $"/api/projects/{project}/subcontracts/{contractId}/items",
            new { name = "Floor tiling", workType = "Tiling", unit = "M3", quantity = 10m, unitPrice = 1_000m, projectItemId = finishing }, HttpStatusCode.Created);
        var measurement = await Send(api, people.Manager, HttpMethod.Post, $"/api/projects/{project}/subcontracts/{contractId}/measurements",
            new { contractItemId = Id(line), measuredOn = "2026-03-01", quantity = 3m, note = "Ground floor" }, HttpStatusCode.Created);
        await Send(api, ex, HttpMethod.Post, $"/api/projects/{project}/subcontract-measurements/{Id(measurement)}/approve", new { rowVersion = Version(measurement) });

        // Every line, from the source records:
        //   concrete  = supplies 2,000 (paid 1,500) + labor 800 (paid 400) + equipment 1,500 (paid 1,500) = 4,300 (paid 3,400)
        //   finishing = supplies 1,500 (paid 1,500) + subcontract 3,000 (paid 0)                          = 4,500 (paid 1,500)
        //   not linked = supplies 1,000 + equipment 2,000                                                    = 3,000 (paid 0)
        //   other site expenses = 300 (paid 300); waiting = 500
        var items = await CoreApi.ItemsAsync(api, people.Accountant, project);
        var c = ItemRow(items, concrete);
        Assert.Equal((2_000m, 800m, 1_500m, 0m, 4_300m, 3_400m, 900m), (Money(c, "supplierCost"), Money(c, "laborCost"), Money(c, "equipmentCost"),
            Money(c, "subcontractorCost"), Money(c, "totalCost"), Money(c, "paid"), Money(c, "outstanding")));
        var f = ItemRow(items, finishing);
        Assert.Equal((1_500m, 3_000m, 4_500m, 1_500m), (Money(f, "supplierCost"), Money(f, "subcontractorCost"), Money(f, "totalCost"), Money(f, "paid")));
        var unallocated = items.GetProperty("unallocated");
        Assert.Equal((1_000m, 2_000m, 3_000m, 0m), (Money(unallocated, "supplierCost"), Money(unallocated, "equipmentCost"), Money(unallocated, "totalCost"), Money(unallocated, "paid")));
        var reconciliation = items.GetProperty("reconciliation");
        Assert.Equal((8_800m, 3_000m, 300m, 12_100m), (Money(reconciliation, "itemsTotalCost"), Money(reconciliation, "unallocatedTotalCost"),
            Money(reconciliation, "otherCosts"), Money(reconciliation, "projectRecognizedCost")));

        // The item detail's lines add up to its row.
        var detail = await CoreApi.ItemDetailAsync(api, people.Accountant, project, concrete);
        var linesTotal = detail.GetProperty("supplierLines").EnumerateArray().Sum(row => Money(row, "amount"))
            + detail.GetProperty("laborLines").EnumerateArray().Sum(row => Money(row, "amount"))
            + detail.GetProperty("equipmentLines").EnumerateArray().Sum(row => Money(row, "amount"))
            + detail.GetProperty("subcontractLines").EnumerateArray().Sum(row => Money(row, "amount"));
        Assert.Equal(4_300m, linesTotal);
        Assert.Equal(1_000m, (await CoreApi.ItemDetailAsync(api, people.Accountant, project, null)).GetProperty("supplierLines").EnumerateArray().Sum(row => Money(row, "amount")));

        // The overview: each group and the grand total, the same figures.
        var overview = await Get(api, people.Accountant, $"/api/projects/{project}/overview");
        (decimal, decimal) G(string group) => (Money(Group(overview, group), "recognized"), Money(Group(overview, group), "paid"));
        Assert.Equal((4_500m, 3_000m), G("Materials"));
        Assert.Equal((300m, 300m), G("SiteExpenses"));
        Assert.Equal((800m, 400m), G("Labor"));
        Assert.Equal((3_500m, 1_500m), G("Equipment"));
        Assert.Equal((3_000m, 0m), G("Subcontractors"));
        Assert.Equal((0m, 0m), G("Staff"));
        var totals = overview.GetProperty("totals");
        Assert.Equal((12_100m, 5_200m, 6_900m, 500m), (Money(totals, "recognized"), Money(totals, "paid"), Money(totals, "outstanding"), Money(totals, "pendingReview")));

        // Suppliers: the project's balances and each supplier's company-wide history say the same.
        var balances = (await CoreApi.BalancesAsync(api, people.Accountant, project)).GetProperty("items").EnumerateArray()
            .ToDictionary(row => row.GetProperty("supplierId").GetInt32());
        Assert.Equal((2_000m, 1_500m, 3_000m, 500m), (Money(balances[cemex], "creditRecognized"), Money(balances[cemex], "custodyRecognized"), Money(balances[cemex], "paid"), Money(balances[cemex], "outstanding")));
        Assert.Equal((1_000m, 0m, 1_000m), (Money(balances[ezz], "creditRecognized"), Money(balances[ezz], "paid"), Money(balances[ezz], "outstanding")));
        var cemexHistory = await Get(api, people.Accountant, $"/api/suppliers/{cemex}/history");
        Assert.Equal((3_500m, 3_000m, 500m), (Money(cemexHistory, "recognized"), Money(cemexHistory, "paid"), Money(cemexHistory, "outstanding")));

        // The cost summary report is the same calculation.
        var report = await Get(api, people.Accountant, $"/api/reports/PRJ-COST?projectId={project}");
        Assert.Equal(12_100m, report.GetProperty("totals").GetProperty("recognized").GetDecimal());
    }
}
