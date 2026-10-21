using System.Text.Json.Nodes;

namespace AlMotaheda.DemoSeed;

internal sealed partial class Demo
{
    /// <summary>
    /// The Nile Tower's approved budget, daily site reports in each review state, a report the manager asked for,
    /// archive documents (DEMO files), a subcontractor with a waterproofing contract and an approved measurement, and a
    /// rented excavator with an approved rental cost.
    /// </summary>
    private async Task SiteAsync()
    {
        Log.Section("Budget, daily reports, documents, subcontractors, equipment");
        await BudgetAsync();
        await DailyReportsAsync();
        await ReportRequestAsync();
        await DocumentsAsync();
        await SubcontractorAsync();
        await EquipmentAsync();
    }

    private async Task BudgetAsync()
    {
        var path = $"/api/projects/{ProjectId(Sites.NileTower)}/budgets";
        var ceo = As(Cast.Ceo);
        var budget = (await ceo.GetAsync(path)).Items().FirstOrDefault(row => row.Str("title").Contains("DEMO"));
        if (budget is null)
        {
            (string Group, string Item, string Description, string Unit, decimal Quantity, decimal Price)[] lines =
            [
                ("Materials", "nt.foundations", "أسمنت وركام للأساسات / Foundation cement and aggregates", "Bag", 1200, 240),
                ("Materials", "nt.columns", "حديد تسليح للأعمدة / Column rebar", "Ton", 40, 40000),
                ("Materials", "nt.slabs", "خرسانة جاهزة للأسقف / Slab ready-mix", "M3", 350, 2600),
                ("Labor", "nt.slabs", "عمالة الخرسانة / Concrete labor", "Day", 120, 4000),
                ("Labor", "nt.masonry", "عمالة المباني / Masonry labor", "Day", 90, 2500),
                ("Subcontractors", "nt.waterproofing", "مقاول العزل / Waterproofing subcontract", "M2", 1800, 140),
                ("Equipment", "nt.site", "حفار وونش / Excavator and crane", "Day", 60, 3500),
            ];
            budget = await ceo.PostAsync(path, new
            {
                title = "ميزانية التنفيذ الأولى (DEMO) / First execution budget",
                notes = Marker,
                lines = lines.Select(line => new
                {
                    costGroup = line.Group,
                    projectItemId = _itemIds[line.Item],
                    description = line.Description,
                    unit = line.Unit,
                    quantity = line.Quantity,
                    unitPrice = line.Price,
                    amount = line.Quantity * line.Price,
                }).ToArray(),
            });
            budget = budget["budget"] ?? budget;
            Log.New($"budget {budget.Str("title")}");
        }

        if (budget.Str("status") is "Draft" or "Submitted")
        {
            var approved = await As(Cast.Deputy).PostAsync($"{path}/{budget.Id()}/approve", new { rowVersion = budget.RowVersion() });
            Log.New($"budget approved ({(approved["budget"] ?? approved).Str("status")})");
        }
        else
        {
            Log.Kept($"budget: {budget.Str("status")}");
        }
    }

    private async Task DailyReportsAsync()
    {
        await DailyReportAsync(Sites.NileTower, Cast.Engineer, today.AddDays(-3), "Reviewed", 24,
            "صب خرسانة سقف الدور الثالث (60 م³) / Third-floor slab poured (60 m3)", _itemIds["nt.slabs"], 60, "M3");
        await DailyReportAsync(Sites.NileTower, Cast.Engineer, today.AddDays(-2), "Returned", 22,
            "تشوين حديد الأعمدة وتركيب الشدات / Column rebar stocked, formwork fixed", _itemIds["nt.columns"], 8, "Ton");
        await DailyReportAsync(Sites.NileTower, Cast.Engineer, today.AddDays(-1), "Submitted", 20,
            "مباني الدور الثاني - الحوائط الخارجية / Second-floor outer walls", _itemIds["nt.masonry"], 140, "M2");
        await DailyReportAsync(Sites.ShoroukSchools, Cast.Engineer2, today.AddDays(-1), "Submitted", 3,
            "حفر وتسوية موقع المبنى ب / Building B excavation and levelling", _itemIds["sh.concrete"], 120, "M3");
    }

