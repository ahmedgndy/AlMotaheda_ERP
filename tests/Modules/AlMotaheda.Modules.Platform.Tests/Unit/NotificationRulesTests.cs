using AlMotaheda.BuildingBlocks.Data;
using AlMotaheda.Modules.Platform.Notifications;

namespace AlMotaheda.Modules.Platform.Tests.Unit;

/// <summary>Who hears about which status change, from the changed record alone.</summary>
public sealed class NotificationRulesTests
{
    private const int Recorder = 11;
    private const int Reviewer = 22;

    private static EntityChange Change(
        string entity, string action, string? before, string? after, params (string Field, int UserId)[] people) =>
        new("projects", entity, "5", action, 3, before, after, people.ToDictionary(person => person.Field, person => person.UserId), ["Status"]);

    [Theory]
    [InlineData("Approved", NotificationTypes.RecordApproved, "تم اعتماد المصروف أو التوريد", "Your expense or supply was approved")]
    [InlineData("Returned", NotificationTypes.RecordReturned, "تمت إعادة المصروف أو التوريد للتعديل", "Your expense or supply was returned for changes")]
    [InlineData("Rejected", NotificationTypes.RecordRejected, "تم رفض المصروف أو التوريد", "Your expense or supply was rejected")]
    public void The_recorder_hears_the_review_of_an_expense(string status, string type, string titleAr, string titleEn)
    {
        var change = Change("ProjectExpense", "Modified", "Recorded", status, ("CreatedBy", Recorder), ("ReviewedBy", Reviewer));

        var draft = Assert.Single(NotificationRules.For(change, Reviewer));

        Assert.Equal(new NotificationDraft(Recorder, type, titleAr, titleEn), draft);
    }

    [Theory]
    [InlineData("ApprovedOnce", NotificationTypes.RecordApproved, "تم اعتماد استثناء السعر")]
    [InlineData("ApprovedWithPriceChange", NotificationTypes.RecordApproved, "تم اعتماد استثناء السعر")]
    [InlineData("Rejected", NotificationTypes.RecordRejected, "تم رفض استثناء السعر")]
    public void The_submitter_hears_the_executive_decision_on_a_price_exception(string status, string type, string titleAr)
    {
        var change = Change("PriceException", "Modified", "Pending", status, ("SubmittedBy", Recorder), ("DecidedBy", Reviewer));

        var draft = Assert.Single(NotificationRules.For(change, Reviewer));

        Assert.Equal((Recorder, type, titleAr), (draft.RecipientUserId, draft.Type, draft.TitleAr));
        Assert.Empty(NotificationRules.For(Change("PriceException", "Modified", "Pending", "Withdrawn", ("SubmittedBy", Recorder)), Recorder));
    }

    [Fact]
    public void A_labor_sheet_notifies_the_person_in_recorded_by()
    {
        var change = Change("DailySheet", "Modified", "Submitted", "Returned", ("RecordedBy", Recorder), ("ReviewedBy", Reviewer));

        var draft = Assert.Single(NotificationRules.For(change, Reviewer));

        Assert.Equal(Recorder, draft.RecipientUserId);
        Assert.Equal(NotificationTypes.RecordReturned, draft.Type);
    }

    [Fact]
    public void Nobody_is_told_about_their_own_action()
    {
        // An Executive who recorded a budget and approves it: nothing to tell.
        var change = Change("Budget", "Modified", "Draft", "Approved", ("CreatedBy", Recorder), ("ApprovedBy", Recorder));

        Assert.Empty(NotificationRules.For(change, Recorder));
    }

    [Fact]
    public void Only_a_real_status_change_notifies()
    {
        // An edit that keeps the status (fix and resubmit of a recorded expense) tells nobody.
        var edited = Change("ProjectExpense", "Modified", "Recorded", "Recorded", ("CreatedBy", Recorder));
        var unknownEntity = Change("Supplier", "Modified", "Active", "Inactive", ("CreatedBy", Recorder));

        Assert.Empty(NotificationRules.For(edited, Reviewer));
        Assert.Empty(NotificationRules.For(unknownEntity, Reviewer));
    }

