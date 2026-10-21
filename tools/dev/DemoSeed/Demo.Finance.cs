using System.Text.Json.Nodes;

namespace AlMotaheda.DemoSeed;

internal sealed partial class Demo
{
    private int _accountantAccountId;
    private int _officeCustodyId;

    /// <summary>
    /// The money chain, done by the people the rules require:
    /// the Executive sets the treasury's opening balance once; the Accountant asks for cash from the treasury, an
    /// Executive approves (never the requester), the Accountant pays it out and confirms it in hand; then the same
    /// four steps from the Accountant to the engineers', the office's and the pickup's custody. Balances move only on
    /// disbursement and receipt, so the amounts below reconcile with the ledger by construction.
    /// </summary>
    private async Task FinanceAsync()
    {
        Log.Section("Treasury and custody");
        var ceo = As(Cast.Ceo);

        var treasury = await ceo.GetAsync("/api/treasury");
        var bank = treasury.Items("accounts").Single(account => account.Str("account") == "Bank");
        if (!(bool)bank["openingSet"]!)
        {
            await ceo.PutAsync("/api/treasury/Bank/opening-balance", new
            {
                openingBalance = 1_000_000m,
                openingDate = Iso(today.AddDays(-100)),
                notes = $"{Marker} opening balance of the demo company's bank account",
                rowVersion = bank.RowVersion(),
            });
            Log.New("treasury Bank opening balance EGP 1,000,000");
        }
        else
        {
            Log.Kept($"treasury Bank opening balance {(decimal)bank["openingBalance"]!:N0}");
        }

        // The Accountant's own custody account (custody Approve opens it).
        var accountantAccounts = (await ceo.GetAsync($"/api/custody-accounts?kind=Accountant&holderUserId={UserId(Cast.Accountant)}&status=Active")).Items().ToList();
        if (accountantAccounts.Count == 0)
        {
            var opened = await ceo.PostAsync("/api/custody-accounts", new
            {
                kind = "Accountant",
                holderUserId = UserId(Cast.Accountant),
                nameAr = "عهدة المحاسبة - منى سمير",
                nameEn = "Accounting custody - Mona Samir",
                notes = Marker,
            });
            _accountantAccountId = opened["account"]?["id"] is { } nested ? (int)nested : opened.Id();
            Log.New("custody account of the Accountant");
        }
        else
        {
            _accountantAccountId = accountantAccounts[0].Id();
            Log.Kept($"custody account of the Accountant {accountantAccounts[0].Str("number")}");
        }

        // The office petty cash (the Office module's custody), held by the Office Manager.
        var accountant = As(Cast.Accountant);
        var custodies = (await accountant.GetAsync("/api/office/custodies")).Items().ToList();
        var office = custodies.FirstOrDefault(custody => (int?)custody["holder"]?["userId"] == UserId(Cast.OfficeManager) && custody.Str("status") != "Closed");
        if (office is null)
        {
            office = await accountant.PostAsync("/api/office/custodies", new
            {
                nameAr = "عهدة المكتب الرئيسي",
                nameEn = "Head office petty cash",
                holderUserId = UserId(Cast.OfficeManager),
                lowBalanceThreshold = 2000m,
                notes = $"{Marker} office custody",
            });
            Log.New("office custody (Head office petty cash)");
        }
        else
        {
            Log.Kept("office custody (Head office petty cash)");
        }

        _officeCustodyId = office.Id();

        var bankSource = new { kind = "Treasury", treasuryAccount = "Bank" };
        var accountantSide = new { kind = "Custody", accountId = _accountantAccountId };
        object EngineerSide(Person engineer, ProjectSpec project) =>
            new { kind = "Custody", accountKind = "EngineerProject", holderUserId = UserId(engineer), referenceId = ProjectId(project) };

        // 1. Treasury -> Accountant 100,000 (received).
        await TransferAsync("treasury-to-accountant", accountant, bankSource, accountantSide, 100_000m,
            "سلفة عهدة للمحاسبة من البنك", "Cash advance to the accounting custody",
            approver: ceo, disburser: accountant, receiver: accountant, until: "Received");

        // 2. Accountant -> Omar's Nile Tower custody 50,000 (received): it pays two weeks of labor (Workforce step).
        await TransferAsync("accountant-to-omar", accountant, accountantSide, EngineerSide(Cast.Engineer, Sites.NileTower), 50_000m,
            "عهدة موقع برج النيل - أجور العمالة", "Nile Tower site custody - labor wages",
            approver: ceo, disburser: accountant, receiver: As(Cast.Engineer), until: "Received");

        // 3. Accountant -> Youssef's Shorouk custody 20,000 (received): site expenses.
        await TransferAsync("accountant-to-youssef", accountant, accountantSide, EngineerSide(Cast.Engineer2, Sites.ShoroukSchools), 20_000m,
            "عهدة موقع الشروق - مصروفات الموقع", "Shorouk site custody - site expenses",
            approver: ceo, disburser: accountant, receiver: As(Cast.Engineer2), until: "Received");

        // 4. Accountant -> the office petty cash 10,000 (received by the Office Manager).
        await TransferAsync("accountant-to-office", accountant, accountantSide,
            new { kind = "Custody", accountKind = "Office", holderUserId = UserId(Cast.OfficeManager), referenceId = _officeCustodyId }, 10_000m,
            "تغذية عهدة المكتب الرئيسي", "Head office petty cash funding",
            approver: ceo, disburser: accountant, receiver: As(Cast.OfficeManager), until: "Received");

        // 5. Accountant -> the pickup's operating custody 3,000 (a vehicle has no holder: a custody reviewer confirms).
        await TransferAsync("accountant-to-pickup", accountant, accountantSide,
            new { kind = "Custody", accountKind = "Vehicle", referenceId = VehicleId(Pickup) }, 3_000m,
            "عهدة تشغيل البيك أب VH-001", "Pickup VH-001 operating custody",
            approver: ceo, disburser: accountant, receiver: As(Cast.Deputy), until: "Received");

        // 6. Omar asks Finance for 5,000 more for his Nile Tower custody: a payment request, nothing moves yet.
        await FundingRequestAsync("omar-asks-5000", Cast.Engineer, Sites.NileTower, 5_000m,
            "طلب تعزيز عهدة لشراء مستلزمات موقع", "Top-up for site consumables", decision: null);

        // 7. Youssef's 30,000 request, refused by the Executive with the reason kept.
        await FundingRequestAsync("youssef-asks-30000", Cast.Engineer2, Sites.ShoroukSchools, 30_000m,
            "طلب عهدة كبيرة بدون تفاصيل", "Large custody request without details", decision: "reject");

        // 8. A transfer the Accountant asked for and the Executive approved, not paid out yet: reserved, not moved.
        await TransferAsync("accountant-to-youssef-7500", accountant, accountantSide, EngineerSide(Cast.Engineer2, Sites.ShoroukSchools), 7_500m,
            "تعزيز عهدة موقع الشروق - أعمال طارئة", "Shorouk custody top-up - urgent works",
            approver: ceo, disburser: accountant, receiver: As(Cast.Engineer2), until: "Approved");
    }

