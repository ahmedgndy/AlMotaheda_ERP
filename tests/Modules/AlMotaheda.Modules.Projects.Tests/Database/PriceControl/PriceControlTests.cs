using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Tests.Database.CostManagement;
using AlMotaheda.Modules.Projects.Tests.Database.Core;
using Microsoft.EntityFrameworkCore;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Projects.Tests.Database.Core.CoreApi;

namespace AlMotaheda.Modules.Projects.Tests.Database.PriceControl;

/// <summary>
/// Central Price Control on SQL Server, under the gating policy: a price inside its range passes; one above it is a
/// true record that waits for the Executive, with the accountant held back on every path; the three decisions; the
/// same rules in every project; retries, concurrent decisions, and who may configure the catalog.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class PriceControlTests(SqlServerFixture database)
{
    private static string Today => DateTime.UtcNow.ToString("yyyy-MM-dd");

    [DatabaseFact]
    public async Task A_price_inside_its_range_is_recorded_approved_and_paid_with_no_case()
    {
        await using var api = await PriceControlApi.StartGatedAsync(database);
        var cast = await api.CastAsync();
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var cement = await api.CreatePriceItemAsync(cast.Executive, "Cement 42.5", "Ton", 1_000m, 1_200m);

        var recorded = await api.RecordAsync(cast.Engineer, cast.ProjectId, PriceControlApi.CementPurchase(supplier, cement, 1_100m));
        var check = recorded.GetProperty("priceCheck");
        Assert.Equal("WithinRange", check.GetProperty("result").GetString());
        Assert.Equal(JsonValueKind.Null, check.GetProperty("exception").ValueKind);

        var approved = await api.ApproveAsync(cast.Accountant, cast.ProjectId, recorded);
        await api.PaidAsync(cast.Accountant, cast.ProjectId, supplier, 2_200m, (approved.Id(), 2_200m));

        Assert.Equal(0, await api.QueryAsync(db => db.PriceExceptions.CountAsync()));
        Assert.Equal(0, (await api.ExceptionsAsync(cast.Executive, "?status=all")).GetProperty("totalCount").GetInt32());
    }

    [DatabaseFact]
    public async Task Cement_at_1400_against_a_1200_ceiling_waits_for_the_executive_who_approves_it_once_and_the_catalog_stays()
    {
        await using var api = await PriceControlApi.StartGatedAsync(database);
        var cast = await api.CastAsync();
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var cement = await api.CreatePriceItemAsync(cast.Executive, "Cement 42.5", "Ton", 1_000m, 1_200m);

        // The engineer's record is saved as it was entered, with the case opened in the same save.
        var recorded = await api.RecordAsync(cast.Engineer, cast.ProjectId, PriceControlApi.CementPurchase(supplier, cement, 1_400m, "Only stock available for Thursday"));
        Assert.Equal("Recorded", recorded.GetProperty("status").GetString());
        var opened = recorded.GetProperty("priceCheck").GetProperty("exception");
        Assert.Equal("Pending", opened.GetProperty("status").GetString());
        var exceptionId = opened.Id();

        // The case carries everything the Executive needs: who, where, what, the two prices and the variance.
        var exception = await api.ExceptionAsync(cast.Executive, exceptionId);
        Assert.Equal((cast.ProjectId, supplier, cement, "Ton", 2m), (exception.GetProperty("projectId").GetInt32(), exception.GetProperty("supplierId").GetInt32(),
            exception.GetProperty("priceItemId").GetInt32(), exception.GetProperty("unit").GetString(), exception.Money("quantity")));
        Assert.Equal((1_400m, 1_000m, 1_200m), (exception.Money("unitPrice"), exception.Money("referenceMinPrice"), exception.Money("referenceMaxPrice")));
        Assert.Equal((200m, 400m, 16.67m), (exception.Money("variancePerUnit"), exception.Money("varianceTotal"), exception.Money("variancePercent")));
        Assert.Equal((cast.Engineer.UserId, "Only stock available for Thursday", "ProjectExpense", recorded.Id()),
            (exception.GetProperty("submittedBy").GetInt32(), exception.GetProperty("reason").GetString(), exception.GetProperty("sourceType").GetString(), exception.GetProperty("sourceId").GetInt32()));

        // The Executive, the accountant and the manager are told (never the engineer); the pending list shows it to them and to its submitter.
        var told = await api.QueryAsync(db => db.PriceExceptionAlerts.Where(alert => alert.ExceptionId == exceptionId).Select(alert => alert.RecipientUserId).ToListAsync());
        Assert.Contains(cast.Executive.UserId, told);
        Assert.Contains(cast.Accountant.UserId, told);
        Assert.Contains(cast.Manager.UserId, told);
        Assert.DoesNotContain(cast.Engineer.UserId, told);
        Assert.Equal(1, (await api.ExceptionsAsync(cast.Executive)).GetProperty("totalCount").GetInt32());
        Assert.Equal(1, (await api.ExceptionsAsync(cast.Engineer)).GetProperty("totalCount").GetInt32());

        // The accountant can neither approve the record nor pay it, from any screen; nor decide the case.
        var approveEarly = await api.ReviewAsync(cast.Accountant, cast.ProjectId, recorded, "approve");
        Assert.Equal((HttpStatusCode.Conflict, "price_exception_pending"), (approveEarly.StatusCode, await CodeOf(approveEarly)));
        var payEarly = await api.PayAsync(cast.Accountant, cast.ProjectId, CoreApi.PaymentBody(supplier, 2_800m, [(recorded.Id(), 2_800m)]));
        Assert.NotEqual(HttpStatusCode.Created, payEarly.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.DecideAsync(cast.Accountant, exceptionId, "approve", "Fine by me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.DecideAsync(cast.Manager, exceptionId, "approve", "Fine by me")).StatusCode);

        // The Executive decides; a reason is required, and a case is decided once.
        var noNote = await api.DecideAsync(cast.Executive, exceptionId, "approve", null);
        Assert.Equal(["note"], await AlMotaheda.Api.Tests.ApiCalls.ErrorFields(noNote));
        var decided = await api.DecidedAsync(cast.Executive, exceptionId, "approve", "Accepted this once for the pour");
        Assert.Equal(("ApprovedOnce", cast.Executive.UserId, "Accepted this once for the pour"),
            (decided.GetProperty("status").GetString(), decided.GetProperty("decidedBy").GetInt32(), decided.GetProperty("decisionNote").GetString()));
        var twice = await api.DecideAsync(cast.Executive, exceptionId, "reject", "Changed my mind");
        Assert.Equal(HttpStatusCode.Conflict, twice.StatusCode);

        // One-time: the company reference is unchanged, and the next purchase at 1,400 opens a new case.
        var again = await Json(await api.SendAsync(HttpMethod.Get, $"/api/price-items/{cement}/check?unitPrice=1400&quantity=1&date={Today}", cast.Engineer));
        Assert.Equal(("AboveRange", 1_200m), (again.GetProperty("result").GetString(), again.Money("maxPrice")));

        // Approval of the price is not the approval of the record, nor its payment: those follow the usual path.
        var fresh = await api.GetExpenseAsync(cast.Accountant, cast.ProjectId, recorded.Id());
        Assert.Equal("Recorded", fresh.GetProperty("status").GetString());
        var approved = await api.ApproveAsync(cast.Accountant, cast.ProjectId, fresh);
        await api.PaidAsync(cast.Accountant, cast.ProjectId, supplier, 2_800m, (approved.Id(), 2_800m));
        Assert.Equal(1_200m, approved.GetProperty("priceCheck").Money("maxPrice"));

        var summary = await Json(await api.SendAsync(HttpMethod.Get, "/api/price-exceptions/summary", cast.Executive));
        Assert.Equal((1, 1, 0), (summary.GetProperty("total").GetInt32(), summary.GetProperty("approvedOnce").GetInt32(), summary.GetProperty("pending").GetInt32()));
        Assert.Equal(400m, summary.GetProperty("byProject")[0].Money("varianceTotal"));
    }

    [DatabaseFact]
    public async Task Decision_B_sets_the_company_reference_for_every_project_from_its_date_and_leaves_the_history_alone()
    {
        await using var api = await PriceControlApi.StartGatedAsync(database);
        var cast = await api.CastAsync("PRJ-A");
        var (otherManagerId, otherManager) = await api.CreateSignedInUserAsync(cast.Executive, "manager");
        var projectB = (await api.CreateProjectAsync(cast.Executive, "PRJ-B", otherManagerId)).Id();
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var cement = await api.CreatePriceItemAsync(cast.Executive, "Cement 42.5", "Ton", 1_000m, 1_200m);

        // Project B is held by the same rule as project A.
        var inB = await api.RecordAsync(otherManager, projectB, PriceControlApi.CementPurchase(supplier, cement, 1_400m, "Market went up"));
        Assert.Equal("Pending", inB.GetProperty("priceCheck").GetProperty("exception").GetProperty("status").GetString());
        var inA = await api.RecordAsync(cast.Engineer, cast.ProjectId, PriceControlApi.CementPurchase(supplier, cement, 1_400m, "Market went up"));
        var caseA = inA.GetProperty("priceCheck").GetProperty("exception").Id();

        // The Executive approves A and raises the company range from today; a range below the price is refused.
        var upsideDown = await api.DecideAsync(cast.Executive, caseA, "approve-with-price", "Market price", new() { ["minPrice"] = 1_500m, ["maxPrice"] = 1_300m, ["effectiveFrom"] = Today });
        Assert.Equal(["maxPrice"], await AlMotaheda.Api.Tests.ApiCalls.ErrorFields(upsideDown));
        var changed = await api.DecidedAsync(cast.Executive, caseA, "approve-with-price", "Market price since October", new()
        {
            ["minPrice"] = 1_300m, ["maxPrice"] = 1_500m, ["referencePrice"] = 1_400m, ["effectiveFrom"] = Today, ["sourceNote"] = "Cemex October list",
        });
        Assert.Equal("ApprovedWithPriceChange", changed.GetProperty("status").GetString());
        var newRangeId = changed.GetProperty("newRangeId").GetInt32();

        // The item's history shows the new approved range and who set it; the old record keeps the comparison it was made with.
        var item = await Json(await api.SendAsync(HttpMethod.Get, $"/api/price-items/{cement}", cast.Engineer));
        var ranges = item.GetProperty("ranges").EnumerateArray().ToList();
        Assert.Equal(2, ranges.Count);
        var newRange = ranges.Single(range => range.Id() == newRangeId);
        Assert.Equal(("Approved", 1_300m, 1_500m, 1_400m), (newRange.GetProperty("status").GetString(), newRange.Money("minPrice"), newRange.Money("maxPrice"), newRange.Money("referencePrice")));
        Assert.Equal(1_200m, (await api.GetExpenseAsync(cast.Executive, cast.ProjectId, inA.Id())).GetProperty("priceCheck").Money("maxPrice"));

        // From today 1,400 is inside the range in every project; B's open case is not decided by A's.
        var nextInB = await api.RecordAsync(otherManager, projectB, PriceControlApi.CementPurchase(supplier, cement, 1_400m));
        Assert.Equal(("WithinRange", JsonValueKind.Null), (nextInB.GetProperty("priceCheck").GetProperty("result").GetString(), nextInB.GetProperty("priceCheck").GetProperty("exception").ValueKind));
        Assert.Equal("Pending", (await api.ExceptionAsync(cast.Executive, inB.GetProperty("priceCheck").GetProperty("exception").Id())).GetProperty("status").GetString());
        var tooDear = await api.RecordAsync(otherManager, projectB, PriceControlApi.CementPurchase(supplier, cement, 1_600m, "Express delivery"));
        Assert.Equal("Pending", tooDear.GetProperty("priceCheck").GetProperty("exception").GetProperty("status").GetString());

        // The changes report names the case the new range came from.
        var report = await api.SendAsync(HttpMethod.Get, "/api/reports/PRICE-CHANGES", cast.Executive);
        await EnsureStatus(report, HttpStatusCode.OK);
        Assert.Contains($"Price exception #{caseA}", await report.Content.ReadAsStringAsync());
    }

    [DatabaseFact]
    public async Task Decision_C_rejects_with_a_reason_and_the_record_stays_blocked_until_its_price_is_corrected()
    {
        await using var api = await PriceControlApi.StartGatedAsync(database);
        var cast = await api.CastAsync();
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var cement = await api.CreatePriceItemAsync(cast.Executive, "Cement 42.5", "Ton", 1_000m, 1_200m);
        var recorded = await api.RecordAsync(cast.Engineer, cast.ProjectId, PriceControlApi.CementPurchase(supplier, cement, 1_400m, "Urgent"));
        var exceptionId = recorded.GetProperty("priceCheck").GetProperty("exception").Id();

        Assert.Equal(["note"], await AlMotaheda.Api.Tests.ApiCalls.ErrorFields(await api.DecideAsync(cast.Executive, exceptionId, "reject", "")));
        var rejected = await api.DecidedAsync(cast.Executive, exceptionId, "reject", "Buy from Suez at the list price");
        Assert.Equal("Rejected", rejected.GetProperty("status").GetString());

        // Still blocked, with the reason to the reviewer; the record itself is kept.
        var approve = await api.ReviewAsync(cast.Accountant, cast.ProjectId, recorded, "approve");
        Assert.Equal((HttpStatusCode.Conflict, "price_exception_rejected"), (approve.StatusCode, await CodeOf(approve)));
        Assert.Equal("Recorded", (await api.GetExpenseAsync(cast.Engineer, cast.ProjectId, recorded.Id())).GetProperty("status").GetString());

        // The engineer corrects the price: the rejected case is closed as withdrawn, no new case, and the record goes through.
        var current = await api.GetExpenseAsync(cast.Engineer, cast.ProjectId, recorded.Id());
        var edited = await api.SendAsync(HttpMethod.Put, $"/api/projects/{cast.ProjectId}/expenses/{recorded.Id()}", cast.Engineer,
            PriceControlApi.EditBody(current, new() { ["unitPrice"] = 1_150m, ["priceJustification"] = null }));
        await EnsureStatus(edited, HttpStatusCode.OK);
        var corrected = await Json(edited);
        Assert.Equal(("WithinRange", JsonValueKind.Null), (corrected.GetProperty("priceCheck").GetProperty("result").GetString(), corrected.GetProperty("priceCheck").GetProperty("exception").ValueKind));
        Assert.Equal("Withdrawn", (await api.ExceptionAsync(cast.Executive, exceptionId)).GetProperty("status").GetString());
        await api.ApproveAsync(cast.Accountant, cast.ProjectId, corrected);
        Assert.Equal(1, await api.QueryAsync(db => db.PriceExceptions.CountAsync()));
    }

    [DatabaseFact]
    public async Task A_retry_after_the_reference_changed_neither_duplicates_the_record_nor_its_case_and_a_new_record_is_checked_afresh()
    {
        await using var api = await PriceControlApi.StartGatedAsync(database);
        var cast = await api.CastAsync();
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var cement = await api.CreatePriceItemAsync(cast.Executive, "Cement 42.5", "Ton", 1_000m, 1_200m);
        var body = PriceControlApi.CementPurchase(supplier, cement, 1_400m, "Urgent", new() { ["idempotencyKey"] = Guid.NewGuid().ToString() });

        var first = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/expenses", cast.Engineer, body);
        await EnsureStatus(first, HttpStatusCode.Created);
        await api.ChangeRangeAsync(cast.Manager, cast.Executive, cement, 1_300m, 1_500m);

        // The phone sends the same save again after the catalog changed: the first record answers, its case is the only one.
        var retry = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/expenses", cast.Engineer, body);
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal((await Json(first)).Id(), (await Json(retry)).Id());
        Assert.Equal((1, 1), (await api.QueryAsync(db => db.ProjectExpenses.CountAsync()), await api.QueryAsync(db => db.PriceExceptions.CountAsync())));

        // A new save is compared with the reference of its day.
        var next = await api.RecordAsync(cast.Engineer, cast.ProjectId, PriceControlApi.CementPurchase(supplier, cement, 1_400m));
        Assert.Equal("WithinRange", next.GetProperty("priceCheck").GetProperty("result").GetString());
        Assert.Equal(1, await api.QueryAsync(db => db.PriceExceptions.CountAsync()));
    }

    [DatabaseFact]
    public async Task Two_decisions_on_the_same_case_at_once_land_exactly_one()
    {
        await using var api = await PriceControlApi.StartGatedAsync(database);
        var cast = await api.CastAsync();
        var (_, secondExecutive) = await api.CreateSignedInUserAsync(cast.Executive, "executive");
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var cement = await api.CreatePriceItemAsync(cast.Executive, "Cement 42.5", "Ton", 1_000m, 1_200m);
        var recorded = await api.RecordAsync(cast.Engineer, cast.ProjectId, PriceControlApi.CementPurchase(supplier, cement, 1_400m, "Urgent"));
        var exceptionId = recorded.GetProperty("priceCheck").GetProperty("exception").Id();
        var rowVersion = (await api.ExceptionAsync(cast.Executive, exceptionId)).GetProperty("rowVersion").GetString();

        var responses = await Task.WhenAll(
            api.SendAsync(HttpMethod.Post, $"/api/price-exceptions/{exceptionId}/approve", cast.Executive, new { note = "Fine", rowVersion }),
            api.SendAsync(HttpMethod.Post, $"/api/price-exceptions/{exceptionId}/reject", secondExecutive, new { note = "No", rowVersion }));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        var final = await api.ExceptionAsync(cast.Executive, exceptionId);
        Assert.Contains(final.GetProperty("status").GetString(), new[] { "ApprovedOnce", "Rejected" });
        Assert.Equal(1, await api.QueryAsync(db => db.PriceExceptions.CountAsync(exception => exception.DecidedAt != null)));
    }

    [DatabaseFact]
    public async Task Categories_and_units_are_configured_by_the_price_owner_and_an_unknown_or_retired_one_is_refused()
    {
        await using var api = await ProjectsApi.StartAsync(database);
        var cast = await api.CastAsync();

        // Seeded with the old classifications and units, plus the company's other spending.
        var categories = await Json(await api.SendAsync(HttpMethod.Get, "/api/price-categories", cast.Engineer));
        var codes = categories.GetProperty("items").EnumerateArray().Select(category => category.GetProperty("code").GetString()).ToList();
        Assert.Contains("CementAggregates", codes);
        Assert.Contains("Meals", codes);
        Assert.Contains("Fuel", codes);
        var units = await Json(await api.SendAsync(HttpMethod.Get, "/api/price-units", cast.Engineer));
        Assert.Contains("WorkerDay", units.GetProperty("items").EnumerateArray().Select(unit => unit.GetProperty("code").GetString()));

        // Only the price owner adds to them; a code is unique.
        var byEngineer = await api.SendAsync(HttpMethod.Post, "/api/price-categories", cast.Engineer, new { code = "Scaffolding", nameAr = "سقالات", nameEn = "Scaffolding" });
        Assert.Equal(HttpStatusCode.Forbidden, byEngineer.StatusCode);
        var duplicate = await api.SendAsync(HttpMethod.Post, "/api/price-categories", cast.Executive, new { code = "Meals", nameAr = "وجبات", nameEn = "Meals again" });
        Assert.Equal((HttpStatusCode.Conflict, "price_category_exists"), (duplicate.StatusCode, await CodeOf(duplicate)));
        await EnsureStatus(await api.SendAsync(HttpMethod.Post, "/api/price-categories", cast.Executive, new { code = "Scaffolding", nameAr = "سقالات", nameEn = "Scaffolding" }), HttpStatusCode.Created);
        var barrel = await Json(await api.SendAsync(HttpMethod.Post, "/api/price-units", cast.Executive, new { code = "Barrel", nameAr = "برميل", nameEn = "Barrel" }));

        // An item in the new category and unit; unknown ones are refused by field.
        var created = await api.SendAsync(HttpMethod.Post, "/api/price-items", cast.Executive, new
        {
            nameAr = "زيت", nameEn = "Form oil", classification = "Scaffolding", unit = "Barrel", minPrice = 900m, maxPrice = 1_000m,
        });
        await EnsureStatus(created, HttpStatusCode.Created);
        var unknownCategory = await api.SendAsync(HttpMethod.Post, "/api/price-items", cast.Executive, new
        {
            nameAr = "طعام", nameEn = "Food", classification = "Food", unit = "Keg", minPrice = 1m, maxPrice = 2m,
        });
        Assert.Equal(["classification", "unit"], (await AlMotaheda.Api.Tests.ApiCalls.ErrorFields(unknownCategory)).Order());
        var listed = await Json(await api.SendAsync(HttpMethod.Get, "/api/price-items?classification=Scaffolding", cast.Engineer));
        var row = Assert.Single(listed.GetProperty("items").EnumerateArray());
        Assert.Equal(("Scaffolding", "Scaffolding"), (row.GetProperty("classification").GetString(), row.GetProperty("categoryNameEn").GetString()));

        // A fixed price with a tolerance is stored as the range it allows.
        var fixedPrice = await api.SendAsync(HttpMethod.Post, "/api/price-items", cast.Executive, new
        {
            nameAr = "وجبة", nameEn = "Worker meal", classification = "Meals", unit = "Meal", kind = "Fixed", referencePrice = 60m, tolerancePercent = 10m,
        });
        await EnsureStatus(fixedPrice, HttpStatusCode.Created);
        var meal = (await Json(fixedPrice)).GetProperty("item");
        Assert.Equal((54m, 66m, 60m), (meal.GetProperty("currentRange").Money("minPrice"), meal.GetProperty("currentRange").Money("maxPrice"), meal.GetProperty("currentRange").Money("referencePrice")));

        // A retired unit takes no new item.
        await EnsureStatus(await api.SendAsync(HttpMethod.Put, $"/api/price-units/{barrel.Id()}", cast.Executive,
            new { nameAr = "برميل", nameEn = "Barrel", isActive = false, rowVersion = barrel.GetProperty("rowVersion").GetString() }), HttpStatusCode.OK);
        var retired = await api.SendAsync(HttpMethod.Post, "/api/price-items", cast.Executive, new
        {
            nameAr = "شحم", nameEn = "Grease", classification = "Consumables", unit = "Barrel", minPrice = 1m, maxPrice = 2m,
        });
        Assert.Equal(["unit"], await AlMotaheda.Api.Tests.ApiCalls.ErrorFields(retired));

        // A record naming a price item that doesn't exist is refused, not waved through.
        var supplier = await api.CreateSupplierAsync(cast.Executive, "Cemex");
        var ghost = await api.SendAsync(HttpMethod.Post, $"/api/projects/{cast.ProjectId}/expenses", cast.Engineer, PriceControlApi.CementPurchase(supplier, 9_999, 1_000m));
        Assert.Equal(["priceItemId"], await AlMotaheda.Api.Tests.ApiCalls.ErrorFields(ghost));
    }
}