    [Fact]
    public void A_report_request_tells_its_recipient_when_added_and_its_requester_when_submitted()
    {
        const int requester = 31;
        const int recipient = 32;
        (string, int)[] people = [("RequesterUserId", requester), ("RecipientUserId", recipient)];

        var added = Assert.Single(NotificationRules.For(Change("ReportRequest", "Added", null, "Requested", people), requester));
        var submitted = Assert.Single(NotificationRules.For(Change("ReportRequest", "Modified", "InProgress", "Submitted", people), recipient));
        var cancelled = Assert.Single(NotificationRules.For(Change("ReportRequest", "Modified", "Requested", "Cancelled", people), requester));

        Assert.Equal((recipient, NotificationTypes.ReportRequested), (added.RecipientUserId, added.Type));
        Assert.Equal((requester, NotificationTypes.ReportSubmitted), (submitted.RecipientUserId, submitted.Type));
        Assert.Equal((recipient, NotificationTypes.ReportCancelled), (cancelled.RecipientUserId, cancelled.Type));
    }

    [Fact]
    public void A_record_without_the_recipient_field_tells_nobody()
    {
        var change = Change("ProjectExpense", "Modified", "Recorded", "Approved");

        Assert.Empty(NotificationRules.For(change, Reviewer));
    }

    [Theory]
    [InlineData("LocationUnavailableAlert", NotificationTypes.LocationUnavailable, NotificationTypes.LocationAvailable)]
    [InlineData("OutsideSiteAlert", NotificationTypes.OutsideSite, NotificationTypes.BackInsideSite)]
    public void A_location_alert_tells_its_recipient_when_it_opens_and_when_it_is_over_but_not_when_sharing_is_turned_off(
        string entity, string opened, string recovered)
    {
        const int engineer = 33;
        (string, int)[] people = [("EngineerUserId", engineer), ("RecipientUserId", Reviewer)];

        var open = Assert.Single(NotificationRules.For(Change(entity, "Added", null, "Open", people), null));
        var over = Assert.Single(NotificationRules.For(Change(entity, "Modified", "Open", "Recovered", people), engineer));

        Assert.Equal((Reviewer, opened), (open.RecipientUserId, open.Type));
        Assert.Equal((Reviewer, recovered), (over.RecipientUserId, over.Type));
        Assert.Empty(NotificationRules.For(Change(entity, "Modified", "Open", "Ended", people), engineer));
    }

    [Theory]
    [InlineData("PriceAboveRangeAlert", NotificationTypes.PriceAboveRange, "تم تسجيل شراء بسعر أعلى من السعر المرجعي")]
    [InlineData("PriceExceptionAlert", NotificationTypes.PriceAboveRange, "تم تسجيل شراء بسعر أعلى من السعر المرجعي")]
    [InlineData("CostRecordedAlert", NotificationTypes.CostRecorded, "تكلفة موقع جديدة بانتظار المراجعة")]
    public void A_cost_alert_tells_its_recipient_once_when_it_is_added(string entity, string type, string titleAr)
    {
        const int engineer = 34;
        (string, int)[] people = [("RecipientUserId", Reviewer)];

        var told = Assert.Single(NotificationRules.For(Change(entity, "Added", null, null, people), engineer));

        Assert.Equal((Reviewer, type, titleAr), (told.RecipientUserId, told.Type, told.TitleAr));
        Assert.Empty(NotificationRules.For(Change(entity, "Added", null, null, people), Reviewer));
    }

    [Theory]
    [InlineData("Approved", NotificationTypes.RecordApproved)]
    [InlineData("Rejected", NotificationTypes.RecordRejected)]
    [InlineData("Paid", NotificationTypes.PaymentPaid)]
    [InlineData("Failed", NotificationTypes.PaymentFailed)]
    public void Whoever_asked_for_a_payment_hears_its_decision_and_outcome(string status, string type)
    {
        var change = Change("PaymentRequest", "Modified", "Submitted", status, ("CreatedBy", Recorder), ("DecidedBy", Reviewer));

        var told = Assert.Single(NotificationRules.For(change, Reviewer));

        Assert.Equal((Recorder, type), (told.RecipientUserId, told.Type));
    }

