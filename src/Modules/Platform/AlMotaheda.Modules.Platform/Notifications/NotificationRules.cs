namespace AlMotaheda.Modules.Platform.Notifications;

/// <summary>The notification types. Stored by name and returned to the client, so renaming one is a breaking change.</summary>
internal static class NotificationTypes
{
    public const string RecordApproved = "RecordApproved";
    public const string RecordReturned = "RecordReturned";
    public const string RecordRejected = "RecordRejected";
    public const string RecordReviewed = "RecordReviewed";
    public const string ReportRequested = "ReportRequested";
    public const string ReportStarted = "ReportStarted";
    public const string ReportSubmitted = "ReportSubmitted";
    public const string ReportCompleted = "ReportCompleted";
    public const string ReportCancelled = "ReportCancelled";
    public const string ExportReady = "ExportReady";
    public const string ExportFailed = "ExportFailed";
    public const string LocationUnavailable = "LocationUnavailable";
    public const string LocationAvailable = "LocationAvailable";
    public const string OutsideSite = "OutsideSite";
    public const string BackInsideSite = "BackInsideSite";
    public const string ReviewRequested = "ReviewRequested";
}

/// <summary>A notification to store: who gets it, its type and its title in both languages.</summary>
internal sealed record NotificationDraft(int RecipientUserId, string Type, string TitleAr, string TitleEn);

/// <summary>
/// Which status changes tell whom, with no database access. The rules follow the review flows that already exist
/// (01-projects-core.md §3.2): the person who recorded a record hears when it is approved, returned or rejected;
/// report requests notify their recipient and their requester.
/// </summary>
/// <remarks>
/// <para>Nobody is told about their own action. A rule names the record's user field (<c>CreatedBy</c>,
/// <c>RecordedBy</c>, …), so the recipient comes from the record itself, never from the client.</para>
/// <para>"Something is waiting for your review" stays in the work inbox (<c>GET /api/inbox</c>), which is computed
/// from the records and the reviewer's permissions on every call.</para>
/// </remarks>
internal static class NotificationRules
{
    private sealed record Rule(string Entity, string? StatusAfter, string RecipientField, string Type);

    /// <summary>The record names used in the titles, in English and Arabic.</summary>
    private static readonly IReadOnlyDictionary<string, (string En, string Ar)> Nouns = new Dictionary<string, (string, string)>
    {
        ["ProjectExpense"] = ("expense or supply", "المصروف أو التوريد"),
        ["EquipmentCost"] = ("equipment cost", "تكلفة المعدة"),
        ["DailySheet"] = ("daily labor sheet", "كشف العمالة اليومي"),
        ["ProgressMeasurement"] = ("measured work", "الأعمال المقاسة"),
        ["ManpowerSheet"] = ("subcontractor manpower sheet", "كشف عمالة مقاول الباطن"),
        ["DailyReport"] = ("daily site report", "التقرير اليومي للموقع"),
        ["ItemPrice"] = ("price change request", "طلب تغيير السعر"),
        ["Budget"] = ("budget", "الموازنة"),
        ["ReportRequest"] = ("report request", "طلب التقرير"),
        ["ExpenseAdjustment"] = ("correction of a record", "تصحيح السجل"),
        ["EquipmentCostAdjustment"] = ("correction of an equipment cost", "تصحيح تكلفة المعدة"),
        ["SalarySheet"] = ("salary sheet", "كشف الرواتب"),
        ["Requisition"] = ("purchase requisition", "طلب الشراء"),
        ["QuotationRequest"] = ("quotation set", "مجموعة عروض الأسعار"),
        ["ReportRun"] = ("report export", "تصدير التقرير"),
        ["LocationUnavailableAlert"] = ("location alert", "تنبيه الموقع"),
        ["OutsideSiteAlert"] = ("location alert", "تنبيه الموقع"),
        ["MasterItemPrice"] = ("master price change request", "طلب تغيير السعر في القائمة الرئيسية"),
        ["PriceVariance"] = ("price variance", "فرق السعر"),
        ["RepairRequest"] = ("repair request", "طلب الإصلاح"),
        ["ProjectWorkDays"] = ("project days", "أيام العمل على المشروع"),
    };

