using System.Net;
using System.Text.Json;
using AlMotaheda.Modules.Access.Tests.Database;
using AlMotaheda.Modules.Office.Data;
using Microsoft.EntityFrameworkCore;
using static AlMotaheda.Modules.Access.Tests.Database.AccessApi;
using static AlMotaheda.Modules.Office.Tests.Database.OfficeApi;

namespace AlMotaheda.Modules.Office.Tests.Database;

/// <summary>
/// The office custody on SQL Server, scenarios A to P of the office brief: funding counted only when received, routine
/// and high-risk expenses, receipts, prices, duplicates, returns, repairs, emergencies, cash counts, reversals, returns
/// of cash, closing, retries and double taps, and who may see and do what.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class OfficeFlowTests(SqlServerFixture database)
{
    /// <summary>A, B, C and P: funding moves the balance only when received; a routine expense posts once; people see only their own.</summary>
    [DatabaseFact]
    public async Task Funding_counts_only_when_received_and_a_routine_expense_posts_once()
    {
        await using var api = await StartAsync(database);
        var cast = await api.CastAsync();

        // A: the custodian asks for 10,000; agreeing it changes nothing in the cash.
        var funding = await api.CreatedAsync(cast.Custodian, "/api/office/funding-requests",
            new { custodyId = cast.CustodyId, amount = 10_000m, reason = "Opening cash", idempotencyKey = Guid.NewGuid() });
        Assert.Equal(10_000m, (await api.BalanceAsync(cast.Custodian, cast.CustodyId)).Money("fundingRequested"));

        // The Executive can't agree funding (they decide its payment in Finance); the Accountant can.
        var byExecutive = await api.SendAsync(HttpMethod.Post, $"/api/office/funding-requests/{funding.Id()}/approve", cast.Executive,
            new { method = "Cash", rowVersion = funding.RowVersion() });
        Assert.Equal("finance_separation", await CodeOf(byExecutive));
        funding = await api.OkAsync(cast.Accountant, HttpMethod.Post, $"/api/office/funding-requests/{funding.Id()}/approve",
            new { method = "Cash", rowVersion = funding.RowVersion() });
        var balance = await api.BalanceAsync(cast.Custodian, cast.CustodyId);
        Assert.Equal((0m, 10_000m), (balance.Money("recorded"), balance.Money("fundingApproved")));

        // Finance pays: disbursed, still not in the custody until the custodian confirms.
        await api.PayInFinanceAsync(cast.Executive, funding.GetProperty("paymentRequestId").GetInt32());
        funding = await api.GetAsync(cast.Custodian, $"/api/office/funding-requests/{funding.Id()}");
        Assert.Equal(("Disbursed", 10_000m), (funding.Status(), funding.Money("disbursedAmount")));
        balance = await api.BalanceAsync(cast.Custodian, cast.CustodyId);
        Assert.Equal((0m, 10_000m), (balance.Money("recorded"), balance.Money("fundingDisbursed")));
        Assert.NotEmpty(await api.NotificationsAsync(cast.Custodian, "OfficeFundsDisbursed"));

        // Only the custodian confirms; a retried confirmation posts nothing more.
        var notHolder = await api.SendAsync(HttpMethod.Post, $"/api/office/funding-requests/{funding.Id()}/confirm-receipt", cast.OtherCustodian,
            new { rowVersion = funding.RowVersion() });
        Assert.Equal(HttpStatusCode.NotFound, notHolder.StatusCode);
        var confirmBody = new { rowVersion = funding.RowVersion() };
        await api.OkAsync(cast.Custodian, HttpMethod.Post, $"/api/office/funding-requests/{funding.Id()}/confirm-receipt", confirmBody);
        var retry = await api.OkAsync(cast.Custodian, HttpMethod.Post, $"/api/office/funding-requests/{funding.Id()}/confirm-receipt", confirmBody);
        Assert.Equal(("Received", 10_000m), (retry.Status(), retry.Money("receivedAmount")));
        Assert.Equal(10_000m, (await api.BalanceAsync(cast.Custodian, cast.CustodyId)).Money("recorded"));
        Assert.Equal(1, await api.OfficeQueryAsync(db => db.LedgerEntries.CountAsync(entry => entry.Kind == OfficeLedgerKind.Funding)));

        // B: partial approval and a short receipt: 3,000 asked, 2,000 agreed, 1,900 received; finance hears of the gap.
        var partial = await api.FundAsync(cast, 3_000m, approved: 2_000m, received: 1_900m, note: "100 short in the envelope");
        Assert.Equal((3_000m, 2_000m, 2_000m, 1_900m), (partial.Money("requestedAmount"), partial.Money("approvedAmount"),
            partial.Money("disbursedAmount"), partial.Money("receivedAmount")));
        Assert.Equal(11_900m, (await api.BalanceAsync(cast.Custodian, cast.CustodyId)).Money("recorded"));
        Assert.NotEmpty(await api.NotificationsAsync(cast.Accountant, "OfficeCashDiscrepancy"));

        // C: a routine expense: 3 × 150. Pending first (available drops, recorded doesn't), then posted once.
        var receipt = await api.PhotoAsync(cast.Custodian);
        var key = Guid.NewGuid();
        var body = ExpenseBody(cast.CustodyId, 3m, 150m, [receipt], vendor: "Al Nour Library", invoiceNo: "A-77", key: key);
        var expense = await api.CreatedAsync(cast.Custodian, "/api/office/expenses", body);
        Assert.Equal((450m, 1, "Submitted"), (expense.Money("amount"), expense.GetProperty("requiredApprovals").GetInt32(), expense.Status()));
        balance = await api.BalanceAsync(cast.Custodian, cast.CustodyId);
        Assert.Equal((11_900m, 450m, 11_450m), (balance.Money("recorded"), balance.Money("pendingExpenses"), balance.Money("available")));

        // The offline queue sends it again: the same expense, not a second one.
        var again = await api.SendAsync(HttpMethod.Post, "/api/office/expenses", cast.Custodian, body);
        Assert.Equal((HttpStatusCode.OK, expense.Id()), (again.StatusCode, (await Json(again)).Id()));
        Assert.NotEmpty(await api.NotificationsAsync(cast.Accountant, "OfficeExpenseSubmitted"));
        Assert.NotEmpty(await api.NotificationsAsync(cast.Executive, "OfficeExpenseSubmitted"));

        var approved = await api.DecidedExpenseAsync(cast.Accountant, expense.Id(), "approve");
        Assert.Equal(("Approved", "Posted"), (approved.Status(), approved.GetProperty("postingStatus").GetString()));
        balance = await api.BalanceAsync(cast.Custodian, cast.CustodyId);
        Assert.Equal((11_450m, 0m), (balance.Money("recorded"), balance.Money("pendingExpenses")));
        var ledger = await api.GetAsync(cast.Custodian, $"/api/office/custodies/{cast.CustodyId}/ledger");
        var newest = ledger.GetProperty("items")[0];
        Assert.Equal(("Expense", -450m, 11_450m), (newest.GetProperty("kind").GetString(), newest.Money("amount"), newest.Money("runningBalance")));
        Assert.NotEmpty(await api.NotificationsAsync(cast.Custodian, "OfficeExpensePosted"));

        // P: another custodian sees nothing of this custody; an engineer has no office access; nobody reviews their own record.
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, $"/api/office/expenses/{expense.Id()}", cast.OtherCustodian)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, $"/api/office/custodies/{cast.CustodyId}", cast.OtherCustodian)).StatusCode);
        Assert.Empty((await api.GetAsync(cast.OtherCustodian, "/api/office/expenses")).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Get, "/api/office/home", cast.Engineer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.DecideExpenseAsync(cast.Custodian, expense.Id(), "approve")).StatusCode);
        var ownFunding = await api.CreatedAsync(cast.Accountant, "/api/office/funding-requests",
            new { custodyId = cast.CustodyId, amount = 500m, reason = "Top-up", idempotencyKey = Guid.NewGuid() });
        var selfApproval = await api.SendAsync(HttpMethod.Post, $"/api/office/funding-requests/{ownFunding.Id()}/approve", cast.Accountant,
            new { method = "Cash", rowVersion = ownFunding.RowVersion() });
        Assert.Equal("own_record", await CodeOf(selfApproval));

        // A custody can only be held by an active office custodian.
        var badHolder = await api.SendAsync(HttpMethod.Post, "/api/office/custodies", cast.Accountant,
            new { nameAr = "عهدة الموقع", nameEn = "Site cash", holderUserId = cast.Engineer.UserId });
        Assert.Equal("holder_invalid", await CodeOf(badHolder));
    }

    /// <summary>D, E and O: receipts are required and must be photos; high-risk expenses need two approvers, one the Executive; double taps count once.</summary>
    [DatabaseFact]
    public async Task Receipts_are_required_and_risky_expenses_need_the_executive_as_second_approver()
    {
        await using var api = await StartAsync(database);
        var cast = await api.CastAsync();
        await api.FundAsync(cast, 20_000m);

        // D: no receipt, a PDF as a receipt, someone else's photo.
        var noReceipt = await api.SendAsync(HttpMethod.Post, "/api/office/expenses", cast.Custodian, ExpenseBody(cast.CustodyId, 1m, 100m, []));
        Assert.Equal("receipt_required", await CodeOf(noReceipt));
        var pdf = await api.UploadAsync(cast.Custodian, "Receipt", Pdf());
        Assert.Equal((HttpStatusCode.UnsupportedMediaType, "file_type_not_allowed"), (pdf.StatusCode, await CodeOf(pdf)));
        var program = await api.UploadAsync(cast.Custodian, "Receipt", [0x4D, 0x5A, 0x90, 0x00, 0x03]);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, program.StatusCode);
        var foreign = await api.PhotoAsync(cast.OtherCustodian);
        var foreignUse = await api.SendAsync(HttpMethod.Post, "/api/office/expenses", cast.Custodian, ExpenseBody(cast.CustodyId, 1m, 100m, [foreign]));
        Assert.Equal("attachment_invalid", await CodeOf(foreignUse));

        // The same photo upload retried stores one file.
        var photoKey = Guid.NewGuid();
        var bytes = Jpeg();
        var first = await api.UploadAsync(cast.Custodian, "Receipt", bytes, photoKey);
        var second = await api.UploadAsync(cast.Custodian, "Receipt", bytes, photoKey);
        Assert.Equal((HttpStatusCode.Created, HttpStatusCode.OK), (first.StatusCode, second.StatusCode));
        Assert.Equal((await Json(first)).Id(), (await Json(second)).Id());

        // The custodian downloads their photo; another custodian can't.
        var photoId = (await Json(first)).Id();
        var content = await api.SendAsync(HttpMethod.Get, $"/api/office/attachments/{photoId}/content", cast.Custodian);
        Assert.Equal((HttpStatusCode.OK, "image/jpeg"), (content.StatusCode, content.Content.Headers.ContentType?.MediaType));
        Assert.Equal(bytes, await content.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await api.SendAsync(HttpMethod.Get, $"/api/office/attachments/{photoId}/content", cast.OtherCustodian)).StatusCode);

        // E: 6,000 is above the routine limit: two approvals, the second the Executive's.
        var big = await api.CreatedAsync(cast.Custodian, "/api/office/expenses", ExpenseBody(cast.CustodyId, 2m, 3_000m, [photoId], category: "Equipment"));
        Assert.Equal(2, big.GetProperty("requiredApprovals").GetInt32());
        Assert.Contains("AboveLimit", big.Strings("reviewReasons"));

        var partly = await api.DecidedExpenseAsync(cast.Accountant, big.Id(), "approve");
        Assert.Equal(("PartlyApproved", "NotPosted"), (partly.Status(), partly.GetProperty("postingStatus").GetString()));
        Assert.Equal(20_000m, (await api.BalanceAsync(cast.Custodian, cast.CustodyId)).Money("recorded"));

        // O: the same reviewer taps again: answered with the same expense, still one approval.
        var tapAgain = await api.DecidedExpenseAsync(cast.Accountant, big.Id(), "approve");
        Assert.Equal(("PartlyApproved", 1), (tapAgain.Status(), tapAgain.GetProperty("approvals").GetArrayLength()));

        // Two accountants are not enough.
        Assert.Equal("executive_required", await CodeOf(await api.DecideExpenseAsync(cast.SecondAccountant, big.Id(), "approve")));
        var done = await api.DecidedExpenseAsync(cast.Executive, big.Id(), "approve");
        Assert.Equal(("Approved", "Posted"), (done.Status(), done.GetProperty("postingStatus").GetString()));
        Assert.Equal(14_000m, (await api.BalanceAsync(cast.Custodian, cast.CustodyId)).Money("recorded"));

        // O: two reviewers approve the same routine expense at the same moment: it is posted once.
        var routine = await api.ExpenseAsync(cast, 1m, 200m);
        var rowVersion = routine.RowVersion();
        var responses = await Task.WhenAll(
            api.SendAsync(HttpMethod.Post, $"/api/office/expenses/{routine.Id()}/approve", cast.Accountant, new { rowVersion }),
            api.SendAsync(HttpMethod.Post, $"/api/office/expenses/{routine.Id()}/approve", cast.Executive, new { rowVersion }));
        Assert.Contains(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Equal(1, await api.OfficeQueryAsync(db => db.LedgerEntries.CountAsync(entry => entry.SourceType == "OfficeExpense" && entry.SourceId == routine.Id())));
        Assert.Equal(13_800m, (await api.BalanceAsync(cast.Custodian, cast.CustodyId)).Money("recorded"));
    }

    /// <summary>F, G and H: office prices are checked against the central Price Control; the Executive decides; history is never rewritten.</summary>
    [DatabaseFact]
    public async Task Prices_above_the_range_wait_for_the_executive_and_history_keeps_the_old_reference()
    {
        await using var api = await StartAsync(database);
        var cast = await api.CastAsync();
        await api.FundAsync(cast, 10_000m);

        var item = await api.CreatedAsync(cast.Executive, "/api/price-items", new
        {
            nameAr = "اشتراك إنترنت المكتب", nameEn = "Office internet subscription", classification = "Internet", unit = "Subscription",
            minPrice = 400m, maxPrice = 500m, effectiveFrom = DateTime.UtcNow.AddDays(-1).ToString("yyyy-MM-dd"),
        });
        var itemId = item.GetProperty("item").Id();
        var listed = await api.GetAsync(cast.Custodian, "/api/office/price-items");
        var row = Assert.Single(listed.GetProperty("items").EnumerateArray(), candidate => candidate.Id() == itemId);
        Assert.Equal((400m, 500m), (row.Money("minPrice"), row.Money("maxPrice")));
        var check = await api.OkAsync(cast.Custodian, HttpMethod.Post, "/api/office/price-check", new { priceItemId = itemId, unit = "Subscription", unitPrice = 650m });
        Assert.Equal("AboveRange", check.GetProperty("result").GetString());

        // F: above the range needs a justification, two approvals and the Executive's price decision before posting.
        var receipt = await api.PhotoAsync(cast.Custodian);
        var noReason = await api.SendAsync(HttpMethod.Post, "/api/office/expenses", cast.Custodian,
            ExpenseBody(cast.CustodyId, 1m, 650m, [receipt], category: "Internet", unit: "Subscription", priceItemId: itemId));
        Assert.Equal(["priceJustification"], await AlMotaheda.Api.Tests.ApiCalls.ErrorFields(noReason));

        var dear = await api.CreatedAsync(cast.Custodian, "/api/office/expenses",
            ExpenseBody(cast.CustodyId, 1m, 650m, [receipt], category: "Internet", unit: "Subscription", priceItemId: itemId, priceJustification: "Faster line after outage"));
        var price = dear.GetProperty("price");
        Assert.Equal(("AboveRange", 500m, 150m, "Pending"), (price.GetProperty("result").GetString(), price.Money("maxPrice"), price.Money("variancePerUnit"),
            price.GetProperty("exceptionStatus").GetString()));
        Assert.Equal(2, dear.GetProperty("requiredApprovals").GetInt32());
        var caseId = price.GetProperty("exceptionId").GetInt32();

        await api.DecidedExpenseAsync(cast.Accountant, dear.Id(), "approve");
        var approved = await api.DecidedExpenseAsync(cast.Executive, dear.Id(), "approve");
        Assert.Equal(("Approved", "NotPosted", true), (approved.Status(), approved.GetProperty("postingStatus").GetString(), approved.GetProperty("waitingForPrice").GetBoolean()));
        Assert.Equal(10_000m, (await api.BalanceAsync(cast.Custodian, cast.CustodyId)).Money("recorded"));

        // The Executive approves this price once: posted now; the catalog is unchanged.
        await api.DecidePriceAsync(cast.Executive, caseId, "approve", "Accepted this month only");
        var posted = await api.GetAsync(cast.Custodian, $"/api/office/expenses/{dear.Id()}");
        Assert.Equal(("Posted", "ApprovedOnce"), (posted.GetProperty("postingStatus").GetString(), posted.GetProperty("price").GetProperty("exceptionStatus").GetString()));
        Assert.Equal(9_350m, (await api.BalanceAsync(cast.Custodian, cast.CustodyId)).Money("recorded"));
        Assert.Equal(500m, (await api.GetAsync(cast.Executive, $"/api/price-items/{itemId}")).GetProperty("item").GetProperty("currentRange").Money("maxPrice"));

        // G: the Executive raises the central price; the expense keeps the comparison it was made with.
        var second = await api.CreatedAsync(cast.Custodian, "/api/office/expenses",
            ExpenseBody(cast.CustodyId, 1m, 600m, [await api.PhotoAsync(cast.Custodian)], category: "Internet", unit: "Subscription", priceItemId: itemId,
                priceJustification: "New tariff"));
        await api.DecidePriceAsync(cast.Executive, second.GetProperty("price").GetProperty("exceptionId").GetInt32(), "approve-with-price", "Tariff changed",
            new() { ["minPrice"] = 550m, ["maxPrice"] = 650m, ["referencePrice"] = 600m, ["effectiveFrom"] = Today });
        Assert.Equal(650m, (await api.GetAsync(cast.Executive, $"/api/price-items/{itemId}")).GetProperty("item").GetProperty("currentRange").Money("maxPrice"));
        var kept = await api.GetAsync(cast.Custodian, $"/api/office/expenses/{second.Id()}");
        Assert.Equal((500m, "ApprovedWithPriceChange"), (kept.GetProperty("price").Money("maxPrice"), kept.GetProperty("price").GetProperty("exceptionStatus").GetString()));
        var firstKept = await api.GetAsync(cast.Custodian, $"/api/office/expenses/{dear.Id()}");
        Assert.Equal(500m, firstKept.GetProperty("price").Money("maxPrice"));

        // H: a rejected price never posts; the accountant then rejects the expense.
        var tooDear = await api.CreatedAsync(cast.Custodian, "/api/office/expenses",
            ExpenseBody(cast.CustodyId, 1m, 900m, [await api.PhotoAsync(cast.Custodian)], category: "Internet", unit: "Subscription", priceItemId: itemId,
                priceJustification: "Premium plan"));
        await api.DecidePriceAsync(cast.Executive, tooDear.GetProperty("price").GetProperty("exceptionId").GetInt32(), "reject", "Not needed");
        await api.DecidedExpenseAsync(cast.Accountant, tooDear.Id(), "approve");
        var stuck = await api.DecidedExpenseAsync(cast.Executive, tooDear.Id(), "approve");
        Assert.Equal(("Approved", "NotPosted"), (stuck.Status(), stuck.GetProperty("postingStatus").GetString()));
        var rejected = await api.DecidedExpenseAsync(cast.Accountant, tooDear.Id(), "reject", "Price refused by the Executive");
        Assert.Equal(("Rejected", "NotPosted"), (rejected.Status(), rejected.GetProperty("postingStatus").GetString()));

        // A unit that can't be compared needs a justification and a second approval.
        var monthly = await api.CreatedAsync(cast.Custodian, "/api/office/expenses",
            ExpenseBody(cast.CustodyId, 1m, 450m, [await api.PhotoAsync(cast.Custodian)], category: "Internet", unit: "Month", priceItemId: itemId,
                priceJustification: "Billed per month"));
        Assert.Equal(("UnitMismatch", 2), (monthly.GetProperty("price").GetProperty("result").GetString(), monthly.GetProperty("requiredApprovals").GetInt32()));
    }

    /// <summary>I and J: look-alike expenses are flagged, not refused; a returned expense is corrected and reviewed afresh.</summary>
    [DatabaseFact]
    public async Task Duplicates_are_flagged_and_a_returned_expense_starts_a_new_review_round()
    {
        await using var api = await StartAsync(database);
        var cast = await api.CastAsync();
        await api.FundAsync(cast, 5_000m);

        var bytes = Jpeg();
        var original = await api.CreatedAsync(cast.Custodian, "/api/office/expenses",
            ExpenseBody(cast.CustodyId, 1m, 300m, [await api.PhotoAsync(cast.Custodian, bytes: bytes)], category: "Cleaning", vendor: "Clean Co", invoiceNo: "INV-9"));
        Assert.Equal(1, original.GetProperty("requiredApprovals").GetInt32());

        // I: the same invoice number of the same vendor.
        var sameInvoice = await api.CreatedAsync(cast.Custodian, "/api/office/expenses",
            ExpenseBody(cast.CustodyId, 2m, 50m, [await api.PhotoAsync(cast.Custodian)], category: "Cleaning", vendor: "Clean Co", invoiceNo: "INV-9"));
        Assert.Contains("DuplicateRisk", sameInvoice.Strings("reviewReasons"));
        Assert.Contains(original.Id(), sameInvoice.GetProperty("duplicateOf").EnumerateArray().Select(id => id.GetInt32()));
        Assert.Equal(2, sameInvoice.GetProperty("requiredApprovals").GetInt32());

        // The same receipt photo again (another upload of the same bytes).
        var samePhoto = await api.CreatedAsync(cast.Custodian, "/api/office/expenses",
            ExpenseBody(cast.CustodyId, 1m, 120m, [await api.PhotoAsync(cast.Custodian, bytes: bytes)], category: "Refreshments"));
        Assert.Contains(original.Id(), samePhoto.GetProperty("duplicateOf").EnumerateArray().Select(id => id.GetInt32()));

        // J: the Accountant approves (1 of 2), the Executive returns it; the correction is a new round.
        await api.DecidedExpenseAsync(cast.Accountant, sameInvoice.Id(), "approve");
        var returned = await api.DecidedExpenseAsync(cast.Executive, sameInvoice.Id(), "return", "This is the same invoice as INV-9. Attach the right one.");
        Assert.Equal("Returned", returned.Status());
        Assert.NotEmpty(await api.NotificationsAsync(cast.Custodian, "RecordReturned"));

        var corrected = ExpenseBody(cast.CustodyId, 2m, 50m, [await api.PhotoAsync(cast.Custodian)], category: "Cleaning", vendor: "Clean Co", invoiceNo: "INV-10");
        corrected["rowVersion"] = returned.RowVersion();
        var byOther = await api.SendAsync(HttpMethod.Put, $"/api/office/expenses/{sameInvoice.Id()}", cast.OtherCustodian, corrected);
        Assert.Equal(HttpStatusCode.NotFound, byOther.StatusCode);
        var resubmitted = await api.OkAsync(cast.Custodian, HttpMethod.Put, $"/api/office/expenses/{sameInvoice.Id()}", corrected);
        Assert.Equal(("Submitted", 2, 1), (resubmitted.Status(), resubmitted.GetProperty("round").GetInt32(), resubmitted.GetProperty("requiredApprovals").GetInt32()));
        Assert.DoesNotContain("DuplicateRisk", resubmitted.Strings("reviewReasons"));

        // The first round's approval doesn't count: one approval of this round posts it.
        var posted = await api.DecidedExpenseAsync(cast.Accountant, sameInvoice.Id(), "approve");
        Assert.Equal(("Approved", "Posted"), (posted.Status(), posted.GetProperty("postingStatus").GetString()));
        Assert.Equal(2, posted.GetProperty("approvals").GetArrayLength());
    }

    /// <summary>K and L: repairs with before and after photos, one cost, a higher final cost, an emergency done first, a waived photo.</summary>
    [DatabaseFact]
    public async Task A_repair_is_one_cost_with_its_evidence_and_an_emergency_can_be_recorded_first()
    {
        await using var api = await StartAsync(database);
        var cast = await api.CastAsync();
        await api.FundAsync(cast, 8_000m);

        // K: a repair needs a photo of the damage.
        var noPhoto = await api.SendAsync(HttpMethod.Post, "/api/office/spend-requests", cast.Custodian, new
        {
            custodyId = cast.CustodyId, kind = "Repair", title = "AC leaking", description = "The meeting room AC leaks water", estimatedAmount = 1_000m,
            idempotencyKey = Guid.NewGuid(),
        });
        Assert.Equal("evidence_required", await CodeOf(noPhoto));

        var quotation = await api.UploadAsync(cast.Custodian, "Quotation", Pdf());
        await EnsureStatus(quotation, HttpStatusCode.Created);
        var repair = await api.CreatedAsync(cast.Custodian, "/api/office/spend-requests", new
        {
            custodyId = cast.CustodyId, kind = "Repair", title = "AC leaking", description = "The meeting room AC leaks water", asset = "AC unit 2",
            location = "Meeting room", supplier = "Cool Air", estimatedAmount = 1_000m,
            attachmentIds = new[] { await api.PhotoAsync(cast.Custodian, "BeforeRepair"), (await Json(quotation)).Id() },
            idempotencyKey = Guid.NewGuid(),
        });
        Assert.NotEmpty(await api.NotificationsAsync(cast.Accountant, "OfficeSpendRequested"));

        // Not approved yet: its cost can't be recorded. Approving it reserves the estimate, but moves no cash.
        var early = await api.SendAsync(HttpMethod.Post, "/api/office/expenses", cast.Custodian,
            ExpenseBody(cast.CustodyId, 1m, 1_200m, [await api.PhotoAsync(cast.Custodian), await api.PhotoAsync(cast.Custodian, "AfterRepair")], category: "Maintenance",
                unit: "Job", spendRequestId: repair.Id()));
        Assert.Equal("spend_request_invalid", await CodeOf(early));
        await api.OkAsync(cast.Accountant, HttpMethod.Post, $"/api/office/spend-requests/{repair.Id()}/approve", new { rowVersion = repair.RowVersion() });
        var balance = await api.BalanceAsync(cast.Custodian, cast.CustodyId);
        Assert.Equal((8_000m, 1_000m, 7_000m), (balance.Money("recorded"), balance.Money("commitments"), balance.Money("available")));

        // The final cost needs a photo of the finished repair.
        var noAfter = await api.SendAsync(HttpMethod.Post, "/api/office/expenses", cast.Custodian,
            ExpenseBody(cast.CustodyId, 1m, 1_200m, [await api.PhotoAsync(cast.Custodian)], category: "Maintenance", unit: "Job", spendRequestId: repair.Id()));
        Assert.Equal("evidence_required", await CodeOf(noAfter));

        var cost = await api.CreatedAsync(cast.Custodian, "/api/office/expenses",
            ExpenseBody(cast.CustodyId, 1m, 1_200m, [await api.PhotoAsync(cast.Custodian), await api.PhotoAsync(cast.Custodian, "AfterRepair")], category: "Maintenance",
                unit: "Job", spendRequestId: repair.Id()));
        Assert.Equal(["AboveEstimate", "Repair"], cost.Strings("reviewReasons").Where(reason => reason is "AboveEstimate" or "Repair").Order());
        Assert.Equal(2, cost.GetProperty("requiredApprovals").GetInt32());
        var completed = await api.GetAsync(cast.Custodian, $"/api/office/spend-requests/{repair.Id()}");
        Assert.Equal(("Completed", cost.Id()), (completed.Status(), completed.GetProperty("expenseId").GetInt32()));

        // One cost: a second expense for the same repair is refused.
        var twice = await api.SendAsync(HttpMethod.Post, "/api/office/expenses", cast.Custodian,
            ExpenseBody(cast.CustodyId, 1m, 100m, [await api.PhotoAsync(cast.Custodian), await api.PhotoAsync(cast.Custodian, "AfterRepair")], category: "Maintenance",
                unit: "Job", spendRequestId: repair.Id()));
        Assert.Equal("spend_request_invalid", await CodeOf(twice));

        await api.DecidedExpenseAsync(cast.Accountant, cost.Id(), "approve");
        await api.DecidedExpenseAsync(cast.Executive, cost.Id(), "approve");
        balance = await api.BalanceAsync(cast.Custodian, cast.CustodyId);
        Assert.Equal((6_800m, 0m), (balance.Money("recorded"), balance.Money("commitments")));

        // L: an emergency repair already done is recorded at once, with its reason, and gets the elevated review.
        var emergency = await api.CreatedAsync(cast.Custodian, "/api/office/spend-requests", new
        {
            custodyId = cast.CustodyId, kind = "Repair", title = "Main door lock", description = "The lock broke at night", isEmergency = true,
            emergencyReason = "The office could not be locked overnight", attachmentIds = new[] { await api.PhotoAsync(cast.Custodian, "BeforeRepair") },
            idempotencyKey = Guid.NewGuid(),
        });
        var emergencyCost = await api.CreatedAsync(cast.Custodian, "/api/office/expenses",
            ExpenseBody(cast.CustodyId, 1m, 350m, [await api.PhotoAsync(cast.Custodian), await api.PhotoAsync(cast.Custodian, "AfterRepair")], category: "Maintenance",
                unit: "Job", spendRequestId: emergency.Id()));
        Assert.Contains("Emergency", emergencyCost.Strings("reviewReasons"));
        Assert.Equal(2, emergencyCost.GetProperty("requiredApprovals").GetInt32());

        // A reviewer may waive the after-repair photo, with a reason.
        var noPhotoPossible = await api.CreatedAsync(cast.Custodian, "/api/office/spend-requests", new
        {
            custodyId = cast.CustodyId, kind = "Repair", title = "Ceiling light", description = "Replace the ballast", estimatedAmount = 200m,
            attachmentIds = new[] { await api.PhotoAsync(cast.Custodian, "BeforeRepair") }, idempotencyKey = Guid.NewGuid(),
        });
        var approvedLight = await api.OkAsync(cast.Accountant, HttpMethod.Post, $"/api/office/spend-requests/{noPhotoPossible.Id()}/approve",
            new { rowVersion = noPhotoPossible.RowVersion() });
        await api.OkAsync(cast.Accountant, HttpMethod.Post, $"/api/office/spend-requests/{noPhotoPossible.Id()}/waive-after-evidence",
            new { note = "Fitted inside the ceiling, nothing to see", rowVersion = approvedLight.RowVersion() });
        await api.CreatedAsync(cast.Custodian, "/api/office/expenses",
            ExpenseBody(cast.CustodyId, 1m, 180m, [await api.PhotoAsync(cast.Custodian)], category: "Maintenance", unit: "Job", spendRequestId: noPhotoPossible.Id()));
    }

    /// <summary>M and N: counts never move cash by themselves; low balance; cash waits for funding; reversal, return and closing.</summary>
    [DatabaseFact]
    public async Task Counts_adjustments_reversals_returns_and_closing_keep_the_ledger_true()
    {
        await using var api = await StartAsync(database);
        var cast = await api.CastAsync(lowBalanceThreshold: 500m);
        await api.FundAsync(cast, 1_000m);

        // M: a matching count needs nothing more.
        var matched = await api.CreatedAsync(cast.Custodian, "/api/office/cash-counts", new { custodyId = cast.CustodyId, countedAmount = 1_000m, idempotencyKey = Guid.NewGuid() });
        Assert.Equal(("Matched", 0m), (matched.Status(), matched.Money("difference")));

        // A difference needs an explanation, alerts finance and changes nothing until reviewed.
        var unexplained = await api.SendAsync(HttpMethod.Post, "/api/office/cash-counts", cast.Custodian, new { custodyId = cast.CustodyId, countedAmount = 950m, idempotencyKey = Guid.NewGuid() });
        Assert.Equal(["explanation"], await AlMotaheda.Api.Tests.ApiCalls.ErrorFields(unexplained));
        var shortCount = await api.CreatedAsync(cast.Custodian, "/api/office/cash-counts",
            new { custodyId = cast.CustodyId, countedAmount = 950m, explanation = "Paid a courier, receipt lost", idempotencyKey = Guid.NewGuid() });
        Assert.Equal(("Submitted", -50m, 1_000m), (shortCount.Status(), shortCount.Money("difference"), shortCount.Money("recordedBalance")));
        Assert.Equal(1_000m, (await api.BalanceAsync(cast.Custodian, cast.CustodyId)).Money("recorded"));
        Assert.NotEmpty(await api.NotificationsAsync(cast.Accountant, "OfficeCashDiscrepancy"));
        Assert.Equal(HttpStatusCode.Forbidden, (await api.SendAsync(HttpMethod.Post, $"/api/office/cash-counts/{shortCount.Id()}/accept", cast.Custodian,
            new { note = "ok", rowVersion = shortCount.RowVersion() })).StatusCode);

        var accepted = await api.OkAsync(cast.Accountant, HttpMethod.Post, $"/api/office/cash-counts/{shortCount.Id()}/accept",
            new { note = "Courier confirmed by phone", rowVersion = shortCount.RowVersion() });
        Assert.Equal("Accepted", accepted.Status());
        Assert.Equal(1_000m, (await api.BalanceAsync(cast.Custodian, cast.CustodyId)).Money("recorded"));

        var adjustCount = await api.CreatedAsync(cast.Custodian, "/api/office/cash-counts",
            new { custodyId = cast.CustodyId, countedAmount = 980m, explanation = "Unknown", idempotencyKey = Guid.NewGuid() });
        var noReason = await api.SendAsync(HttpMethod.Post, $"/api/office/cash-counts/{adjustCount.Id()}/adjust", cast.Accountant, new { rowVersion = adjustCount.RowVersion() });
        Assert.Equal(["note"], await AlMotaheda.Api.Tests.ApiCalls.ErrorFields(noReason));
        await api.OkAsync(cast.Accountant, HttpMethod.Post, $"/api/office/cash-counts/{adjustCount.Id()}/adjust",
            new { note = "Shortage written off after review", rowVersion = adjustCount.RowVersion() });
        Assert.Equal(980m, (await api.BalanceAsync(cast.Custodian, cast.CustodyId)).Money("recorded"));

        // A posted expense takes the balance below the threshold: one low-balance warning.
        var expense = await api.ExpenseAsync(cast, 1m, 700m);
        await api.DecidedExpenseAsync(cast.Accountant, expense.Id(), "approve");
        var balance = await api.BalanceAsync(cast.Custodian, cast.CustodyId);
        Assert.Equal((280m, true), (balance.Money("recorded"), balance.GetProperty("isLow").GetBoolean()));
        Assert.NotEmpty(await api.NotificationsAsync(cast.Custodian, "OfficeLowBalance"));
        Assert.NotEmpty(await api.NotificationsAsync(cast.Executive, "OfficeLowBalance"));

        // More than the cash: two approvals, then it waits for funds and posts by itself when they are received.
        var tooMuch = await api.ExpenseAsync(cast, 1m, 400m);
        Assert.Contains("AboveAvailable", tooMuch.Strings("reviewReasons"));
        await api.DecidedExpenseAsync(cast.Accountant, tooMuch.Id(), "approve");
        var waiting = await api.DecidedExpenseAsync(cast.Executive, tooMuch.Id(), "approve");
        Assert.Equal(("Approved", "NotPosted", true), (waiting.Status(), waiting.GetProperty("postingStatus").GetString(), waiting.GetProperty("waitingForFunds").GetBoolean()));
        await api.FundAsync(cast, 500m);
        Assert.Equal("Posted", (await api.GetAsync(cast.Custodian, $"/api/office/expenses/{tooMuch.Id()}")).GetProperty("postingStatus").GetString());
        Assert.Equal(380m, (await api.BalanceAsync(cast.Custodian, cast.CustodyId)).Money("recorded"));

        // N: a posted expense is reversed by a linked entry, never deleted; a second reversal changes nothing.
        var reversed = await api.DecidedExpenseAsync(cast.Accountant, expense.Id(), "reverse", "Recorded on the wrong custody");
        Assert.Equal("Reversed", reversed.GetProperty("postingStatus").GetString());
        Assert.Equal("Reversed", (await api.DecidedExpenseAsync(cast.Accountant, expense.Id(), "reverse", "Again")).GetProperty("postingStatus").GetString());
        Assert.Equal(1_080m, (await api.BalanceAsync(cast.Custodian, cast.CustodyId)).Money("recorded"));
        var entries = await api.OfficeQueryAsync(db => db.LedgerEntries.Where(entry => entry.SourceId == expense.Id() && entry.SourceType.StartsWith("OfficeExpense")).OrderBy(entry => entry.Id).ToListAsync());
        Assert.Equal([OfficeLedgerKind.Expense, OfficeLedgerKind.Reversal], entries.Select(entry => entry.Kind));
        Assert.Equal(entries[0].Id, entries[1].ReversesEntryId);

        // Suspended: nothing new; reopened: back to normal.
        var custody = await api.GetAsync(cast.Accountant, $"/api/office/custodies/{cast.CustodyId}");
        custody = await api.OkAsync(cast.Accountant, HttpMethod.Post, $"/api/office/custodies/{cast.CustodyId}/suspend", new { reason = "Audit", rowVersion = custody.RowVersion() });
        var whileSuspended = await api.SendAsync(HttpMethod.Post, "/api/office/expenses", cast.Custodian,
            ExpenseBody(cast.CustodyId, 1m, 10m, [await api.PhotoAsync(cast.Custodian)]));
        Assert.Equal("custody_inactive", await CodeOf(whileSuspended));
        custody = await api.OkAsync(cast.Accountant, HttpMethod.Post, $"/api/office/custodies/{cast.CustodyId}/reopen", new { reason = "Audit done", rowVersion = custody.RowVersion() });

        // Closing needs a zero balance: the cash is handed back first (once, whatever the retries).
        var refused = await api.SendAsync(HttpMethod.Post, $"/api/office/custodies/{cast.CustodyId}/close", cast.Accountant, new { reason = "Office moved", rowVersion = custody.RowVersion() });
        Assert.Equal("custody_not_empty", await CodeOf(refused));
        var tooMuchBack = await api.SendAsync(HttpMethod.Post, $"/api/office/custodies/{cast.CustodyId}/returns", cast.Accountant,
            new { amount = 5_000m, reason = "Closing", idempotencyKey = Guid.NewGuid() });
        Assert.Equal("insufficient_balance", await CodeOf(tooMuchBack));
        var returnBody = new { amount = 1_080m, reason = "Closing the office custody", reference = "RCPT-1", idempotencyKey = Guid.NewGuid() };
        await api.CreatedAsync(cast.Accountant, $"/api/office/custodies/{cast.CustodyId}/returns", returnBody);
        await api.OkAsync(cast.Accountant, HttpMethod.Post, $"/api/office/custodies/{cast.CustodyId}/returns", returnBody);
        Assert.Equal(0m, (await api.BalanceAsync(cast.Custodian, cast.CustodyId)).Money("recorded"));

        custody = await api.GetAsync(cast.Accountant, $"/api/office/custodies/{cast.CustodyId}");
        var closed = await api.OkAsync(cast.Accountant, HttpMethod.Post, $"/api/office/custodies/{cast.CustodyId}/close", new { reason = "Office moved", rowVersion = custody.RowVersion() });
        Assert.Equal("Closed", closed.Status());

        // The summary agrees with the ledger.
        var summary = await api.GetAsync(cast.Accountant, $"/api/office/reports/summary?custodyId={cast.CustodyId}");
        Assert.Equal((1_500m, 1_100m, 700m, 1_080m, -20m, 0m), (summary.Money("fundingReceived"), summary.Money("expensesPosted"), summary.Money("reversals"),
            summary.Money("returns"), summary.Money("adjustments"), summary.Money("closingBalance")));
    }
}

internal static class PriceDecisions
{
    public static async Task DecidePriceAsync(this AccessApi api, Session executive, int exceptionId, string action, string note, Dictionary<string, object?>? extra = null)
    {
        var current = await api.GetAsync(executive, $"/api/price-exceptions/{exceptionId}");
        var body = new Dictionary<string, object?> { ["note"] = note, ["rowVersion"] = current.RowVersion() };
        foreach (var (key, value) in extra ?? [])
        {
            body[key] = value;
        }

        var response = await api.SendAsync(HttpMethod.Post, $"/api/price-exceptions/{exceptionId}/{action}", executive, body);
        await EnsureStatus(response, HttpStatusCode.OK);
    }
}