    [Fact]
    public void A_custody_transfer_tells_the_executives_when_asked_the_holder_when_disbursed_and_the_requester_of_the_rest()
    {
        const int requester = 51;
        const int accountant = 52;
        var asked = Assert.Single(NotificationRules.For(Change("CustodyTransferAlert", "Added", null, null, ("RecipientUserId", Reviewer)), requester));
        Assert.Equal((Reviewer, NotificationTypes.TransferRequested, "تحويل عهدة بانتظار قرارك"), (asked.RecipientUserId, asked.Type, asked.TitleAr));

        var approved = Assert.Single(NotificationRules.For(Change("CustodyTransfer", "Modified", "Requested", "Approved", ("RequestedBy", requester), ("DecidedBy", Reviewer)), Reviewer));
        Assert.Equal((requester, NotificationTypes.RecordApproved, "تم اعتماد تحويل العهدة"), (approved.RecipientUserId, approved.Type, approved.TitleAr));

        var received = NotificationRules.For(Change("CustodyTransfer", "Modified", "Disbursed", "Received", ("RequestedBy", requester), ("DisbursedBy", accountant), ("ReceivedBy", 53)), 53);
        Assert.Equal([requester, accountant], received.Select(draft => draft.RecipientUserId));
        Assert.All(received, draft => Assert.Equal(NotificationTypes.TransferReceived, draft.Type));

        // The requester who also disbursed is told once; a disbursement itself tells nobody but the alert rows.
        Assert.Single(NotificationRules.For(Change("CustodyTransfer", "Modified", "Disbursed", "Received", ("RequestedBy", requester), ("DisbursedBy", requester)), 53));
        Assert.Empty(NotificationRules.For(Change("CustodyTransfer", "Modified", "Approved", "Disbursed", ("RequestedBy", requester)), accountant));
        Assert.Equal(NotificationTypes.RecordReversed, Assert.Single(NotificationRules.For(Change("CustodyTransfer", "Modified", "Received", "Reversed", ("RequestedBy", requester)), Reviewer)).Type);
    }

    [Theory]
    [InlineData("Approved", NotificationTypes.RecordApproved, "تم اعتماد مطالبة مصروف المركبة")]
    [InlineData("Rejected", NotificationTypes.RecordRejected, "تم رفض مطالبة مصروف المركبة")]
    [InlineData("EvidenceRequested", NotificationTypes.ClaimEvidenceRequested, "طُلبت مستندات إضافية لمطالبتك")]
    [InlineData("Escalated", NotificationTypes.ClaimEscalated, "تمت إحالة مطالبتك إلى الإدارة العليا")]
    public void The_driver_who_submitted_a_vehicle_claim_hears_every_review_outcome(string status, string type, string titleAr)
    {
        var change = Change("VehicleExpense", "Modified", "Submitted", status, ("CreatedBy", Recorder), ("ReviewedBy", Reviewer));

        var draft = Assert.Single(NotificationRules.For(change, Reviewer));

        Assert.Equal((Recorder, type, titleAr), (draft.RecipientUserId, draft.Type, draft.TitleAr));
    }

    [Fact]
    public void A_submitted_vehicle_claim_tells_the_accountants_and_executives_through_its_alert_rows_never_the_driver()
    {
        var alert = Assert.Single(NotificationRules.For(Change("VehicleExpenseAlert", "Added", null, null, ("RecipientUserId", Reviewer)), Recorder));
        Assert.Equal((Reviewer, NotificationTypes.ClaimSubmitted, "مطالبة مصروف مركبة بانتظار المراجعة"), (alert.RecipientUserId, alert.Type, alert.TitleAr));
        Assert.Empty(NotificationRules.For(Change("VehicleExpenseAlert", "Added", null, null, ("RecipientUserId", Recorder)), Recorder));
        Assert.Empty(NotificationRules.For(Change("VehicleExpense", "Added", null, "Submitted", ("CreatedBy", Recorder)), Recorder));
    }

