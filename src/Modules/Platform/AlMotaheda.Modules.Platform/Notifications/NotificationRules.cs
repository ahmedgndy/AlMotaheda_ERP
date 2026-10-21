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
    public const string PriceAboveRange = "PriceAboveRange";
    public const string CostRecorded = "CostRecorded";
    public const string PaymentPaid = "PaymentPaid";
    public const string PaymentFailed = "PaymentFailed";
    public const string RecordReversed = "RecordReversed";
    public const string PayrollSettled = "PayrollSettled";
    public const string CustodyChanged = "CustodyChanged";
    public const string TransferRequested = "TransferRequested";
    public const string TransferReceivable = "TransferReceivable";
    public const string TransferReceived = "TransferReceived";
    public const string ClaimSubmitted = "ClaimSubmitted";
    public const string ClaimEvidenceRequested = "ClaimEvidenceRequested";
    public const string ClaimEscalated = "ClaimEscalated";
    public const string HrReferred = "HrReferred";
    public const string HrReferralClosed = "HrReferralClosed";
    public const string AdjustmentPosted = "AdjustmentPosted";
    public const string AdjustmentDisputed = "AdjustmentDisputed";
    public const string AdjustmentResolved = "AdjustmentResolved";
    public const string HrRequestSubmitted = "HrRequestSubmitted";
    public const string HrRequestNeedsInfo = "HrRequestNeedsInfo";
    public const string HrRequestDecided = "HrRequestDecided";
    public const string HrRequestAnswered = "HrRequestAnswered";

    // Office management (company office cash custody).
    public const string OfficeExpenseSubmitted = "OfficeExpenseSubmitted";
    public const string OfficeExpensePosted = "OfficeExpensePosted";
    public const string OfficeFundingRequested = "OfficeFundingRequested";
    public const string OfficeFundsDisbursed = "OfficeFundsDisbursed";
    public const string OfficeLowBalance = "OfficeLowBalance";
    public const string OfficeCashDiscrepancy = "OfficeCashDiscrepancy";
    public const string OfficeSpendRequested = "OfficeSpendRequested";
    public const string LowBattery = "LowBattery";
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
        ["PriceRange"] = ("price range request", "طلب النطاق السعري"),
        ["PriceAboveRangeAlert"] = ("purchase above its reference price", "شراء بسعر أعلى من السعر المرجعي"),
        ["PriceExceptionAlert"] = ("price above its reference range", "سعر أعلى من النطاق المرجعي"),
        ["PriceException"] = ("price exception", "استثناء السعر"),
        ["PriceCategory"] = ("price category", "تصنيف الأسعار"),
        ["PriceUnit"] = ("price unit", "وحدة السعر"),
        ["CostRecordedAlert"] = ("new site cost", "تكلفة موقع جديدة"),
        ["PaymentRequest"] = ("payment request", "طلب الصرف"),
        ["PayrollSettlement"] = ("weekly labor settlement", "تسوية العمالة الأسبوعية"),
        ["SettlementAlert"] = ("weekly labor settlement", "تسوية العمالة الأسبوعية"),
        ["ContractVariation"] = ("subcontract value change", "تعديل قيمة عقد الباطن"),
        ["CustodyEntry"] = ("custody", "العهدة"),
        ["CustodyTransfer"] = ("custody transfer", "تحويل العهدة"),
        ["CustodyTransferAlert"] = ("custody transfer", "تحويل العهدة"),
        ["VehicleExpense"] = ("vehicle expense claim", "مطالبة مصروف المركبة"),
        ["VehicleExpenseAlert"] = ("vehicle expense claim", "مطالبة مصروف المركبة"),
        ["VehicleViolation"] = ("vehicle violation", "مخالفة المركبة"),
        ["HrReferral"] = ("HR referral", "إحالة الموارد البشرية"),
        ["HrRequest"] = ("HR request", "طلب الموارد البشرية"),
        ["CustodyAdjustment"] = ("custody adjustment", "تسوية العهدة"),
        ["HrRequestAlert"] = ("HR request", "طلب الموارد البشرية"),
        ["VehicleCustodyEntry"] = ("vehicle custody", "عهدة المركبة"),
        ["OfficeExpense"] = ("office expense", "مصروف المكتب"),
        ["OfficeFundingRequest"] = ("custody funding request", "طلب تغذية العهدة"),
        ["OfficeSpendRequest"] = ("office repair or purchase request", "طلب صيانة أو شراء للمكتب"),
        ["OfficeCashCount"] = ("cash count", "جرد النقدية"),
        ["OfficeExpenseSubmittedAlert"] = ("office expense", "مصروف المكتب"),
        ["OfficeExpensePostedAlert"] = ("office expense", "مصروف المكتب"),
        ["OfficeFundingRequestedAlert"] = ("custody funding request", "طلب تغذية العهدة"),
        ["OfficeFundsDisbursedAlert"] = ("custody funding", "تغذية العهدة"),
        ["OfficeLowBalanceAlert"] = ("office custody", "عهدة المكتب"),
        ["OfficeDiscrepancyAlert"] = ("cash count", "جرد النقدية"),
        ["OfficeSpendRequestedAlert"] = ("office repair or purchase request", "طلب صيانة أو شراء للمكتب"),
        ["LowBatteryAlert"] = ("location alert", "تنبيه الموقع"),
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

        // The company price list (Projects module): the requester hears the Executive's decision.
        new("PriceRange", "Approved", "RequestedBy", NotificationTypes.RecordApproved),
        new("PriceRange", "Rejected", "RequestedBy", NotificationTypes.RecordRejected),

        // Cost alerts (Projects module): one row per recipient, written in the record's own transaction, so a
        // notification exists only for a committed record and a retried delivery stores it once.
        new("PriceAboveRangeAlert", null, "RecipientUserId", NotificationTypes.PriceAboveRange),

        // Price Control: the people who decide and reconcile hear of a new case (one alert row each); the submitter
        // hears the Executive's decision. A withdrawn case (the transaction changed) tells nobody.
        new("PriceExceptionAlert", null, "RecipientUserId", NotificationTypes.PriceAboveRange),
        new("PriceException", "ApprovedOnce", "SubmittedBy", NotificationTypes.RecordApproved),
        new("PriceException", "ApprovedWithPriceChange", "SubmittedBy", NotificationTypes.RecordApproved),
        new("PriceException", "Rejected", "SubmittedBy", NotificationTypes.RecordRejected),
        new("CostRecordedAlert", null, "RecipientUserId", NotificationTypes.CostRecorded),

        // Payment requests (Finance): whoever asked hears the Executive's decision and the payment's outcome. The
        // notification holds no amount or payee: it only points to the request.
        new("PaymentRequest", "Approved", "CreatedBy", NotificationTypes.RecordApproved),
        new("PaymentRequest", "Rejected", "CreatedBy", NotificationTypes.RecordRejected),
        new("PaymentRequest", "Paid", "CreatedBy", NotificationTypes.PaymentPaid),
        new("PaymentRequest", "Failed", "CreatedBy", NotificationTypes.PaymentFailed),

        // Weekly labor settlements (Workforce): the managers, Executives and accountants hear of a new one (alert
        // rows, saved with it); its recorder and the custody holder hear the reviewer's decision.
        new("SettlementAlert", null, "RecipientUserId", NotificationTypes.PayrollSettled),
        new("PayrollSettlement", "Confirmed", "RecordedBy", NotificationTypes.RecordApproved),
        new("PayrollSettlement", "Rejected", "RecordedBy", NotificationTypes.RecordRejected),
        new("PayrollSettlement", "Reversed", "RecordedBy", NotificationTypes.RecordReversed),
        new("PayrollSettlement", "Confirmed", "PaidByUserId", NotificationTypes.RecordApproved),
        new("PayrollSettlement", "Rejected", "PaidByUserId", NotificationTypes.RecordRejected),
        new("PayrollSettlement", "Reversed", "PaidByUserId", NotificationTypes.RecordReversed),

        // Subcontract value changes (Subcontractors): whoever asked hears the decision.
        new("ContractVariation", "Approved", "RequestedBy", NotificationTypes.RecordApproved),
        new("ContractVariation", "Rejected", "RequestedBy", NotificationTypes.RecordRejected),

        // Cash custody (Projects): the holder hears of every funding, return or wage payment someone else records.
        new("CustodyEntry", null, "HolderUserId", NotificationTypes.CustodyChanged),

        // Custody transfers (Finance): the Executives hear of a request and the destination's holder of a disbursement
        // (alert rows, saved with the transfer); the requester hears the decision, the receipt and a reversal.
        new("CustodyTransferAlert", null, "RecipientUserId", NotificationTypes.TransferRequested),
        new("CustodyTransfer", "Approved", "RequestedBy", NotificationTypes.RecordApproved),
        new("CustodyTransfer", "Rejected", "RequestedBy", NotificationTypes.RecordRejected),
        new("CustodyTransfer", "Received", "RequestedBy", NotificationTypes.TransferReceived),
        new("CustodyTransfer", "Received", "DisbursedBy", NotificationTypes.TransferReceived),
        new("CustodyTransfer", "Reversed", "RequestedBy", NotificationTypes.RecordReversed),

        // Vehicle expense claims (Fleet): the accountants and the Executives hear of a new claim (alert rows); the
        // submitter hears every review outcome. A posted claim's reversal is told to the submitter too.
        new("VehicleExpenseAlert", null, "RecipientUserId", NotificationTypes.ClaimSubmitted),
        new("VehicleExpense", "Approved", "CreatedBy", NotificationTypes.RecordApproved),
        new("VehicleExpense", "Rejected", "CreatedBy", NotificationTypes.RecordRejected),
        new("VehicleExpense", "EvidenceRequested", "CreatedBy", NotificationTypes.ClaimEvidenceRequested),
        new("VehicleExpense", "Escalated", "CreatedBy", NotificationTypes.ClaimEscalated),

        // Vehicle violations (Fleet): the recorder hears the review.
        new("VehicleViolation", "Resolved", "CreatedBy", NotificationTypes.RecordReviewed),
        new("VehicleViolation", "Dismissed", "CreatedBy", NotificationTypes.RecordReviewed),

        // HR referrals: whoever referred the case hears when HR closes it.
        new("HrReferral", "Closed", "ReferredBy", NotificationTypes.HrReferralClosed),

        // A reviewer's adjustment of someone's custody: the holder is told; a dispute goes back to the reviewer; the resolution to the holder.
        new("CustodyAdjustment", null, "HolderUserId", NotificationTypes.AdjustmentPosted),
        new("CustodyAdjustment", "Disputed", "CreatedBy", NotificationTypes.AdjustmentDisputed),
        new("CustodyAdjustment", "Upheld", "HolderUserId", NotificationTypes.AdjustmentResolved),
        new("CustodyAdjustment", "Reversed", "HolderUserId", NotificationTypes.AdjustmentResolved),

        // An employee's HR request: HR is told through the alert rows (a new request, or an answer to its question); the
        // employee is told when HR asks for more and when it is decided or answered.
        new("HrRequestAlert", null, "RecipientUserId", NotificationTypes.HrRequestSubmitted),
        new("HrRequest", "NeedsInfo", "SubmittedBy", NotificationTypes.HrRequestNeedsInfo),
        new("HrRequest", "Approved", "SubmittedBy", NotificationTypes.HrRequestDecided),
        new("HrRequest", "Rejected", "SubmittedBy", NotificationTypes.HrRequestDecided),
        new("HrRequest", "Closed", "SubmittedBy", NotificationTypes.HrRequestDecided),
        new("HrRequestMessage", null, "RecipientUserId", NotificationTypes.HrRequestAnswered),
        // Office management (Office): the custodian hears every decision on what they submitted; reviewers hear of new
        // items through alert rows (one per reviewer, the submitter never told about their own record).
        new("OfficeExpense", "Approved", "CreatedBy", NotificationTypes.RecordApproved),
        new("OfficeExpense", "Returned", "CreatedBy", NotificationTypes.RecordReturned),
        new("OfficeExpense", "Rejected", "CreatedBy", NotificationTypes.RecordRejected),
        new("OfficeFundingRequest", "Approved", "RequestedBy", NotificationTypes.RecordApproved),
        new("OfficeFundingRequest", "Rejected", "RequestedBy", NotificationTypes.RecordRejected),
        new("OfficeSpendRequest", "Approved", "RequestedBy", NotificationTypes.RecordApproved),
        new("OfficeSpendRequest", "Rejected", "RequestedBy", NotificationTypes.RecordRejected),
        new("OfficeCashCount", "Accepted", "CountedBy", NotificationTypes.RecordReviewed),
        new("OfficeCashCount", "Adjusted", "CountedBy", NotificationTypes.RecordReviewed),
        new("OfficeExpenseSubmittedAlert", null, "RecipientUserId", NotificationTypes.OfficeExpenseSubmitted),
        new("OfficeExpensePostedAlert", null, "RecipientUserId", NotificationTypes.OfficeExpensePosted),
        new("OfficeFundingRequestedAlert", null, "RecipientUserId", NotificationTypes.OfficeFundingRequested),
        new("OfficeFundsDisbursedAlert", null, "RecipientUserId", NotificationTypes.OfficeFundsDisbursed),
        new("OfficeLowBalanceAlert", null, "RecipientUserId", NotificationTypes.OfficeLowBalance),
        new("OfficeDiscrepancyAlert", null, "RecipientUserId", NotificationTypes.OfficeCashDiscrepancy),
        new("OfficeSpendRequestedAlert", null, "RecipientUserId", NotificationTypes.OfficeSpendRequested),
        // The phone said its battery is low while it could still report. Its recovery shows on the map only.
        new("LowBatteryAlert", null, "RecipientUserId", NotificationTypes.LowBattery),
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

            // Two rules can name the same person (a settlement's recorder who also paid it): they are told once.
            if (drafts.Any(draft => draft.RecipientUserId == recipient && draft.Type == rule.Type))
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
        NotificationTypes.PriceAboveRange => ("A purchase was recorded above its reference price", "تم تسجيل شراء بسعر أعلى من السعر المرجعي"),
        NotificationTypes.CostRecorded => ("A new site cost is waiting for review", "تكلفة موقع جديدة بانتظار المراجعة"),
        NotificationTypes.PaymentPaid => ("Your payment request was paid", "تم صرف طلب الصرف الخاص بك"),
        NotificationTypes.PaymentFailed => ("Your payment request failed at the bank", "تعذّر تنفيذ طلب الصرف لدى البنك"),
        NotificationTypes.RecordReversed => ($"Your {noun.En} was reversed", $"تم عكس {noun.Ar}"),
        NotificationTypes.PayrollSettled => ("A weekly labor settlement is waiting for review", "تسوية عمالة أسبوعية بانتظار المراجعة"),
        NotificationTypes.CustodyChanged => ("Your custody was updated", "تم تحديث عهدتك"),
        NotificationTypes.TransferRequested => ("A custody transfer is waiting for your decision", "تحويل عهدة بانتظار قرارك"),
        NotificationTypes.TransferReceivable => ("Cash was handed out to your custody: confirm receiving it", "تم صرف نقدية لعهدتك: أكّد الاستلام"),
        NotificationTypes.TransferReceived => ($"The {noun.En} was received", $"تم استلام {noun.Ar}"),
        NotificationTypes.ClaimSubmitted => ("A vehicle expense claim is waiting for review", "مطالبة مصروف مركبة بانتظار المراجعة"),
        NotificationTypes.ClaimEvidenceRequested => ("More evidence was asked for your vehicle expense claim", "طُلبت مستندات إضافية لمطالبتك"),
        NotificationTypes.ClaimEscalated => ("Your vehicle expense claim was escalated to an Executive", "تمت إحالة مطالبتك إلى الإدارة العليا"),
        NotificationTypes.HrReferred => ("A case was referred to HR", "تمت إحالة حالة إلى الموارد البشرية"),
        NotificationTypes.HrReferralClosed => ($"The {noun.En} you made was closed", $"تم إغلاق {noun.Ar} التي أحلتها"),
        NotificationTypes.AdjustmentPosted => ("Your custody balance was adjusted by a reviewer", "تم تعديل رصيد عهدتك بتسوية من المراجع"),
        NotificationTypes.AdjustmentDisputed => ("A holder disputed your custody adjustment", "اعترض أمين العهدة على تسويتك"),
        NotificationTypes.AdjustmentResolved => ("Your dispute of a custody adjustment was decided", "تم البت في اعتراضك على تسوية العهدة"),
        NotificationTypes.HrRequestSubmitted => ("An HR request is waiting for you", "طلب موارد بشرية بانتظارك"),
        NotificationTypes.HrRequestNeedsInfo => ("HR asked you for more on your request", "طلبت الموارد البشرية معلومات إضافية عن طلبك"),
        NotificationTypes.HrRequestDecided => ("Your HR request was decided", "تم البت في طلبك للموارد البشرية"),
        NotificationTypes.HrRequestAnswered => ("Your HR request has a new answer", "هناك رد جديد على طلبك للموارد البشرية"),
        NotificationTypes.OfficeExpenseSubmitted => ("An office expense is waiting for review", "مصروف مكتب بانتظار المراجعة"),
        NotificationTypes.OfficeExpensePosted => ("An office expense was deducted from the custody", "تم خصم مصروف مكتب من العهدة"),
        NotificationTypes.OfficeFundingRequested => ("A custody funding request is waiting for review", "طلب تغذية عهدة بانتظار المراجعة"),
        NotificationTypes.OfficeFundsDisbursed => ("Custody funds were paid out: confirm you received them", "تم صرف تغذية العهدة: أكّد الاستلام"),
        NotificationTypes.OfficeLowBalance => ("The office custody balance is low", "رصيد عهدة المكتب منخفض"),
        NotificationTypes.OfficeCashDiscrepancy => ("A cash count does not match the custody balance", "جرد النقدية لا يطابق رصيد العهدة"),
        NotificationTypes.OfficeSpendRequested => ("An office repair or purchase request is waiting for review", "طلب صيانة أو شراء للمكتب بانتظار المراجعة"),
        NotificationTypes.LowBattery => ("A site engineer's phone battery is low", "بطارية هاتف مهندس الموقع منخفضة"),
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown notification type."),
    };
}