    private async Task DailyReportAsync(ProjectSpec project, Person engineer, DateOnly date, string until, int workers, string work, int itemId, decimal quantity, string unit)
    {
        var path = $"/api/projects/{ProjectId(project)}/daily-reports";
        var report = await As(engineer).PostAsync(path, new
        {
            reportDate = Iso(date),
            workersPresent = workers,
            weather = "مشمس 29° / Sunny 29°C",
            issues = until == "Returned" ? "تأخر توريد الحديد ساعتين / Rebar delivery two hours late" : null,
            notes = $"{Marker} daily site report",
            lines = new[] { new { workType = "Concrete", description = work, unit, quantity, performedBy = "Internal", projectItemId = itemId, workersCount = workers } },
            materials = Array.Empty<object>(),
            equipment = Array.Empty<object>(),
            idempotencyKey = Key($"daily-report:{project.Code}:{Iso(date)}"),
            writtenAt = date.ToDateTime(new TimeOnly(16, 0), DateTimeKind.Utc).ToString("O"),
        });
        report = report["report"] ?? report;
        var start = report.Str("status");
        if (start == "Submitted" && until is "Reviewed" or "Returned")
        {
            var action = until == "Reviewed" ? "review" : "return";
            var note = until == "Reviewed" ? $"{Marker} reviewed on site" : "برجاء توضيح سبب تأخر الحديد وأثره على الجدول / Explain the rebar delay and its effect on the schedule.";
            report = await As(Cast.Manager).PostAsync($"{path}/{report.Id()}/{action}", new { note, rowVersion = report.RowVersion() });
            report = report["report"] ?? report;
            Log.New($"{project.Code} daily report {Iso(date)}: {start} -> {report.Str("status")}");
        }
        else
        {
            Log.Kept($"{project.Code} daily report {Iso(date)}: {start}");
        }
    }

    private async Task ReportRequestAsync()
    {
        var path = $"/api/projects/{ProjectId(Sites.NileTower)}/report-requests";
        var manager = As(Cast.Manager);
        if ((await manager.GetAsync(path)).Items().Any(row => row.Str("title").Contains("DEMO")))
        {
            Log.Kept("report request (weekly progress)");
            return;
        }

        await manager.PostAsync(path, new
        {
            isProactive = false,
            recipientUserId = UserId(Cast.Engineer),
            type = "DailyReport",
            priority = "High",
            title = "تقرير تقدم أسبوعي مصور (DEMO) / Weekly progress report with photos",
            description = "نسبة الإنجاز لكل بند، الصور، والمعوقات / Progress per item, photos and blockers",
            dueDate = Iso(today.AddDays(2)),
        });
        Log.New("report request: weekly progress (to the site engineer)");
    }

    private async Task DocumentsAsync()
    {
        var path = $"/api/projects/{ProjectId(Sites.NileTower)}/documents";
        var ceo = As(Cast.Ceo);
        var existing = (await ceo.GetAsync($"{path}?pageSize=100")).Items().Select(row => row.Str("title")).ToHashSet();
        (string Title, string Category, string File, bool Pdf)[] documents =
        [
            ("عقد المقاولة الرئيسي (DEMO) / Main construction contract", "Contract", "DEMO-main-contract.pdf", true),
            ("محضر اجتماع الموقع رقم 7 (DEMO) / Site meeting minutes 7", "MeetingMinutes", "DEMO-minutes-7.pdf", true),
            ("صورة صب سقف الدور الثالث (DEMO) / Third-floor slab pour photo", "SitePhoto", "DEMO-slab-pour.png", false),
        ];
        foreach (var document in documents)
        {
            if (existing.Contains(document.Title))
            {
                Log.Kept($"document {document.File}");
                continue;
            }

            await ceo.UploadAsync(path, new Dictionary<string, string>
            {
                ["title"] = document.Title,
                ["category"] = document.Category,
                ["description"] = $"{Marker} demo file for development testing",
                ["documentDate"] = Iso(today.AddDays(-5)),
            }, document.File, document.Pdf ? DemoPdf(document.Title) : DemoPng(document.Title), document.Pdf ? "application/pdf" : "image/png",
                idempotencyKey: Key($"document:{document.File}"));
            Log.New($"document {document.File}");
        }
    }