    /// <summary>StatusAfter <c>null</c> = the record was just added.</summary>
    private static readonly Rule[] Rules =
    [
        .. Reviewed("ProjectExpense", "CreatedBy", returns: true),
        .. Reviewed("EquipmentCost", "CreatedBy", returns: true),
        .. Reviewed("DailySheet", "RecordedBy", returns: true),
        .. Reviewed("ProgressMeasurement", "CreatedBy", returns: false),
        .. Reviewed("ManpowerSheet", "CreatedBy", returns: false),
        new("DailyReport", "Reviewed", "AuthorUserId", NotificationTypes.RecordReviewed),
        new("DailyReport", "Returned", "AuthorUserId", NotificationTypes.RecordReturned),
        new("ItemPrice", "Approved", "RequestedBy", NotificationTypes.RecordApproved),
        new("ItemPrice", "Rejected", "RequestedBy", NotificationTypes.RecordRejected),
        new("Budget", "Approved", "CreatedBy", NotificationTypes.RecordApproved),
        // docs/cost-flow: decisions on the new review flows go back to whoever asked.
        new("MasterItemPrice", "Approved", "RequestedBy", NotificationTypes.RecordApproved),
        new("MasterItemPrice", "Rejected", "RequestedBy", NotificationTypes.RecordRejected),
        new("PriceVariance", "Accepted", "RaisedBy", NotificationTypes.RecordApproved),
        new("PriceVariance", "Rejected", "RaisedBy", NotificationTypes.RecordRejected),
        new("RepairRequest", "Approved", "RequestedBy", NotificationTypes.RecordApproved),
        new("RepairRequest", "Rejected", "RequestedBy", NotificationTypes.RecordRejected),
        new("RepairRequest", "Completed", "RequestedBy", NotificationTypes.RecordReviewed),
        .. Reviewed("ProjectWorkDays", "CreatedBy", returns: true),
        new("ExpenseAdjustment", "Approved", "RequestedBy", NotificationTypes.RecordApproved),
        new("ExpenseAdjustment", "Rejected", "RequestedBy", NotificationTypes.RecordRejected),
        new("EquipmentCostAdjustment", "Approved", "RequestedBy", NotificationTypes.RecordApproved),
        new("EquipmentCostAdjustment", "Rejected", "RequestedBy", NotificationTypes.RecordRejected),
        new("SalarySheet", "Approved", "SubmittedBy", NotificationTypes.RecordApproved),
        new("SalarySheet", "Returned", "SubmittedBy", NotificationTypes.RecordReturned),
        new("Requisition", "Approved", "CreatedBy", NotificationTypes.RecordApproved),
        new("Requisition", "Rejected", "CreatedBy", NotificationTypes.RecordRejected),
        new("QuotationRequest", "Selected", "SubmittedBy", NotificationTypes.RecordApproved),
        new("QuotationRequest", "Returned", "SubmittedBy", NotificationTypes.RecordReturned),
        new("ReportRun", "Ready", "RequestedBy", NotificationTypes.ExportReady),
        new("ReportRun", "Failed", "RequestedBy", NotificationTypes.ExportFailed),
        new("ReportRequest", null, "RecipientUserId", NotificationTypes.ReportRequested),
        new("ReportRequest", "InProgress", "RequesterUserId", NotificationTypes.ReportStarted),
        new("ReportRequest", "Submitted", "RequesterUserId", NotificationTypes.ReportSubmitted),
        new("ReportRequest", "Returned", "RecipientUserId", NotificationTypes.RecordReturned),
        new("ReportRequest", "Completed", "RecipientUserId", NotificationTypes.ReportCompleted),
        new("ReportRequest", "Cancelled", "RecipientUserId", NotificationTypes.ReportCancelled),

        // Site engineers' location (Projects module): one alert row per recipient, opened once per outage and
        // closed once when it is over. Closing because sharing was turned off ("Ended") tells nobody.
        new("LocationUnavailableAlert", null, "RecipientUserId", NotificationTypes.LocationUnavailable),
        new("LocationUnavailableAlert", "Recovered", "RecipientUserId", NotificationTypes.LocationAvailable),
        new("OutsideSiteAlert", null, "RecipientUserId", NotificationTypes.OutsideSite),
        new("OutsideSiteAlert", "Recovered", "RecipientUserId", NotificationTypes.BackInsideSite),
    ];

