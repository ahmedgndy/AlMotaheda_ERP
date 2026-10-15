using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Data;
using Microsoft.EntityFrameworkCore;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Core.CoreApi;

namespace AlMotaheda.Modules.Projects.Tests.Database.Core;

/// <summary>Recording, reviewing and resubmitting supplies and site expenses, end to end on SQL Server.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class ExpenseWorkflowTests(SqlServerFixture database)
{
    [DatabaseFact]
    public async Task A_supply_is_recorded_with_a_server_computed_amount_and_becomes_a_cost_only_when_someone_else_approves_it()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Cemex");

        // The client's amount is ignored: 120.5 bags × 118.75 = 14,309.375 → 14,309.38.
        var recorded = await api.RecordAsync(cast.Engineer, cast.ProjectId,
            SupplyBody(supplier, 120.5m, 118.75m, "Credit", new() { ["amount"] = 1m }));

        Assert.Equal("Recorded", recorded.GetProperty("status").GetString());
        Assert.Equal(14_309.38m, recorded.Money("amount"));
        Assert.Equal(0m, recorded.Money("recognized"));
        Assert.Equal(0m, recorded.Money("paid"));
        Assert.Equal(JsonValueKind.Null, recorded.GetProperty("paymentState").ValueKind);
        Assert.Equal(cast.Engineer.UserId, recorded.GetProperty("recorder").GetProperty("userId").GetInt32());
        Assert.Equal("Cemex", recorded.GetProperty("supplierNameEn").GetString());

        var own = await api.ReviewAsync(cast.Engineer, cast.ProjectId, recorded, "approve");
        Assert.Equal(HttpStatusCode.Conflict, own.StatusCode);
        Assert.Equal("own_record", await CodeOf(own));

        var approved = await api.ApproveAsync(cast.Accountant, cast.ProjectId, recorded);
        Assert.Equal("Approved", approved.GetProperty("status").GetString());
        Assert.Equal(14_309.38m, approved.Money("recognized"));
        Assert.Equal(0m, approved.Money("paid"));
        Assert.Equal(14_309.38m, approved.Money("outstanding"));
        Assert.Equal("Unpaid", approved.GetProperty("paymentState").GetString());
        Assert.Equal(cast.Accountant.UserId, approved.GetProperty("reviewer").GetProperty("userId").GetInt32());

        // Frozen: the recorder can't change it any more, and nobody can approve it twice.
        var edit = await api.SendAsync(HttpMethod.Put, $"/api/projects/{cast.ProjectId}/expenses/{approved.Id()}", cast.Engineer,
            EditBody(approved, new() { ["quantity"] = 1m }));
        Assert.Equal(HttpStatusCode.Conflict, edit.StatusCode);
        Assert.Equal("invalid_status", await CodeOf(edit));
        var again = await api.ReviewAsync(cast.Executive, cast.ProjectId, approved, "approve");
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal("invalid_status", await CodeOf(again));

        var summary = (await api.ListExpensesAsync(cast.Executive, cast.ProjectId)).GetProperty("summary");
        Assert.Equal(14_309.38m, summary.Money("suppliesRecognized"));
        Assert.Equal(0m, summary.Money("expensesRecognized"));
        Assert.Equal(14_309.38m, summary.Money("outstanding"));
        Assert.Equal(0, summary.GetProperty("pendingReviewCount").GetInt32());
    }

    [DatabaseFact]
    public async Task Fuel_and_equipment_suppliers_are_refused_because_their_costs_are_recorded_in_site_equipment()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var fuel = await api.CreateSupplierAsync(cast.Executive, "Misr Petroleum", kind: "Fuel");
        var rentals = await api.CreateSupplierAsync(cast.Executive, "Crane Rentals", kind: "Equipment");
        var cement = await api.CreateSupplierAsync(cast.Executive, "Cemex");

        var fuelSupply = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/expenses", cast.Engineer,
            SupplyBody(fuel, 500m, 15m, "Credit"));
        Assert.Equal(HttpStatusCode.Conflict, fuelSupply.StatusCode);
        Assert.Equal("use_equipment_costs", await CodeOf(fuelSupply));

        var rentalExpense = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/expenses", cast.Engineer,
            ExpenseBody(3_000m, "Credit", rentals));
        Assert.Equal(HttpStatusCode.Conflict, rentalExpense.StatusCode);
        Assert.Equal("use_equipment_costs", await CodeOf(rentalExpense));

        // A record can't be moved to such a supplier when it is fixed either.
        var recorded = await api.RecordAsync(cast.Engineer, cast.ProjectId, SupplyBody(cement, 10m, 100m, "Credit"));
        var moved = await api.SendAsync(HttpMethod.Put, $"/api/projects/{cast.ProjectId}/expenses/{recorded.Id()}", cast.Engineer,
            EditBody(recorded, new() { ["supplierId"] = fuel }));
        Assert.Equal(HttpStatusCode.Conflict, moved.StatusCode);
        Assert.Equal("use_equipment_costs", await CodeOf(moved));
        Assert.Equal(1, (await api.ListExpensesAsync(cast.Executive, cast.ProjectId)).GetProperty("items").GetArrayLength());
    }

    [DatabaseFact]
    public async Task A_returned_record_is_fixed_and_resubmitted_by_its_recorder_only_and_a_rejected_one_is_final()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var recorded = await api.RecordAsync(cast.Engineer, cast.ProjectId, ExpenseBody(450m, "Custody"));

        var noNote = await api.ReviewAsync(cast.Accountant, cast.ProjectId, recorded, "return");
        Assert.Equal(HttpStatusCode.BadRequest, noNote.StatusCode);
        Assert.Contains("note", (await Json(noNote)).GetProperty("errors").EnumerateObject().Select(field => field.Name));

        var returned = await Json(await api.ReviewAsync(cast.Accountant, cast.ProjectId, recorded, "return", "Attach the lab receipt"));
        Assert.Equal("Returned", returned.GetProperty("status").GetString());
        Assert.Equal("Attach the lab receipt", returned.GetProperty("reviewNote").GetString());
        Assert.Equal(1, (await api.ListExpensesAsync(cast.Executive, cast.ProjectId)).GetProperty("summary").GetProperty("returnedCount").GetInt32());

        // A returned record is in no total, and a reviewer can't approve it as it stands.
        var approveReturned = await api.ReviewAsync(cast.Accountant, cast.ProjectId, returned, "approve");
        Assert.Equal(HttpStatusCode.Conflict, approveReturned.StatusCode);

        // Someone else can't fix it; the recorder can.
        var byManager = await api.SendAsync(HttpMethod.Put, $"/api/projects/{cast.ProjectId}/expenses/{returned.Id()}", cast.Manager,
            EditBody(returned, new() { ["amount"] = 475m }));
        Assert.Equal(HttpStatusCode.Forbidden, byManager.StatusCode);

        var resubmitted = await api.SendAsync(HttpMethod.Put, $"/api/projects/{cast.ProjectId}/expenses/{returned.Id()}", cast.Engineer,
            EditBody(returned, new() { ["amount"] = 475m, ["notes"] = "Receipt attached" }));
        await EnsureStatus(resubmitted, HttpStatusCode.OK);
        var fixedRecord = await Json(resubmitted);
        Assert.Equal("Recorded", fixedRecord.GetProperty("status").GetString());
        Assert.Equal(475m, fixedRecord.Money("amount"));
        Assert.Equal(JsonValueKind.Null, fixedRecord.GetProperty("reviewNote").ValueKind);
        Assert.Equal(JsonValueKind.Null, fixedRecord.GetProperty("reviewer").ValueKind);

        var stale = await api.SendAsync(HttpMethod.Put, $"/api/projects/{cast.ProjectId}/expenses/{returned.Id()}", cast.Engineer,
            EditBody(returned, new() { ["amount"] = 480m }));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("concurrency_conflict", await CodeOf(stale));

        var rejected = await Json(await api.ReviewAsync(cast.Executive, cast.ProjectId, fixedRecord, "reject", "Not a project cost"));
        Assert.Equal("Rejected", rejected.GetProperty("status").GetString());
        var editRejected = await api.SendAsync(HttpMethod.Put, $"/api/projects/{cast.ProjectId}/expenses/{rejected.Id()}", cast.Engineer,
            EditBody(rejected, new() { ["amount"] = 1m }));
        Assert.Equal(HttpStatusCode.Conflict, editRejected.StatusCode);

        var summary = (await api.ListExpensesAsync(cast.Executive, cast.ProjectId)).GetProperty("summary");
        Assert.Equal(0m, summary.Money("recognizedTotal"));
        Assert.Equal(0, summary.GetProperty("pendingReviewCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("returnedCount").GetInt32());
    }

    [DatabaseFact]
    public async Task Custody_is_paid_at_approval_and_credit_only_through_allocations()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Steel House", "Materials");

        var custody = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(supplier, 10m, 100m, "Custody"));
        var credit = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(supplier, 20m, 100m, "Credit"));

        Assert.Equal(1_000m, custody.Money("paid"));
        Assert.Equal(0m, custody.Money("outstanding"));
        Assert.Equal("Paid", custody.GetProperty("paymentState").GetString());
        Assert.Equal(0m, credit.Money("paid"));
        Assert.Equal(2_000m, credit.Money("outstanding"));
        Assert.Equal("Unpaid", credit.GetProperty("paymentState").GetString());

        var payment = await api.PaidAsync(cast.Accountant, cast.ProjectId, supplier, 1_500m, (credit.Id(), 1_500m));
        var partly = await api.GetExpenseAsync(cast.Executive, cast.ProjectId, credit.Id());
        Assert.Equal(1_500m, partly.Money("paid"));
        Assert.Equal(500m, partly.Money("outstanding"));
        Assert.Equal("PartiallyPaid", partly.GetProperty("paymentState").GetString());
        var shown = Assert.Single(partly.GetProperty("payments").EnumerateArray());
        Assert.Equal(payment.Id(), shown.GetProperty("paymentId").GetInt32());
        Assert.Equal(1_500m, shown.Money("amount"));

        var balances = await api.BalancesAsync(cast.Executive, cast.ProjectId);
        var balance = Assert.Single(balances.GetProperty("items").EnumerateArray());
        Assert.Equal(2_000m, balance.Money("creditRecognized"));
        Assert.Equal(1_000m, balance.Money("custodyRecognized"));
        Assert.Equal(2_500m, balance.Money("paid"));
        Assert.Equal(500m, balance.Money("outstanding"));
        Assert.Equal(1, balance.GetProperty("openInvoices").GetInt32());

        // A voided payment pays nothing: the invoice is open again, and the summary agrees.
        await EnsureStatus(await api.VoidAsync(cast.Executive, cast.ProjectId, payment), HttpStatusCode.OK);
        var unpaidAgain = await api.GetExpenseAsync(cast.Executive, cast.ProjectId, credit.Id());
        Assert.Equal(0m, unpaidAgain.Money("paid"));
        Assert.Empty(unpaidAgain.GetProperty("payments").EnumerateArray());
        var summary = (await api.ListExpensesAsync(cast.Executive, cast.ProjectId)).GetProperty("summary");
        Assert.Equal(3_000m, summary.Money("recognizedTotal"));
        Assert.Equal(1_000m, summary.Money("paid"));
        Assert.Equal(2_000m, summary.Money("outstanding"));

        var paymentFilter = await api.ListExpensesAsync(cast.Executive, cast.ProjectId, "&paymentState=Unpaid");
        Assert.Equal([credit.Id()], paymentFilter.GetProperty("items").EnumerateArray().Select(item => item.Id()));
        var paidFilter = await api.ListExpensesAsync(cast.Executive, cast.ProjectId, "&paymentState=Paid");
        Assert.Equal([custody.Id()], paidFilter.GetProperty("items").EnumerateArray().Select(item => item.Id()));
    }

    [DatabaseFact]
    public async Task Without_see_money_an_engineer_sees_only_the_records_they_recorded()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var mine = await api.RecordAsync(cast.Engineer, cast.ProjectId, ExpenseBody(100m, "Custody"));
        var theirs = await api.RecordAsync(cast.Manager, cast.ProjectId, ExpenseBody(200m, "Custody"));

        var list = await api.ListExpensesAsync(cast.Engineer, cast.ProjectId);
        Assert.Equal([mine.Id()], list.GetProperty("items").EnumerateArray().Select(item => item.Id()));
        Assert.Equal(JsonValueKind.Null, list.GetProperty("summary").ValueKind);

        var other = await api.SendAsync(HttpMethod.Get, $"/api/projects/{cast.ProjectId}/expenses/{theirs.Id()}", cast.Engineer);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);

        var all = await api.ListExpensesAsync(cast.Manager, cast.ProjectId, "&sort=amount");
        Assert.Equal([theirs.Id(), mine.Id()], all.GetProperty("items").EnumerateArray().Select(item => item.Id()));
        Assert.Equal(2, all.GetProperty("summary").GetProperty("pendingReviewCount").GetInt32());
        Assert.Equal(300m, all.GetProperty("summary").Money("pendingReviewAmount"));
    }

    [DatabaseFact]
    public async Task Suppliers_are_unique_by_english_name_and_inactive_ones_cannot_be_chosen()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Nile Aggregates");

        var duplicate = await api.SendAsync(HttpMethod.Post, "/api/suppliers", cast.Executive,
            new { nameAr = "x", nameEn = "nile aggregates", kind = "Materials" });
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("supplier_exists", await CodeOf(duplicate));

        var list = await Json(await api.SendAsync(HttpMethod.Get, "/api/suppliers?search=nile", cast.Engineer));
        var row = Assert.Single(list.GetProperty("items").EnumerateArray());
        var deactivated = await api.SendAsync(HttpMethod.Put, $"/api/suppliers/{supplier}", cast.Manager, new
        {
            nameAr = row.GetProperty("nameAr").GetString(),
            nameEn = row.GetProperty("nameEn").GetString(),
            kind = "Materials",
            status = "Inactive",
            rowVersion = row.GetProperty("rowVersion").GetString(),
        });
        await EnsureStatus(deactivated, HttpStatusCode.OK);

        var refused = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/expenses", cast.Engineer, SupplyBody(supplier, 1m, 1m, "Credit"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("supplierId", (await Json(refused)).GetProperty("errors").EnumerateObject().Select(field => field.Name));

        var stored = await api.QueryAsync(db => db.Suppliers.AsNoTracking().SingleAsync(candidate => candidate.Id == supplier));
        Assert.Equal(SupplierStatus.Inactive, stored.Status);
        Assert.Equal(cast.Manager.UserId, stored.UpdatedBy);
    }

    [DatabaseFact]
    public async Task The_database_refuses_bad_expense_rows_even_if_the_code_had_a_bug()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Any");

        Task<int> Insert(Action<ProjectsDbContext> add) => api.QueryAsync(db =>
        {
            add(db);
            return db.SaveChangesAsync();
        });

        // Credit without a supplier.
        var creditNoSupplier = await Assert.ThrowsAsync<DbUpdateException>(() => Insert(db => db.ProjectExpenses.Add(new ProjectExpense
        {
            ProjectId = cast.ProjectId, Kind = ExpenseKind.Expense, ExpenseDate = new DateOnly(2026, 1, 1), Category = ExpenseCategory.Lab,
            Description = "x", Amount = 10, Channel = PaymentChannel.Credit, CreatedBy = cast.Engineer.UserId,
        })));
        Assert.Contains("CK_ProjectExpense_CreditSupplier", creditNoSupplier.InnerException!.Message);

        // A supply whose amount is not quantity × unit price.
        var wrongAmount = await Assert.ThrowsAsync<DbUpdateException>(() => Insert(db => db.ProjectExpenses.Add(new ProjectExpense
        {
            ProjectId = cast.ProjectId, Kind = ExpenseKind.Supply, ExpenseDate = new DateOnly(2026, 1, 1), SupplierId = supplier,
            ItemName = "x", Unit = "Bag", Quantity = 2, UnitPrice = 10, Amount = 30, Channel = PaymentChannel.Custody, CreatedBy = cast.Engineer.UserId,
        })));
        Assert.Contains("CK_ProjectExpense_KindColumns", wrongAmount.InnerException!.Message);

        // A payment allocation of zero.
        var approved = await api.RecordApprovedAsync(cast.Engineer, cast.Accountant, cast.ProjectId, SupplyBody(supplier, 1m, 100m, "Credit"));
        var zeroAllocation = await Assert.ThrowsAsync<DbUpdateException>(() => Insert(db =>
        {
            var payment = new SupplierPayment
            {
                ProjectId = cast.ProjectId, SupplierId = supplier, PaymentDate = new DateOnly(2026, 1, 1), Amount = 1,
                Method = PaymentMethod.Cash, IdempotencyKey = Guid.NewGuid(), CreatedBy = cast.Accountant.UserId,
            };
            payment.Allocations.Add(new SupplierPaymentAllocation { ExpenseId = approved.Id(), Amount = 0 });
            db.SupplierPayments.Add(payment);
        }));
        Assert.Contains("CK_SupplierPaymentAllocation_Amount", zeroAllocation.InnerException!.Message);
    }
}
