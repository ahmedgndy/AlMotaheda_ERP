using System.Text.Json.Nodes;

namespace AlMotaheda.DemoSeed;

internal sealed record WorkerSpec(string Key, string NameAr, string NameEn, string NationalId, string Trade, decimal Rate, string Item);

internal sealed partial class Demo
{
    // The Nile Tower crew: 10,000 a day together, so 3 + 2 approved days are exactly the 50,000 the custody received.
    private static readonly WorkerSpec[] NileCrew =
    [
        new("foreman", "حسن محمود", "Hassan Mahmoud", "29900000100001", "construction", 900, "nt.slabs"),
        new("mason1", "رمضان عبد الله", "Ramadan Abdallah", "29900000100002", "mason", 600, "nt.masonry"),
        new("mason2", "جمال سعد", "Gamal Saad", "29900000100003", "mason", 600, "nt.masonry"),
        new("mason3", "فتحي بدوي", "Fathy Badawy", "29900000100014", "mason", 600, "nt.masonry"),
        new("carpenter", "صبري فوزي", "Sabry Fawzy", "29900000100004", "carpenter", 600, "nt.columns"),
        new("carpenter2", "مجدي حلمي", "Magdy Helmy", "29900000100015", "carpenter", 600, "nt.columns"),
        new("steel", "عادل منير", "Adel Mounir", "29900000100005", "steel", 650, "nt.columns"),
        new("steel2", "شريف وهبة", "Sherif Wahba", "29900000100016", "steel", 650, "nt.slabs"),
        new("plasterer", "عصام جودة", "Essam Gouda", "29900000100017", "plasterer", 550, "nt.finishing"),
        new("insulation", "ناصر عطية", "Nasser Attia", "29900000100018", "waterproofing", 600, "nt.waterproofing"),
        new("labor1", "محمد عيد", "Mohamed Eid", "29900000100006", "general", 365, "nt.slabs"),
        new("labor2", "أحمد شعبان", "Ahmed Shaaban", "29900000100007", "general", 365, "nt.slabs"),
        new("labor3", "كريم ناصر", "Karim Nasser", "29900000100008", "general", 365, "nt.foundations"),
        new("labor4", "سامح رجب", "Sameh Ragab", "29900000100009", "general", 365, "nt.foundations"),
        new("labor5", "وليد حمدي", "Walid Hamdy", "29900000100010", "general", 365, "nt.foundations"),
        new("labor6", "عماد سالم", "Emad Salem", "29900000100019", "general", 365, "nt.columns"),
        new("labor7", "طه زكي", "Taha Zaki", "29900000100020", "general", 365, "nt.columns"),
        new("labor8", "حمادة السيد", "Hamada El Sayed", "29900000100021", "general", 365, "nt.masonry"),
        new("labor9", "رضا عثمان", "Reda Osman", "29900000100022", "general", 365, "nt.masonry"),
        new("labor10", "سعيد مرسي", "Said Morsy", "29900000100023", "general", 365, "nt.site"),
    ];

    private static readonly WorkerSpec[] ShoroukCrew =
    [
        new("sh.mason", "إبراهيم طه", "Ibrahim Taha", "29900000100011", "mason", 500, "sh.masonry"),
        new("sh.labor1", "مصطفى عزت", "Mostafa Ezzat", "29900000100012", "general", 350, "sh.masonry"),
        new("sh.labor2", "هاني فاروق", "Hany Farouk", "29900000100013", "general", 350, "sh.concrete"),
    ];

    private readonly Dictionary<string, int> _workerIds = new();