    [Fact]
    public void A_violation_review_and_an_hr_referral_closure_go_back_to_whoever_recorded_or_referred()
    {
        var reviewed = Assert.Single(NotificationRules.For(Change("VehicleViolation", "Modified", "Recorded", "Resolved", ("CreatedBy", Recorder)), Reviewer));
        Assert.Equal((Recorder, NotificationTypes.RecordReviewed), (reviewed.RecipientUserId, reviewed.Type));

        var closed = Assert.Single(NotificationRules.For(Change("HrReferral", "Modified", "UnderInvestigation", "Closed", ("ReferredBy", Recorder), ("ClosedBy", Reviewer)), Reviewer));
        Assert.Equal((Recorder, NotificationTypes.HrReferralClosed, "تم إغلاق إحالة الموارد البشرية التي أحلتها"), (closed.RecipientUserId, closed.Type, closed.TitleAr));
        Assert.Empty(NotificationRules.For(Change("HrReferral", "Added", null, "Open", ("ReferredBy", Recorder)), Recorder));
    }

    [Fact]
    public void A_custody_adjustment_tells_the_holder_a_dispute_the_reviewer_and_the_resolution_the_holder()
    {
        const int holder = 61;
        var posted = Assert.Single(NotificationRules.For(Change("CustodyAdjustment", "Added", null, "Posted", ("HolderUserId", holder), ("CreatedBy", Reviewer)), Reviewer));
        Assert.Equal((holder, NotificationTypes.AdjustmentPosted), (posted.RecipientUserId, posted.Type));

        var disputed = Assert.Single(NotificationRules.For(Change("CustodyAdjustment", "Modified", "Posted", "Disputed", ("HolderUserId", holder), ("CreatedBy", Reviewer)), holder));
        Assert.Equal((Reviewer, NotificationTypes.AdjustmentDisputed), (disputed.RecipientUserId, disputed.Type));

        foreach (var outcome in new[] { "Upheld", "Reversed" })
        {
            var resolved = Assert.Single(NotificationRules.For(Change("CustodyAdjustment", "Modified", "Disputed", outcome, ("HolderUserId", holder), ("CreatedBy", Reviewer), ("ResolvedBy", Reviewer)), Reviewer));
            Assert.Equal((holder, NotificationTypes.AdjustmentResolved), (resolved.RecipientUserId, resolved.Type));
        }

        // A vehicle custody has no holder: nobody to tell, nothing invented.
        Assert.Empty(NotificationRules.For(Change("CustodyAdjustment", "Added", null, "Posted", ("CreatedBy", Reviewer)), Reviewer));
    }

    [Fact]
    public void An_hr_request_reaches_hr_through_its_alert_rows_and_the_employee_hears_questions_answers_and_the_decision()
    {
        const int employee = 71;
        var alert = Assert.Single(NotificationRules.For(Change("HrRequestAlert", "Added", null, null, ("RecipientUserId", Reviewer)), employee));
        Assert.Equal((Reviewer, NotificationTypes.HrRequestSubmitted, "طلب موارد بشرية بانتظارك"), (alert.RecipientUserId, alert.Type, alert.TitleAr));
        Assert.Empty(NotificationRules.For(Change("HrRequest", "Added", null, "Submitted", ("SubmittedBy", employee)), employee));

        var asked = Assert.Single(NotificationRules.For(Change("HrRequest", "Modified", "UnderReview", "NeedsInfo", ("SubmittedBy", employee), ("ReviewedBy", Reviewer)), Reviewer));
        Assert.Equal((employee, NotificationTypes.HrRequestNeedsInfo), (asked.RecipientUserId, asked.Type));
        foreach (var outcome in new[] { "Approved", "Rejected", "Closed" })
        {
            var decided = Assert.Single(NotificationRules.For(Change("HrRequest", "Modified", "UnderReview", outcome, ("SubmittedBy", employee), ("DecidedBy", Reviewer)), Reviewer));
            Assert.Equal((employee, NotificationTypes.HrRequestDecided), (decided.RecipientUserId, decided.Type));
        }

        // The employee answering HR's question moves the request back to Submitted: HR hears through a fresh alert row, not a status rule.
        Assert.Empty(NotificationRules.For(Change("HrRequest", "Modified", "NeedsInfo", "Submitted", ("SubmittedBy", employee)), employee));
    }