    /// <summary>The notifications one changed record causes. <paramref name="actorUserId"/> made the change.</summary>
    public static IReadOnlyList<NotificationDraft> For(EntityChange change, int? actorUserId)
    {
        var drafts = new List<NotificationDraft>();

        foreach (var rule in Rules)
        {
            if (rule.Entity != change.Entity || !Matches(rule, change))
            {
                continue;
            }

            if (!change.People.TryGetValue(rule.RecipientField, out var recipient) || recipient == actorUserId)
            {
                continue;
            }

            var (en, ar) = Titles(rule.Type, Nouns[rule.Entity]);
            drafts.Add(new NotificationDraft(recipient, rule.Type, ar, en));
        }

        return drafts;
    }

    private static bool Matches(Rule rule, EntityChange change) =>
        rule.StatusAfter is null
            ? change.Action == "Added"
            : change.Action == "Modified" && change.StatusAfter == rule.StatusAfter && change.StatusBefore != change.StatusAfter;

    private static IEnumerable<Rule> Reviewed(string entity, string recorderField, bool returns)
    {
        yield return new Rule(entity, "Approved", recorderField, NotificationTypes.RecordApproved);
        yield return new Rule(entity, "Rejected", recorderField, NotificationTypes.RecordRejected);
        if (returns)
        {
            yield return new Rule(entity, "Returned", recorderField, NotificationTypes.RecordReturned);
        }
    }

    private static (string En, string Ar) Titles(string type, (string En, string Ar) noun) => type switch
    {
        NotificationTypes.RecordApproved => ($"Your {noun.En} was approved", $"تم اعتماد {noun.Ar}"),
        NotificationTypes.RecordReturned => ($"Your {noun.En} was returned for changes", $"تمت إعادة {noun.Ar} للتعديل"),
        NotificationTypes.RecordRejected => ($"Your {noun.En} was rejected", $"تم رفض {noun.Ar}"),
        NotificationTypes.RecordReviewed => ($"Your {noun.En} was reviewed", $"تمت مراجعة {noun.Ar}"),
        NotificationTypes.ReportRequested => ("A report was requested from you", "طُلب منك تقرير جديد"),
        NotificationTypes.ReportStarted => ("Work started on your report request", "بدأ العمل على طلب التقرير الذي أرسلته"),
        NotificationTypes.ReportSubmitted => ("The report you requested was submitted", "تم تسليم التقرير الذي طلبته"),
        NotificationTypes.ReportCompleted => ("Your report was accepted as complete", "تم قبول التقرير واعتباره مكتملاً"),
        NotificationTypes.ReportCancelled => ("A report request to you was cancelled", "تم إلغاء طلب تقرير موجّه إليك"),
        NotificationTypes.ExportReady => ("Your report export is ready to download", "تصدير التقرير جاهز للتنزيل"),
        NotificationTypes.ExportFailed => ("Your report export failed", "تعذّر تصدير التقرير"),
        NotificationTypes.LocationUnavailable => ("A site engineer's location is unavailable", "موقع مهندس الموقع غير متاح"),
        NotificationTypes.LocationAvailable => ("A site engineer's location is available again", "عاد موقع مهندس الموقع متاحاً"),
        NotificationTypes.OutsideSite => ("A site engineer is outside the site area", "مهندس الموقع خارج نطاق الموقع"),
        NotificationTypes.BackInsideSite => ("A site engineer is back in the site area", "عاد مهندس الموقع إلى نطاق الموقع"),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown notification type."),
    };
}
