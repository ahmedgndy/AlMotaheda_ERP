using AlMotaheda.BuildingBlocks.Validation;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features.Claims;
using AlMotaheda.Modules.Projects.Contracts;

namespace AlMotaheda.Modules.Fleet.Tests.Unit;

/// <summary>The camera fuel claim's rules with no database: the reading, the capture moment, the price per litre and the vehicle's fuel.</summary>
public sealed class FuelClaimRulesTests
{
    private static readonly DateTime Now = new(2026, 10, 24, 9, 0, 0, DateTimeKind.Utc);

    private static FuelReading? Read(decimal? amount, AmountReadingRequest? request, out ValidationErrors errors)
    {
        errors = new ValidationErrors();
        return FuelClaimRules.Reading(amount, request, errors);
    }

    [Fact]
    public void An_amount_read_from_the_display_is_the_reading_with_its_confidence()
    {
        var reading = Read(500m, new AmountReadingRequest("Ocr", 500m, 40m, 12.5m, 0.97m, "mlkit-latin", "TOTAL 500.00"), out var errors);

        Assert.False(errors.HasErrors);
        Assert.Equal((500m, VehicleExpenseAmountSource.Ocr, 40m, 0.97m), (reading!.Amount, reading.Source, reading.OcrLiters, reading.Confidence));
        Assert.Empty(FuelClaimRules.ReadingFlags(reading));
    }

    [Theory]
    [InlineData(550.0, "Ocr", 500.0, 0.9)] // changed by hand but sent as read
    [InlineData(500.0, "Ocr", 500.0, null)] // no confidence
    [InlineData(500.0, "Ocr", null, 0.9)] // nothing read
    [InlineData(500.0, "Guess", 500.0, 0.9)]
    [InlineData(500.005, "ManualCorrection", null, null)] // not money
    public void A_reading_the_server_can_not_trust_as_sent_is_refused(double amount, string source, double? ocrAmount, double? confidence)
    {
        Read((decimal)amount, new AmountReadingRequest(source, (decimal?)ocrAmount, null, null, (decimal?)confidence, null, null), out var errors);

        Assert.True(errors.HasErrors);
    }

    [Fact]
    public void A_corrected_or_doubtful_reading_is_accepted_and_flagged_for_the_reviewer()
    {
        var corrected = Read(520m, new AmountReadingRequest("ManualCorrection", 52m, null, null, 0.41m, null, null), out var errors)!;
        Assert.False(errors.HasErrors);
        Assert.Equal([VehicleExpenseFlagKind.AmountManuallyEntered, VehicleExpenseFlagKind.LowReadingConfidence], FuelClaimRules.ReadingFlags(corrected).Select(flag => flag.Kind));

        // 40 L × 12.50 is 500, not 600: the display's own numbers disagree with the total.
        var inconsistent = Read(600m, new AmountReadingRequest("Ocr", 600m, 40m, 12.5m, 0.95m, null, null), out _)!;
        Assert.Equal([VehicleExpenseFlagKind.PumpReadingInconsistent], FuelClaimRules.ReadingFlags(inconsistent).Select(flag => flag.Kind));

        // Unread: nothing read and the amount typed.
        var unread = Read(300m, new AmountReadingRequest("ManualCorrection", null, null, null, null, null, null), out _)!;
        Assert.Contains("couldn't be read", Assert.Single(FuelClaimRules.ReadingFlags(unread)).Detail);
    }

    [Fact]
    public void The_price_per_litre_is_computed_in_decimals_and_rounded_once()
    {
        Assert.Equal(12.5m, FuelClaimRules.EffectiveUnitPrice(500m, 40m));
        Assert.Equal(13.8007m, FuelClaimRules.EffectiveUnitPrice(500m, 36.23m));
        Assert.Equal(16.6667m, FuelClaimRules.EffectiveUnitPrice(500m, 30m));
    }

    [Fact]
    public void The_capture_moment_is_kept_as_taken_never_ahead_of_the_server_nor_too_old()
    {
        var errors = new ValidationErrors();
        Assert.Equal(Now.AddHours(-6), FuelClaimRules.CapturedAt(Now.AddHours(-6).ToString("O"), Now, 60, errors));
        Assert.Equal(Now.AddMinutes(5), FuelClaimRules.CapturedAt("2026-10-24T11:05:00+02:00", Now, 60, errors));
        Assert.False(errors.HasErrors);

        foreach (var bad in new[] { null, "yesterday", Now.AddHours(1).ToString("O"), Now.AddDays(-61).ToString("O") })
        {
            var refused = new ValidationErrors();
            Assert.Null(FuelClaimRules.CapturedAt(bad, Now, 60, refused));
            Assert.True(refused.HasErrors);
        }
    }

    [Fact]
    public void The_vehicles_fuel_is_its_linked_item_or_the_only_matching_fuel_never_a_guess_between_grades()
    {
        static PriceItemListing Fuel(int id, string name, bool active = true, string unit = MeasurementUnits.Liter) =>
            new(new PriceItemRef(id, name, name, unit, "Fuel", active), null, null, null, null);
        var fuels = new[] { Fuel(1, "Diesel"), Fuel(2, "Petrol 92"), Fuel(3, "Petrol 95"), Fuel(4, "Diesel (old)", active: false) };

        Assert.Equal(1, FuelClaimRules.PickFuelItem(VehicleFuelType.Diesel, null, false, fuels)?.Id);
        Assert.Null(FuelClaimRules.PickFuelItem(VehicleFuelType.Petrol, null, false, fuels));
        Assert.Null(FuelClaimRules.PickFuelItem(VehicleFuelType.Gas, null, false, fuels));
        Assert.Equal(3, FuelClaimRules.PickFuelItem(VehicleFuelType.Petrol, fuels[2].Item, true, fuels)?.Id);
        Assert.Null(FuelClaimRules.PickFuelItem(VehicleFuelType.Diesel, fuels[3].Item, true, fuels));
        Assert.Null(FuelClaimRules.PickFuelItem(VehicleFuelType.Diesel, null, true, fuels));
    }
}