    /// <summary>
    /// Workers on the projects, daily labor sheets recorded by the site engineer and approved by the project manager,
    /// and the two weekly settlements Omar paid in cash from his custody, confirmed by the Accountant. Plus sheets in
    /// every other state (waiting for review, returned, draft) for the review screens.
    /// </summary>
    private async Task WorkforceAsync()
    {
        Log.Section("Workforce: workers, daily sheets, weekly settlements");
        var trades = (await As(Cast.Ceo).GetAsync("/api/trades")).Items().ToDictionary(trade => trade.Str("code"), trade => trade.Id());

        await WorkersAsync(Sites.NileTower, Cast.Engineer, NileCrew, trades);
        await WorkersAsync(Sites.ShoroukSchools, Cast.Engineer2, ShoroukCrew, trades);

        // Labor sheets may only be dated today or the 7 days before, so the paid weeks are the last five days but
        // three: days 7-5 (settled 30,000) and days 4-3 (settled 20,000), approved by the project manager (workers
        // Approve, never the recorder).
        var week1 = Enumerable.Range(0, 3).Select(day => today.AddDays(-7 + day)).ToList();
        var week2 = Enumerable.Range(0, 2).Select(day => today.AddDays(-4 + day)).ToList();
        foreach (var date in week1.Concat(week2))
        {
            await SheetAsync(Sites.NileTower, Cast.Engineer, NileCrew, date, until: "Approved");
        }

        await SettlementAsync("nt-week1", week1[0], week1[^1], 30_000m);
        await SettlementAsync("nt-week2", week2[0], week2[^1], 20_000m);

        // The review states: waiting for the manager, sent back with a question, and today's draft not sent yet.
        await SheetAsync(Sites.NileTower, Cast.Engineer, NileCrew, today.AddDays(-2), until: "Submitted");
        await SheetAsync(Sites.NileTower, Cast.Engineer, NileCrew, today.AddDays(-1), until: "Returned");
        await SheetAsync(Sites.NileTower, Cast.Engineer, NileCrew[..6], today, until: "Draft");
        await SheetAsync(Sites.ShoroukSchools, Cast.Engineer2, ShoroukCrew, today.AddDays(-2), until: "Submitted");
    }

    private async Task WorkersAsync(ProjectSpec project, Person engineer, WorkerSpec[] crew, IReadOnlyDictionary<string, int> trades)
    {
        var assigned = (await As(engineer).GetAsync($"/api/projects/{ProjectId(project)}/workers?pageSize=100")).Items()
            .Select(row => (int?)row["workerId"] ?? (int?)row["worker"]?["id"] ?? 0).ToHashSet();
        foreach (var worker in crew)
        {
            var found = (await As(Cast.Ceo).GetAsync($"/api/workers?search={worker.NationalId}&pageSize=5")).Items()
                .FirstOrDefault(row => row.Str("nameEn") == worker.NameEn);
            if (found is null)
            {
                found = await As(Cast.Accountant).PostAsync("/api/workers", new
                {
                    nameAr = worker.NameAr,
                    nameEn = worker.NameEn,
                    nationalId = worker.NationalId,
                    phone = $"0111{worker.NationalId[^7..]}",
                    tradeId = trades[worker.Trade],
                    dailyRate = worker.Rate,
                    notes = $"{Marker} demo worker",
                });
                Log.New($"worker {worker.NameEn} ({worker.Trade}, {worker.Rate:N0}/day)");
            }
            else
            {
                Log.Kept($"worker {worker.NameEn}");
            }

            _workerIds[worker.Key] = found.Id();
            if (!assigned.Contains(found.Id()))
            {
                await As(Cast.Accountant).PostAsync($"/api/projects/{ProjectId(project)}/workers", new
                {
                    workerId = found.Id(),
                    dailyRate = worker.Rate,
                    startDate = Iso(today.AddDays(-45)),
                });
                Log.New($"  on {project.Code} at {worker.Rate:N0}/day");
            }
        }
    }