    /// <summary>An engineer's custody funding request (a Finance payment request); optionally the Executive's decision.</summary>
    private async Task FundingRequestAsync(string key, Person engineer, ProjectSpec project, decimal amount, string notesAr, string notesEn, string? decision)
    {
        var reference = await As(engineer).PostAsync($"/api/projects/{ProjectId(project)}/custody/fundings", new
        {
            holderUserId = UserId(engineer),
            amount,
            method = "Cash",
            requestedDate = Iso(today.AddDays(-3)),
            reference = $"DEMO-{key}",
            notes = $"{Marker} {notesAr} / {notesEn}",
            idempotencyKey = Key($"funding:{key}"),
        });
        var details = await As(Cast.Ceo).GetAsync($"/api/payment-requests/{reference.Id()}");
        var request = details["request"]!;
        var status = request.Str("status");
        if (decision == "reject" && status is "Submitted" or "Pending" or "Requested")
        {
            request = await As(Cast.Ceo).PostAsync($"/api/payment-requests/{reference.Id()}/reject",
                new { note = "يرجى تقديم بيان تفصيلي بالأعمال المطلوبة أولًا / Send a breakdown of the works first.", rowVersion = details.RowVersion() });
            request = request["request"] ?? request;
            Log.New($"funding request {request.Str("number")} {amount:N0} ({notesEn}): {status} -> {request.Str("status")}");
            return;
        }

        Log.Kept($"funding request {request.Str("number")} {amount:N0} ({notesEn}): {status}");
    }

    private static readonly string[] TransferSteps = ["Requested", "Approved", "Disbursed", "Received"];

    /// <summary>
    /// Asks for a transfer with a fixed idempotency key (the same requester gets the same transfer back), then moves it
    /// one real step at a time up to <paramref name="until"/>; a later run only finishes the missing steps.
    /// </summary>
    private async Task<JsonNode> TransferAsync(string key, Session requester, object source, object destination, decimal amount,
        string notesAr, string notesEn, Session approver, Session disburser, Session receiver, string until)
    {
        var transfer = await requester.PostAsync("/api/custody-transfers", new
        {
            source,
            destination,
            amount,
            purpose = "CustodyAdvance",
            reference = $"DEMO-{key}",
            notes = $"{Marker} {notesAr} / {notesEn}",
            idempotencyKey = Key($"transfer:{key}"),
        });
        var label = $"transfer {transfer.Str("number")} {amount:N0} ({notesEn})";
        var start = transfer.Str("status");

        if (until == "Rejected")
        {
            if (start == "Requested")
            {
                transfer = await approver.PostAsync($"/api/custody-transfers/{transfer.Id()}/reject",
                    new { note = "يرجى تقديم بيان تفصيلي بالأعمال المطلوبة أولًا / Send a breakdown of the works first.", rowVersion = transfer.RowVersion() });
            }
        }
        else
        {
            var target = Array.IndexOf(TransferSteps, until);
            while (Array.IndexOf(TransferSteps, transfer.Str("status")) is var at and >= 0 && at < target)
            {
                var path = $"/api/custody-transfers/{transfer.Id()}";
                transfer = TransferSteps[at] switch
                {
                    "Requested" => await approver.PostAsync($"{path}/approve", new { note = $"{Marker} approved", rowVersion = transfer.RowVersion() }),
                    "Approved" => await disburser.PostAsync($"{path}/disburse", new { on = Iso(today.AddDays(-80 + _transferIndex)), reference = $"DEMO-{key}-CASH", note = Marker, rowVersion = transfer.RowVersion() }),
                    _ => await receiver.PostAsync($"{path}/receive", new { on = Iso(today.AddDays(-80 + _transferIndex)), note = Marker, rowVersion = transfer.RowVersion() }),
                };
            }
        }

        _transferIndex++;
        if (transfer.Str("status") == start)
        {
            Log.Kept($"{label}: {start}");
        }
        else
        {
            Log.New($"{label}: {start} -> {transfer.Str("status")}");
        }

        return transfer;
    }

    private int _transferIndex;
}
