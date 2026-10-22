using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features.Claims;
using AlMotaheda.Modules.Fleet.Features.Vehicles;
using AlMotaheda.Modules.Fleet.Storage;

namespace AlMotaheda.Modules.Fleet.Tests.Unit;

/// <summary>The vehicle custody arithmetic, the claim rules and the review flags, with no database.</summary>
public sealed class VehicleRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc);

    private static VehicleCustodyEntry Entry(VehicleCustodyKind kind, decimal amount, VehicleCustodyStatus status = VehicleCustodyStatus.Posted) =>
        new() { Kind = kind, Amount = amount, Status = status };

    [Fact]
    public void The_balance_is_fundings_minus_returns_minus_posted_claims_and_pending_claims_are_exposure_apart()
    {
        var figures = VehicleCustodyBalance.Compute(
        [
            Entry(VehicleCustodyKind.Funding, 2_000m),
            Entry(VehicleCustodyKind.Expense, 500m, VehicleCustodyStatus.Pending),
            Entry(VehicleCustodyKind.Expense, 300m),
            Entry(VehicleCustodyKind.Expense, 999m, VehicleCustodyStatus.Released),
            Entry(VehicleCustodyKind.Return, 100m),
            Entry(VehicleCustodyKind.AdjustmentOut, 50m),
        ]);

        Assert.Equal((2_000m, 100m, 300m, 500m, -50m, 1_550m, 1), (figures.Funded, figures.Returned, figures.SpentPosted, figures.Pending, figures.Adjusted, figures.Posted, figures.PendingCount));
        Assert.Equal(2_000m, VehicleCustodyBalance.Signed(Entry(VehicleCustodyKind.Funding, 2_000m)));
        Assert.Equal(-300m, VehicleCustodyBalance.Signed(Entry(VehicleCustodyKind.Expense, 300m)));
    }

    [Fact]
    public void A_fuel_claim_is_litres_times_price_rounded_once()
    {
        Assert.Equal(500.01m, VehicleExpenseRules.Amount(VehicleExpenseType.Fuel, 33.334m, 15.0m, null));
        Assert.Equal(120m, VehicleExpenseRules.Amount(VehicleExpenseType.Toll, null, null, 120m));
    }

    [Fact]
    public void The_submitter_and_the_driver_never_review_their_own_claim_and_an_escalated_one_needs_an_executive()
    {
        var claim = new VehicleExpense { CreatedBy = 11, DriverEmployeeId = 7, Amount = 500m, Status = VehicleExpenseStatus.Submitted };

        Assert.Equal(ClaimDecision.OwnRecord, VehicleExpenseRules.Review(claim, 11, null, false, 0));
        Assert.Equal(ClaimDecision.OwnRecord, VehicleExpenseRules.Review(claim, 22, 7, false, 0));
        Assert.Equal(ClaimDecision.Allowed, VehicleExpenseRules.Review(claim, 22, 9, false, 0));
        Assert.Equal(ClaimDecision.NeedsExecutive, VehicleExpenseRules.Review(claim, 22, 9, false, executiveAbove: 400m));
        Assert.Equal(ClaimDecision.Allowed, VehicleExpenseRules.Review(claim, 22, 9, true, executiveAbove: 400m));

        var escalated = new VehicleExpense { CreatedBy = 11, DriverEmployeeId = 7, Amount = 5m, Status = VehicleExpenseStatus.Escalated };
        Assert.Equal(ClaimDecision.NeedsExecutive, VehicleExpenseRules.Review(escalated, 22, 9, false, 0));
        Assert.Equal(ClaimDecision.Allowed, VehicleExpenseRules.Review(escalated, 22, 9, true, 0));

        var approved = new VehicleExpense { CreatedBy = 11, DriverEmployeeId = 7, Status = VehicleExpenseStatus.Approved };
        Assert.Equal(ClaimDecision.WrongStatus, VehicleExpenseRules.Review(approved, 22, 9, true, 0));
    }

    [Fact]
    public void A_claim_is_posted_once_and_a_reversal_needs_a_posted_claim_by_someone_else()
    {
        var claim = new VehicleExpense { CreatedBy = 11, DriverEmployeeId = 7, Status = VehicleExpenseStatus.Submitted };
        claim.Review(VehicleExpenseStatus.Approved, 22, Now, null);
        Assert.Equal(ClaimDecision.WrongStatus, VehicleExpenseRules.Reverse(claim, 22)); // approved but not posted yet (the handler posts in the same save)

        claim.PostingStatus = VehiclePostingStatus.Posted;
        Assert.Equal(ClaimDecision.OwnRecord, VehicleExpenseRules.Reverse(claim, 11));
        Assert.Equal(ClaimDecision.Allowed, VehicleExpenseRules.Reverse(claim, 22));
        Assert.Throws<InvalidOperationException>(() => claim.Review(VehicleExpenseStatus.Approved, 33, Now, null));
    }

    [Fact]
    public void Evidence_requested_goes_back_to_the_submitter_who_resubmits_with_a_new_round()
    {
        var claim = new VehicleExpense { CreatedBy = 11, DriverEmployeeId = 7, Status = VehicleExpenseStatus.Submitted };
        claim.Review(VehicleExpenseStatus.EvidenceRequested, 22, Now, "Odometer unreadable");

        Assert.Equal(ClaimDecision.NotSubmitter, VehicleExpenseRules.Resubmit(claim, 22));
        Assert.Equal(ClaimDecision.Allowed, VehicleExpenseRules.Resubmit(claim, 11));
        claim.Resubmit(11, Now);
        Assert.Equal((VehicleExpenseStatus.Submitted, 2, (string?)null), (claim.Status, claim.Round, claim.ReviewNote));
    }

    [Fact]
    public void Flags_point_at_duplicates_and_odd_odometers_and_block_nothing()
    {
        var flags = VehicleExpenseRules.Flags(
            odometerKm: 10_500m, expenseDate: new DateOnly(2026, 10, 20), lastOdometer: (10_000m, new DateOnly(2026, 10, 19)), implausibleKmPerDay: 1_000m,
            sameInvoiceNo: [3], sameReceipt: [4], sameAmountSameDay: []);

        Assert.Equal([VehicleExpenseFlagKind.DuplicateInvoiceNo, VehicleExpenseFlagKind.DuplicateReceipt], flags.Select(flag => flag.Kind));
        Assert.Equal([3, 4], flags.Select(flag => flag.RelatedExpenseId!.Value));

        var notIncreasing = VehicleExpenseRules.Flags(9_900m, new DateOnly(2026, 10, 20), (10_000m, new DateOnly(2026, 10, 19)), 1_000m, [], [], []);
        Assert.Equal([VehicleExpenseFlagKind.OdometerNotIncreasing], notIncreasing.Select(flag => flag.Kind));

        var implausible = VehicleExpenseRules.Flags(12_500m, new DateOnly(2026, 10, 20), (10_000m, new DateOnly(2026, 10, 19)), 1_000m, [], [], []);
        Assert.Equal([VehicleExpenseFlagKind.ImplausibleDistance], implausible.Select(flag => flag.Kind));

        var fine = VehicleExpenseRules.Flags(10_400m, new DateOnly(2026, 10, 20), (10_000m, new DateOnly(2026, 10, 19)), 1_000m, [], [], []);
        Assert.Empty(fine);
        Assert.Empty(VehicleExpenseRules.Flags(100m, new DateOnly(2026, 10, 20), (0m, null), 1_000m, [], [], [])); // a new vehicle: nothing to compare with

        // No reading given: the reviewer reads the odometer photo; only the duplicate checks run.
        Assert.Empty(VehicleExpenseRules.Flags(null, new DateOnly(2026, 10, 20), (10_000m, new DateOnly(2026, 10, 19)), 1_000m, [], [], []));
        Assert.Equal([VehicleExpenseFlagKind.DuplicateInvoiceNo], VehicleExpenseRules.Flags(null, new DateOnly(2026, 10, 20), (10_000m, null), 1_000m, [5], [], []).Select(flag => flag.Kind));
    }

    [Fact]
    public void A_violation_is_reviewed_once_by_someone_else_and_a_driver_assignment_covers_its_days()
    {
        var violation = new VehicleViolation { CreatedBy = 11 };
        Assert.True(violation.CanMoveTo(VehicleViolationStatus.Resolved));
        violation.Review(VehicleViolationStatus.Dismissed, 22, Now, "Not ours", null);
        Assert.False(violation.CanMoveTo(VehicleViolationStatus.Resolved));

        var stay = new VehicleDriverAssignment { StartDate = new DateOnly(2026, 1, 10), EndDate = new DateOnly(2026, 1, 20) };
        Assert.True(stay.Covers(new DateOnly(2026, 1, 10)));
        Assert.True(stay.Covers(new DateOnly(2026, 1, 20)));
        Assert.False(stay.Covers(new DateOnly(2026, 1, 21)));
        Assert.True(new VehicleDriverAssignment { StartDate = new DateOnly(2026, 1, 21) }.Covers(new DateOnly(2027, 1, 1)));
    }

    [Fact]
    public void Evidence_types_come_from_the_bytes_and_an_odometer_is_always_a_photo()
    {
        Assert.Equal("JPEG", FleetFileTypes.Detect([0xFF, 0xD8, 0xFF, 0xE0])!.Name);
        Assert.Equal("PDF", FleetFileTypes.Detect("%PDF-1.7"u8)!.Name);
        Assert.Null(FleetFileTypes.Detect("<html>"u8));
        Assert.False(FleetFileTypes.Allows(FleetAttachmentPurpose.Odometer, FleetFileTypes.Pdf));
        Assert.True(FleetFileTypes.Allows(FleetAttachmentPurpose.Invoice, FleetFileTypes.Pdf));
        Assert.True(FleetFileTypes.Allows(FleetAttachmentPurpose.Odometer, FleetFileTypes.Jpeg));
    }
}
