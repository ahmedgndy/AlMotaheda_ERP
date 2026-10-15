using AlMotaheda.Modules.Workforce.Common;
using AlMotaheda.Modules.Workforce.Data;
using AlMotaheda.Modules.Workforce.Features.Sheets;
using AlMotaheda.Modules.Workforce.Features.Workers;

namespace AlMotaheda.Modules.Workforce.Tests.Unit;

/// <summary>The sheet workflow and the sheet form rules, with no database.</summary>
public sealed class SheetRulesTests
{
    private const int Engineer = 5;
    private const int Reviewer = 9;

    // The statuses and decisions are internal, so the theories take their names (a public test method can't take internal types).
    [Theory]
    [InlineData("Submitted", Reviewer, "Allowed")]
    [InlineData("Submitted", Engineer, "OwnRecord")]
    [InlineData("Draft", Reviewer, "WrongStatus")]
    [InlineData("Returned", Reviewer, "WrongStatus")]
    [InlineData("Approved", Reviewer, "WrongStatus")]
    [InlineData("Rejected", Reviewer, "WrongStatus")]
    public void Only_someone_else_reviews_and_only_a_submitted_sheet(string status, int caller, string expected)
    {
        Assert.Equal(Enum.Parse<SheetDecision>(expected), SheetRules.Review(Enum.Parse<SheetStatus>(status), Engineer, caller));
    }

    [Theory]
    [InlineData("Draft", Engineer, "Allowed")]
    [InlineData("Returned", Engineer, "Allowed")]
    [InlineData("Submitted", Engineer, "WrongStatus")]
    [InlineData("Approved", Engineer, "WrongStatus")]
    [InlineData("Draft", Reviewer, "NotRecorder")]
    public void Only_the_recorder_edits_and_only_a_draft_or_returned_sheet(string status, int caller, string expected)
    {
        Assert.Equal(Enum.Parse<SheetDecision>(expected), SheetRules.Edit(Enum.Parse<SheetStatus>(status), Engineer, caller));
    }

    [Theory]
    [InlineData("Draft", Engineer, false, true, "Allowed")]
    [InlineData("Returned", Engineer, false, true, "Allowed")]
    [InlineData("Submitted", Engineer, false, true, "NotRecorder")]
    [InlineData("Approved", Engineer, false, true, "NotRecorder")]
    [InlineData("Draft", Reviewer, false, true, "NotRecorder")]
    [InlineData("Submitted", Reviewer, true, false, "Allowed")]
    [InlineData("Submitted", Engineer, true, true, "Allowed")]
    [InlineData("Approved", Reviewer, true, false, "Allowed")]
    [InlineData("Rejected", Reviewer, true, false, "WrongStatus")]
    public void Allocations_are_set_by_the_recorder_on_a_draft_or_returned_sheet_or_by_edit_or_approve_until_rejected(
        string status, int caller, bool canEditOrApprove, bool canCreate, string expected)
    {
        Assert.Equal(
            Enum.Parse<SheetDecision>(expected),
            SheetRules.Allocate(Enum.Parse<SheetStatus>(status), Engineer, caller, canEditOrApprove, canCreate));
    }

    [Fact]
    public void A_sheet_is_for_today_or_one_of_the_seven_days_before()
    {
        var today = new DateOnly(2026, 1, 15);

        Assert.True(SheetRules.WorkDateAllowed(today, today));
        Assert.True(SheetRules.WorkDateAllowed(today.AddDays(-7), today));
        Assert.False(SheetRules.WorkDateAllowed(today.AddDays(-8), today));
        Assert.False(SheetRules.WorkDateAllowed(today.AddDays(1), today));
    }

    [Fact]
    public void Only_submitted_sheets_are_pending_review_because_a_returned_sheet_waits_for_its_recorder()
    {
        Assert.Equal(
            [SheetStatus.Submitted],
            Enum.GetValues<SheetStatus>().Where(SheetRules.IsPending));
    }

    [Fact]
    public void The_sheet_form_reports_every_failing_field_with_its_path()
    {
        var errors = new ValidationErrors();
        var request = new FormRequest(
            WorkDate: "2026-01-01",
            Shift: "Evening",
            Notes: null,
            Submit: true,
            Lines:
            [
                new(WorkerId: 1, TradeId: null, Days: 0.75m, OvertimeHours: 13m, WorkType: "Cooking", Notes: null,
                    Adjustments: [new("Deduction", "Other", 50m, null), new("Bonus", "Food", -1m, null)],
                    Allocations: [new(ItemId: 4, Portion: 0.5m), new(ItemId: 4, Portion: 1.5m)]),
                new(WorkerId: 1, TradeId: 0, Days: 1m, OvertimeHours: 0.25m, WorkType: null, Notes: null, Adjustments: null, Allocations: null),
            ]);

        var form = SheetForm.Validate(request, today: new DateOnly(2026, 1, 15), checkDateWindow: true, errors);

        Assert.Null(form);
        Assert.Equal(
            [
                "lines[0].adjustments[0].reason",
                "lines[0].adjustments[1].amount",
                "lines[0].adjustments[1].kind",
                "lines[0].allocations[1].itemId",
                "lines[0].allocations[1].portion",
                "lines[0].days",
                "lines[0].overtimeHours",
                "lines[0].workType",
                "lines[1].overtimeHours",
                "lines[1].tradeId",
                "lines[1].workerId",
                "shift",
                "workDate",
            ],
            errors.ToDictionary().Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_valid_form_is_cleaned()
    {
        var errors = new ValidationErrors();
        var request = new FormRequest(
            WorkDate: "2026-01-14",
            Shift: "night",
            Notes: "  Pour of slab B  ",
            Submit: null,
            Lines:
            [
                new(WorkerId: 1, TradeId: null, Days: 0.5m, OvertimeHours: null, WorkType: "mep", Notes: "",
                    Adjustments: [new("allowance", "food", 50m, null)], Allocations: [new(ItemId: 4, Portion: 0.25m)]),
            ]);

        var form = SheetForm.Validate(request, today: new DateOnly(2026, 1, 15), checkDateWindow: true, errors);

        Assert.False(errors.HasErrors);
        Assert.NotNull(form);
        Assert.Equal(Shift.Night, form.Shift);
        Assert.Equal("Pour of slab B", form.Notes);
        Assert.False(form.Submit);
        var line = Assert.Single(form.Lines);
        Assert.Equal(0m, line.OvertimeHours);
        Assert.Equal("Mep", line.WorkType);
        Assert.Null(line.Notes);
        Assert.Equal(new AdjustmentForm(AdjustmentKind.Allowance, AdjustmentType.Food, 50m, null), Assert.Single(line.Adjustments));
        Assert.Equal(new PortionForm(4, 0.25m), Assert.Single(line.Allocations));
    }

    [Theory]
    [InlineData("29304120137073", "**********7073")]
    [InlineData(null, null)]
    public void National_ids_are_masked_except_the_last_four_digits(string? nationalId, string? expected)
    {
        Assert.Equal(expected, NationalIds.Mask(nationalId));
        Assert.Equal(nationalId, NationalIds.Show(nationalId, full: true));
    }

    private sealed record FormRequest(string? WorkDate, string? Shift, string? Notes, bool? Submit, IReadOnlyList<LaborLineInput>? Lines) : ISheetFormRequest;
}
