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
}