    /// <summary>One day sheet for a crew, taken to <paramref name="until"/> (Draft, Submitted, Returned or Approved).</summary>
    private async Task SheetAsync(ProjectSpec project, Person engineer, WorkerSpec[] crew, DateOnly date, string until)
    {
        var path = $"/api/projects/{ProjectId(project)}/labor/sheets";
        var sheet = (await As(engineer).GetAsync($"{path}?from={Iso(date)}&to={Iso(date)}&pageSize=10")).Items()
            .FirstOrDefault(row => row.Str("shift") == "Day");
        var label = $"{project.Code} sheet {Iso(date)} ({crew.Length} workers)";
        if (sheet is null)
        {
            sheet = await As(engineer).PostAsync(path, new
            {
                workDate = Iso(date),
                shift = "Day",
                notes = $"{Marker} daily labor",
                submit = until != "Draft",
                lines = crew.Select(worker => new
                {
                    workerId = _workerIds[worker.Key],
                    days = 1,
                    overtimeHours = 0,
                    allocations = new[] { new { itemId = _itemIds[worker.Item], portion = 1 } },
                }).ToArray(),
            });
            Log.New($"{label}: {sheet.Str("status")}");
        }

        var start = sheet.Str("status");
        var manager = As(Cast.Manager);
        if (start == "Submitted" && until == "Approved")
        {
            sheet = await manager.PostAsync($"{path}/{sheet.Id()}/approve", new { note = Marker, rowVersion = sheet.RowVersion() });
        }
        else if (start == "Submitted" && until == "Returned")
        {
            sheet = await manager.PostAsync($"{path}/{sheet.Id()}/return",
                new { note = "يوجد عامل مسجل يومًا كاملًا وغادر الموقع ظهرًا، برجاء التصحيح / One worker left at noon: fix the days.", rowVersion = sheet.RowVersion() });
        }

        if (sheet.Str("status") == start)
        {
            Log.Kept($"{label}: {start}");
        }
        else
        {
            Log.New($"{label}: {start} -> {sheet.Str("status")}");
        }
    }

    /// <summary>A weekly settlement Omar paid in cash from his custody, then the Accountant's confirmation (which alone marks wages paid).</summary>
    private async Task SettlementAsync(string key, DateOnly from, DateOnly to, decimal expected)
    {
        var path = $"/api/projects/{ProjectId(Sites.NileTower)}/labor/settlements";
        var existing = (await As(Cast.Engineer).GetAsync(path)).Items()
            .FirstOrDefault(row => row.Str("reference") == $"DEMO-{key}");
        if (existing is not null)
        {
            Log.Kept($"settlement {key} {(decimal)existing["amount"]!:N0}: {existing.Str("status")}");
            if (existing.Str("status") == "Pending")
            {
                await As(Cast.Accountant).PostAsync($"{path}/{existing.Id()}/confirm", new { note = $"{Marker} checked against the signed payroll sheet", rowVersion = existing.RowVersion() });
                Log.New($"settlement {key}: Pending -> Confirmed");
            }

            return;
        }

        // Pay exactly what the backend says the approved days are still owed (its own arithmetic, never recomputed here).
        var payable = await As(Cast.Engineer).GetAsync($"/api/projects/{ProjectId(Sites.NileTower)}/labor/payable?from={Iso(from)}&to={Iso(to)}");
        var lines = payable.Items().Where(row => (decimal)row["outstanding"]! > 0)
            .Select(row => new { workerId = row.Id("workerId"), amount = (decimal)row["outstanding"]! }).ToArray();
        var total = lines.Sum(line => line.amount);
        if (total != expected)
        {
            throw new InvalidOperationException($"Settlement {key}: the approved days owe {total:N2}, not {expected:N2}. The crew's rates and the paid days must add up.");
        }

        var settlement = await As(Cast.Engineer).PostAsync(path, new
        {
            periodFrom = Iso(from),
            periodTo = Iso(to),
            paidOn = Iso(to.AddDays(1)),
            paidByUserId = UserId(Cast.Engineer),
            reference = $"DEMO-{key}",
            notes = $"{Marker} weekly wages paid in cash on site",
            idempotencyKey = Key($"settlement:{key}"),
            lines,
        });
        settlement = settlement["settlement"] ?? settlement;
        var start = settlement.Str("status");
        if (start == "Pending")
        {
            settlement = await As(Cast.Accountant).PostAsync($"{path}/{settlement.Id()}/confirm", new { note = $"{Marker} checked against the signed payroll sheet", rowVersion = settlement.RowVersion() });
            settlement = settlement["settlement"] ?? settlement;
            Log.New($"settlement {key} {total:N0}: {start} -> {settlement.Str("status")}");
        }
        else
        {
            Log.Kept($"settlement {key} {total:N0}: {start}");
        }
    }
}
