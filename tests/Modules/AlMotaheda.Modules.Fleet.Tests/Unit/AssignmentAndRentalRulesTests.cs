using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features.Assignments;
using AlMotaheda.Modules.Fleet.Features.Register;

namespace AlMotaheda.Modules.Fleet.Tests.Unit;

/// <summary>A machine on one project at a time, stays that never overlap, and rental terms over time.</summary>
public sealed class AssignmentAndRentalRulesTests
{
    private static readonly DateOnly Today = new(2026, 1, 31);

    [Fact]
    public void A_machine_that_is_on_a_project_can_not_go_to_a_second_one()
    {
        // The open stay may be on this project or another one: either way the answer is "end it first".
        Assert.Equal(
            AssignDecision.AlreadyAssigned,
            AssignmentRules.CanAssign(EquipmentStatus.InService, hasOpenAssignment: true, lastEndDate: null, startDate: Today));
    }

    [Theory]
    [InlineData("UnderMaintenance")]
    [InlineData("Lost")]
    [InlineData("Retired")]
    public void Only_a_machine_in_service_can_go_to_a_project(string status)
    {
        Assert.Equal(AssignDecision.Unavailable, AssignmentRules.CanAssign(Enum.Parse<EquipmentStatus>(status), false, null, Today));
    }

    [Fact]
    public void A_new_stay_starts_on_or_after_the_day_the_last_one_ended()
    {
        var lastEnd = new DateOnly(2026, 1, 20);

        Assert.Equal(AssignDecision.OverlapsHistory, AssignmentRules.CanAssign(EquipmentStatus.InService, false, lastEnd, new DateOnly(2026, 1, 19)));
        // Leaving one site and reaching the next on the same day is allowed.
        Assert.Equal(AssignDecision.Allowed, AssignmentRules.CanAssign(EquipmentStatus.InService, false, lastEnd, lastEnd));
        Assert.Equal(AssignDecision.Allowed, AssignmentRules.CanAssign(EquipmentStatus.InService, false, lastEnd, Today));
        Assert.Equal(AssignDecision.Allowed, AssignmentRules.CanAssign(EquipmentStatus.InService, false, null, Today));
    }

    [Fact]
    public void A_stay_ends_between_its_start_date_and_today()
    {
        var start = new DateOnly(2026, 1, 5);

        Assert.True(AssignmentRules.CanEndOn(start, start, Today));
        Assert.True(AssignmentRules.CanEndOn(start, Today, Today));
        Assert.False(AssignmentRules.CanEndOn(start, new DateOnly(2026, 1, 4), Today));
        Assert.False(AssignmentRules.CanEndOn(start, Today.AddDays(1), Today));
    }

    [Fact]
    public void New_rental_terms_start_after_every_earlier_term_and_close_the_open_ones_the_day_before()
    {
        Assert.Null(RentalTimeline.EarliestNextStart([]));

        // Closed terms 1-31 Dec and open terms from 1 Jan: new terms can start on 2 Jan at the earliest.
        var terms = new List<(DateOnly Start, DateOnly? End)>
        {
            (new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31)),
            (new DateOnly(2026, 1, 1), null),
        };
        Assert.Equal(new DateOnly(2026, 1, 2), RentalTimeline.EarliestNextStart(terms));
        Assert.Equal(new DateOnly(2026, 1, 14), RentalTimeline.CloseBefore(new DateOnly(2026, 1, 15)));

        // Terms that ended after a later start still push the earliest start past their end.
        Assert.Equal(
            new DateOnly(2026, 2, 1),
            RentalTimeline.EarliestNextStart([(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31))]));
    }

    [Fact]
    public void Terms_that_stop_when_the_machine_becomes_owned_end_today_but_never_before_they_started()
    {
        Assert.Equal(Today, RentalTimeline.CloseOn(new DateOnly(2026, 1, 1), Today));
        Assert.Equal(new DateOnly(2026, 2, 10), RentalTimeline.CloseOn(new DateOnly(2026, 2, 10), Today));
    }
}