    [Fact]
    public void A_price_range_request_is_answered_to_its_requester()
    {
        var change = Change("PriceRange", "Modified", "Pending", "Approved", ("RequestedBy", Recorder), ("DecidedBy", Reviewer));

        Assert.Equal(Recorder, Assert.Single(NotificationRules.For(change, Reviewer)).RecipientUserId);
    }

    [Fact]
    public void A_weekly_settlement_alert_row_tells_its_recipient_and_the_decision_tells_the_recorder_and_the_holder()
    {
        var alert = Change("SettlementAlert", "Added", null, null, ("RecipientUserId", Reviewer));
        var draft = Assert.Single(NotificationRules.For(alert, Recorder));
        Assert.Equal((Reviewer, NotificationTypes.PayrollSettled), (draft.RecipientUserId, draft.Type));

        const int holder = 33;
        var confirmed = Change("PayrollSettlement", "Modified", "Pending", "Confirmed", ("RecordedBy", Recorder), ("PaidByUserId", holder), ("ReviewedBy", Reviewer));
        Assert.Equal([Recorder, holder], NotificationRules.For(confirmed, Reviewer).Select(item => item.RecipientUserId).Order());
        Assert.All(NotificationRules.For(confirmed, Reviewer), item => Assert.Equal(NotificationTypes.RecordApproved, item.Type));

        var reversed = Change("PayrollSettlement", "Modified", "Confirmed", "Reversed", ("RecordedBy", Recorder), ("PaidByUserId", Recorder));
        var once = Assert.Single(NotificationRules.For(reversed, Reviewer));
        Assert.Equal(("RecordReversed", "Your weekly labor settlement was reversed"), (once.Type, once.TitleEn));
    }

    [Fact]
    public void The_custody_holder_hears_of_movements_others_record_but_not_of_their_own()
    {
        const int holder = 44;
        var funded = Change("CustodyEntry", "Added", null, "Posted", ("HolderUserId", holder), ("CreatedBy", Reviewer));
        Assert.Equal(NotificationTypes.CustodyChanged, Assert.Single(NotificationRules.For(funded, Reviewer)).Type);

        var ownPayroll = Change("CustodyEntry", "Added", null, "Pending", ("HolderUserId", holder), ("CreatedBy", holder));
        Assert.Empty(NotificationRules.For(ownPayroll, holder));
    }

    [Fact]
    public void A_subcontract_variation_decision_tells_whoever_asked()
    {
        var approved = Change("ContractVariation", "Modified", "Pending", "Approved", ("RequestedBy", Recorder), ("DecidedBy", Reviewer));

        var draft = Assert.Single(NotificationRules.For(approved, Reviewer));

        Assert.Equal((Recorder, NotificationTypes.RecordApproved, "تم اعتماد تعديل قيمة عقد الباطن"), (draft.RecipientUserId, draft.Type, draft.TitleAr));
    [Fact]
    public void A_low_battery_alert_tells_its_recipient_once_and_its_recovery_only_shows_on_the_map()
    {
        const int engineer = 33;
        (string, int)[] people = [("EngineerUserId", engineer), ("RecipientUserId", Reviewer)];

        var open = Assert.Single(NotificationRules.For(Change("LowBatteryAlert", "Added", null, "Open", people), null));

        Assert.Equal((Reviewer, NotificationTypes.LowBattery), (open.RecipientUserId, open.Type));
        Assert.Equal("A site engineer's phone battery is low", open.TitleEn);
        Assert.Empty(NotificationRules.For(Change("LowBatteryAlert", "Modified", "Open", "Recovered", people), engineer));
    }
}
