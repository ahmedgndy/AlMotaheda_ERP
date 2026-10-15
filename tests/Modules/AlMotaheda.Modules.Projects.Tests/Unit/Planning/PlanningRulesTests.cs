using System.Globalization;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.DailyReports;
using AlMotaheda.Modules.Projects.Features.ReportRequests;

namespace AlMotaheda.Modules.Projects.Tests.Unit.Planning;

/// <summary>
/// The daily report workflow and the report request state machine, without a database. Enum values are passed by
/// name, because the module's enums are internal and a public test method can't take them as parameters.
/// </summary>
public sealed class PlanningRulesTests
{
    private const int Author = 10;
    private const int Reviewer = 20;
    private const int Requester = 30;
    private const int Recipient = 40;
    private const int Someone = 50;

    private static readonly DateOnly Today = new(2026, 10, 14);

    [Theory]
    [InlineData("2026-10-14", null)]
    [InlineData("2026-10-07", null)]
    [InlineData("2026-10-06", "The report date can be at most 7 days ago.")]
    [InlineData("2026-10-15", "The report date can't be in the future.")]
    public void A_daily_report_is_for_today_or_one_of_the_7_days_before(string date, string? problem)
    {
        Assert.Equal(problem, DailyReportRules.DateProblem(DateOnly.Parse(date, CultureInfo.InvariantCulture), Today));
    }

    [Theory]
    [InlineData("Submitted", Author, "Allowed")]
    [InlineData("Returned", Author, "Allowed")]
    [InlineData("Reviewed", Author, "WrongStatus")]
    [InlineData("Submitted", Reviewer, "NotAuthor")]
    public void Only_the_author_edits_and_only_until_the_report_is_reviewed(string status, int caller, string expected)
    {
        Assert.Equal(
            Enum.Parse<ReportDecision>(expected),
            DailyReportRules.Edit(Enum.Parse<DailyReportStatus>(status), Author, caller));
    }

    [Theory]
    [InlineData("Submitted", Reviewer, "Allowed")]
    [InlineData("Submitted", Author, "OwnReport")]
    [InlineData("Returned", Reviewer, "WrongStatus")]
    [InlineData("Reviewed", Reviewer, "WrongStatus")]
    public void Someone_else_reviews_and_only_a_submitted_report(string status, int caller, string expected)
    {
        Assert.Equal(
            Enum.Parse<ReportDecision>(expected),
            DailyReportRules.Review(Enum.Parse<DailyReportStatus>(status), Author, caller));
    }

    [Theory]
    // Start: the recipient, from Requested only.
    [InlineData("Start", "Requested", Recipient, false, "Allowed")]
    [InlineData("Start", "InProgress", Recipient, false, "WrongStatus")]
    [InlineData("Start", "Requested", Requester, true, "NotRecipient")]
    // Submit: the recipient, while open (Requested, InProgress, Returned).
    [InlineData("Submit", "Requested", Recipient, false, "Allowed")]
    [InlineData("Submit", "InProgress", Recipient, false, "Allowed")]
    [InlineData("Submit", "Returned", Recipient, false, "Allowed")]
    [InlineData("Submit", "Submitted", Recipient, false, "WrongStatus")]
    [InlineData("Submit", "Completed", Recipient, false, "WrongStatus")]
    [InlineData("Submit", "Requested", Someone, true, "NotRecipient")]
    // Complete and return: the requester or an Executive, from Submitted only.
    [InlineData("Complete", "Submitted", Requester, false, "Allowed")]
    [InlineData("Complete", "Submitted", Someone, true, "Allowed")]
    [InlineData("Complete", "Submitted", Recipient, false, "NotRequesterOrExecutive")]
    [InlineData("Complete", "InProgress", Requester, false, "WrongStatus")]
    [InlineData("Return", "Submitted", Requester, false, "Allowed")]
    [InlineData("Return", "Completed", Requester, false, "WrongStatus")]
    [InlineData("Return", "Submitted", Someone, false, "NotRequesterOrExecutive")]
    // Cancel: the requester only, while open.
    [InlineData("Cancel", "Requested", Requester, false, "Allowed")]
    [InlineData("Cancel", "Returned", Requester, false, "Allowed")]
    [InlineData("Cancel", "Submitted", Requester, false, "WrongStatus")]
    [InlineData("Cancel", "Cancelled", Requester, false, "WrongStatus")]
    [InlineData("Cancel", "Requested", Someone, true, "NotRequester")]
    [InlineData("Cancel", "Requested", Recipient, false, "NotRequester")]
    public void Every_move_of_a_report_request_has_one_actor_and_its_starting_statuses(
        string action, string status, int caller, bool callerIsExecutive, string expected)
    {
        var decision = ReportRequestFlow.Decide(
            Enum.Parse<RequestAction>(action), Enum.Parse<ReportRequestStatus>(status), Requester, Recipient, caller, callerIsExecutive);

        Assert.Equal(Enum.Parse<RequestDecision>(expected), decision);
    }

    [Theory]
    [InlineData("Start", "InProgress")]
    [InlineData("Submit", "Submitted")]
    [InlineData("Complete", "Completed")]
    [InlineData("Return", "Returned")]
    [InlineData("Cancel", "Cancelled")]
    public void Each_move_leads_to_its_status(string action, string target)
    {
        Assert.Equal(Enum.Parse<ReportRequestStatus>(target), ReportRequestFlow.Target(Enum.Parse<RequestAction>(action)));
    }

    [Fact]
    public void A_request_is_late_only_while_open_after_its_due_date()
    {
        var yesterday = Today.AddDays(-1);

        Assert.True(ReportRequestFlow.IsOverdue(ReportRequestStatus.InProgress, yesterday, Today));
        Assert.False(ReportRequestFlow.IsOverdue(ReportRequestStatus.InProgress, Today, Today));
        Assert.False(ReportRequestFlow.IsOverdue(ReportRequestStatus.Submitted, yesterday, Today));
        Assert.False(ReportRequestFlow.IsOverdue(ReportRequestStatus.Requested, null, Today));
    }

    [Theory]
    [InlineData("executive", true)]
    [InlineData("manager", true)]
    [InlineData("accountant", false)]
    [InlineData("engineer", false)]
    public void Executives_and_managers_see_every_request_of_a_project_they_can_open(string role, bool seesAll)
    {
        Assert.Equal(seesAll, ReportRequestFlow.SeesEveryRequest(role));
    }
}