    private async Task SubcontractorAsync()
    {
        var ceo = As(Cast.Ceo);
        var company = (await ceo.GetAsync("/api/subcontractors?search=Delta%20Insulation&pageSize=10")).Items().FirstOrDefault(row => row.Str("nameEn").StartsWith("Delta Insulation"));
        if (company is null)
        {
            company = await ceo.PostAsync("/api/subcontractors", new
            {
                nameAr = "الدلتا للعزل (تجريبي)",
                nameEn = "Delta Insulation (demo)",
                specialty = "Finishing",
                contactName = "م. سامي رفعت",
                phone = "0220000011",
                notes = $"{Marker} demo subcontractor",
                status = "Active",
            });
            Log.New("subcontractor Delta Insulation (demo)");
        }
        else
        {
            Log.Kept("subcontractor Delta Insulation (demo)");
        }

        var path = $"/api/projects/{ProjectId(Sites.NileTower)}/subcontracts";
        var contract = (await ceo.GetAsync(path)).Items().FirstOrDefault(row => row.Str("contractNo") == "SC-DEMO-01");
        if (contract is null)
        {
            contract = await ceo.PostAsync(path, new
            {
                subcontractorId = company.Id(),
                model = "UnitRate",
                value = 252000m,
                retentionPercent = 5m,
                startDate = Iso(today.AddDays(-40)),
                endDate = Iso(today.AddDays(60)),
                status = "Active",
                contractNo = "SC-DEMO-01",
                scope = "عزل الأسطح والحمامات / Roof and wet-area waterproofing",
                projectItemId = _itemIds["nt.waterproofing"],
            });
            contract = contract["contract"] ?? contract;
            Log.New("subcontract SC-DEMO-01 (waterproofing, 252,000)");
        }
        else
        {
            Log.Kept("subcontract SC-DEMO-01");
        }

        var detail = await ceo.GetAsync($"{path}/{contract.Id()}");
        var items = (detail["items"] as JsonArray ?? (detail["contract"]?["items"] as JsonArray) ?? []).Where(item => item is not null).Select(item => item!).ToList();
        var line = items.FirstOrDefault();
        if (line is null)
        {
            line = await ceo.PostAsync($"{path}/{contract.Id()}/items", new
            {
                name = "عزل أسطح بالأغشية / Membrane roof waterproofing",
                workType = "Waterproofing",
                unit = "M2",
                quantity = 1800m,
                unitPrice = 140m,
                projectItemId = _itemIds["nt.waterproofing"],
            });
            line = line["item"] ?? line;
            Log.New("subcontract line: 1,800 m2 at 140");
        }

        var measurements = (await ceo.GetAsync($"{path}/{contract.Id()}")).Items("measurements").ToList();
        if (measurements.Count == 0)
        {
            var measurement = await As(Cast.Manager).PostAsync($"{path}/{contract.Id()}/measurements", new
            {
                contractItemId = line.Id(),
                measuredOn = Iso(today.AddDays(-5)),
                quantity = 420m,
                note = $"{Marker} roof of block A measured with the subcontractor",
            });
            measurement = measurement["measurement"] ?? measurement;
            await As(Cast.Ceo).PostAsync($"/api/projects/{ProjectId(Sites.NileTower)}/subcontract-measurements/{measurement.Id()}/approve", new { note = Marker, rowVersion = measurement.RowVersion() });
            Log.New("subcontract measurement 420 m2: approved");
        }
        else
        {
            Log.Kept("subcontract measurement");
        }
    }

    private async Task EquipmentAsync()
    {
        var ceo = As(Cast.Ceo);
        var found = (await ceo.GetAsync("/api/equipment?search=EQ-DEMO-01&pageSize=10")).Items().FirstOrDefault(row => row.Str("assetNo") == "EQ-DEMO-01");
        if (found is null)
        {
            found = await ceo.PostAsync("/api/equipment", new
            {
                assetNo = "EQ-DEMO-01",
                nameAr = "حفار كاتربيلر 320",
                nameEn = "Caterpillar 320 excavator",
                category = "Heavy",
                typeName = "Excavator",
                ownership = "Rented",
                condition = "Good",
                status = "InService",
                notes = Marker,
                rental = new { supplierId = _supplierIds["equipment"], rateBasis = "Day", rate = 3500m, startDate = Iso(today.AddDays(-30)), notes = Marker },
            });
            Log.New("equipment EQ-DEMO-01 (rented excavator)");
        }
        else
        {
            Log.Kept("equipment EQ-DEMO-01");
        }

        var projectPath = $"/api/projects/{ProjectId(Sites.NileTower)}";
        var assigned = (await ceo.GetAsync($"{projectPath}/equipment?pageSize=50")).Items().Any(row => (int?)row["equipmentId"] == found.Id() && row["endDate"] is null);
        if (!assigned)
        {
            await ceo.PostAsync($"{projectPath}/equipment", new { equipmentId = found.Id(), responsibleUserId = UserId(Cast.Engineer), operatorName = "مشغل المورد", startDate = Iso(today.AddDays(-30)), notes = Marker });
            Log.New("EQ-DEMO-01 on NT-2026-01");
        }

        var costs = (await ceo.GetAsync($"{projectPath}/equipment-costs?pageSize=50")).Items().ToList();
        if (!costs.Any(row => row.Str("invoiceNo") == "NER-DEMO-410"))
        {
            var cost = await As(Cast.Manager).PostAsync($"{projectPath}/equipment-costs", new
            {
                equipmentId = found.Id(),
                costDate = Iso(today.AddDays(-7)),
                type = "Rental",
                basis = "Day",
                quantity = 6m,
                rate = 3500m,
                amount = 21000m,
                description = $"{Marker} 6 days of excavation for the water tank",
                supplierId = _supplierIds["equipment"],
                channel = "Credit",
                invoiceNo = "NER-DEMO-410",
                projectItemId = _itemIds["nt.site"],
            });
            cost = cost["cost"] ?? cost;
            await As(Cast.Accountant).PostAsync($"{projectPath}/equipment-costs/{cost.Id()}/approve", new { note = Marker, rowVersion = cost.RowVersion() });
            Log.New("equipment cost 21,000 (6 rental days): approved");
        }
        else
        {
            Log.Kept("equipment cost NER-DEMO-410");
        }
    }
}
