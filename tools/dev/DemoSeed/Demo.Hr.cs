using System.Text.Json.Nodes;

namespace AlMotaheda.DemoSeed;

internal sealed partial class Demo
{
    /// <summary>
    /// Employee self-service requests in every state, HR's review (HR prepares and asks; deciding needs hr Approve,
    /// which only the Executive has by default), the investigation of the fuel claim referred above with the driver's
    /// answer, a proposed bonus, and last month's salary sheet submitted for the Accountant's review (nothing paid).
    /// </summary>
    private async Task HrAsync()
    {
        Log.Section("HR requests, referral, payroll");
        var hr = As(Cast.Hr);
        var ceo = As(Cast.Ceo);

        // The truck driver's case: HR opens the investigation; the driver answers it from his own account.
        if (_rejectedClaimReferral != 0)
        {
            var referral = await hr.GetAsync($"/api/hr/referrals/{_rejectedClaimReferral}");
            referral = referral["referral"] ?? referral;
            if (referral.Str("status") == "Open")
            {
                await hr.PostAsync($"/api/hr/referrals/{_rejectedClaimReferral}/start-investigation", new { note = $"{Marker} meeting with the driver scheduled", rowVersion = referral.RowVersion() });
                Log.New("HR referral: Open -> UnderInvestigation");
            }
            else
            {
                Log.Kept($"HR referral: {referral.Str("status")}");
            }
        }

        await HrRequestAsync(Cast.Driver, "driver-leave", new
        {
            type = "Leave", subject = "إجازة اعتيادية 3 أيام / Annual leave, 3 days",
            details = "ظروف عائلية، يغطي أشرف كمال خلال الإجازة / Family matters; Ashraf Kamal covers the pickup",
            fromDate = Iso(today.AddDays(7)), toDate = Iso(today.AddDays(9)),
        }, "driver annual leave", steps: []);

        await HrRequestAsync(Cast.Engineer, "omar-attendance", new
        {
            type = "AttendanceCorrection", subject = "تصحيح حضور يوم الأحد / Sunday attendance correction",
            details = "كنت في زيارة للمورد صباحًا ولم يسجل الحضور / I was at the supplier in the morning, so no check-in was recorded",
            fromDate = Iso(today.AddDays(-6)), toDate = Iso(today.AddDays(-6)),
        }, "engineer attendance correction", steps: ["start-review", "request-info"]);

        await HrRequestAsync(Cast.Accountant, "mona-permission", new
        {
            type = "Permission", subject = "إذن انصراف مبكر / Early leave permission",
            details = "موعد طبي الساعة 3 عصرًا / Medical appointment at 3 pm",
            fromDate = Iso(today.AddDays(-4)), toDate = Iso(today.AddDays(-4)),
        }, "accountant permission (approved)", steps: ["start-review", "approve"]);

        await HrRequestAsync(Cast.Purchasing, "tarek-overtime", new
        {
            type = "OvertimeClarification", subject = "ساعات إضافية لشهر سبتمبر / September overtime",
            details = "عملت 12 ساعة إضافية أثناء الجرد / I worked 12 extra hours during the stock count",
        }, "purchasing overtime question (rejected)", steps: ["start-review", "reject"]);

        if (_rejectedClaimReferral != 0)
        {
            await HrRequestAsync(Cast.Driver2, "ashraf-case", new
            {
                type = "CaseResponse", subject = "رد على مطالبة الوقود المرفوضة / Answer about the rejected fuel claim",
                details = "تم تموين الشاحنة مرتين في نفس اليوم ودمجت الإيصالين / The truck was filled twice that day; I merged both receipts",
                referralId = _rejectedClaimReferral,
            }, "truck driver's answer to the case", steps: []);
        }

        // A proposed bonus for the site engineer (counts only through the salary sheet's review).
        var month = today.AddMonths(-1);
        var monthKey = $"{month:yyyy-MM}";
        var inputs = List(await hr.GetAsync($"/api/hr/payroll-inputs?month={monthKey}&employeeId={EmployeeId(Cast.Engineer)}")).ToList();
        if (inputs.Count == 0)
        {
            await hr.PostAsync("/api/hr/payroll-inputs", new
            {
                employeeId = EmployeeId(Cast.Engineer),
                month = monthKey,
                kind = "Bonus",
                amount = 2000m,
                reason = $"{Marker} مكافأة إنجاز صب سقف الدور الثالث قبل الموعد / Third-floor slab cast ahead of schedule",
            });
            Log.New($"payroll input: bonus 2,000 for {Cast.Engineer.NameEn} ({monthKey})");
        }
        else
        {
            Log.Kept($"payroll input for {monthKey}");
        }

        var sheets = (await hr.GetAsync("/api/hr/salary-sheets?pageSize=50")).Items().ToList();
        var sheet = sheets.FirstOrDefault(row => row.Str("month").StartsWith(monthKey));
        if (sheet is null)
        {
            sheet = await hr.PostAsync("/api/hr/salary-sheets", new { month = monthKey });
            sheet = sheet["sheet"] ?? sheet;
            Log.New($"salary sheet {monthKey}: {sheet.Str("status")}");
        }

        if (sheet.Str("status") == "Draft")
        {
            sheet = await hr.PostAsync($"/api/hr/salary-sheets/{sheet.Id()}/submit", new { rowVersion = sheet.RowVersion() });
            sheet = sheet["sheet"] ?? sheet;
            Log.New($"salary sheet {monthKey}: Draft -> {sheet.Str("status")}");
        }
        else
        {
            Log.Kept($"salary sheet {monthKey}: {sheet.Str("status")}");
        }
    }

    private async Task HrRequestAsync(Person employee, string key, object fields, string label, string[] steps)
    {
        var body = System.Text.Json.JsonSerializer.SerializeToNode(fields)!.AsObject();
        body["idempotencyKey"] = Key($"hr-request:{key}");
        var request = await As(employee).PostAsync("/api/hr/my/requests", body);
        request = request["request"] ?? request;
        var start = request.Str("status");
        if (start != "Submitted" || steps.Length == 0)
        {
            Log.Kept($"HR request {request.Str("number")} {label}: {start}");
            return;
        }

        foreach (var step in steps)
        {
            var path = $"/api/hr/requests/{request.Id()}/{step}";
            var current = await As(Cast.Hr).GetAsync($"/api/hr/requests/{request.Id()}");
            var rowVersion = (current["request"] ?? current).RowVersion();
            JsonNode result = step switch
            {
                "start-review" => await As(Cast.Hr).PostAsync(path, new { note = Marker, rowVersion }),
                "request-info" => await As(Cast.Hr).PostAsync(path, new { text = "برجاء إرفاق خطاب المورد أو صورة إذن الخروج / Attach the supplier's letter or the exit pass.", attachmentIds = Array.Empty<int>(), rowVersion }),
                // Deciding needs hr Approve: the Executive by default.
                "approve" => await As(Cast.Ceo).PostAsync(path, new { note = $"{Marker} approved", rowVersion }),
                _ => await As(Cast.Ceo).PostAsync(path, new { note = "لا يوجد اعتماد مسبق للساعات الإضافية / The overtime was not approved beforehand.", rowVersion }),
            };
            request = result["request"] ?? result;
        }

        Log.New($"HR request {request.Str("number")} {label}: {start} -> {request.Str("status")}");
    }
}
