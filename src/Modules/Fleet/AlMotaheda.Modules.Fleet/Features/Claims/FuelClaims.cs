using System.Globalization;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Data.Configurations;
using AlMotaheda.Modules.Fleet.Features.Vehicles;
using AlMotaheda.Modules.Fleet.Security;
using AlMotaheda.Modules.HR.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Features.Claims;

/// <summary>
/// What the phone read on the pump display, as it read it. <paramref name="Source"/> is <c>Ocr</c> (the amount is the
/// reading, unchanged) or <c>ManualCorrection</c> (the driver typed the amount because the display couldn't be read
/// reliably; <paramref name="OcrAmount"/> is what was read, if anything). <paramref name="Confidence"/> is 0–1.
/// <paramref name="PhotoNotes"/> is what the phone's photo checks found and the driver kept (shown to the reviewer).
/// </summary>
internal sealed record AmountReadingRequest(
    string? Source, decimal? OcrAmount, decimal? OcrLiters, decimal? OcrUnitPrice, decimal? Confidence, string? Engine, string? Text, string? PhotoNotes = null);

/// <summary>A validated reading: the claim's amount and how it was obtained.</summary>
internal sealed record FuelReading(
    decimal Amount, VehicleExpenseAmountSource Source, decimal? OcrAmount, decimal? OcrLiters, decimal? OcrUnitPrice, decimal? Confidence, string? Engine, string? Text,
    string? PhotoNotes = null);

/// <summary>
/// The Price Control comparison of a camera fuel claim. <paramref name="PriceCheck"/> is the verdict's result, or
/// <see cref="VehiclePriceChecks.Incomplete"/> when it couldn't be made (never a pass).
/// </summary>
internal sealed record FuelPricing(int? PriceItemId, PriceVerdict? Verdict, string PriceCheck, decimal? EffectiveUnitPrice, IReadOnlyList<ClaimFlag> Flags)
{
    /// <summary>Above the range under the ExecutiveApproval policy: the Executive decides the price.</summary>
    public bool Escalate => Verdict is { IsAboveRange: true, Gates: true };

    /// <summary>Above the range under the Block policy: refused (409 <c>price_above_range</c>).</summary>
    public bool Blocked => Verdict is { IsAboveRange: true, Blocked: true };
}

/// <summary>
/// The rules of a camera fuel claim (three photos: receipt, pump display, vehicle at the pump; the total read on the
/// phone), with no database access. The server never takes the phone's word for the price: it computes the price per
/// litre from the amount and the litres read, and compares it with the vehicle's fuel in Price Control.
/// </summary>
internal static class FuelClaimRules
{
    /// <summary>A reading below this confidence is shown to the reviewer.</summary>
    public const decimal LowConfidence = 0.80m;

    public const decimal MaxLiters = 5_000m;

    /// <summary>Fits a flag's detail with its prefix.</summary>
    public const int PhotoNotesMaxLength = 250;

    /// <summary>How far ahead of the server's clock a phone's capture moment may be.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(10);

    public static FuelReading? Reading(decimal? amount, AmountReadingRequest? request, ValidationErrors errors)
    {
        var total = NumberInput.RequiredMoney(amount, errors, "amount", "The total amount");
        if (request is null)
        {
            errors.Add("reading", "A fuel claim carries the reading of the pump display photo (source, and what was read).");
            return null;
        }

        var source = Input.RequiredName<VehicleExpenseAmountSource>(request.Source, errors, "reading.source");
        var ocrAmount = NumberInput.OptionalMoney(request.OcrAmount, errors, "reading.ocrAmount", "The amount read");
        decimal? liters = null;
        if (request.OcrLiters is { } readLiters)
        {
            liters = NumberInput.RequiredQuantity(readLiters, errors, "reading.ocrLiters", "The litres read");
            if (liters > MaxLiters)
            {
                errors.Add("reading.ocrLiters", $"The litres read must be at most {MaxLiters:0}.");
                liters = null;
            }
        }

        decimal? unitPrice = null;
        if (request.OcrUnitPrice is { } readPrice)
        {
            if (readPrice <= 0 || readPrice > NumberInput.MaxMoney || decimal.Round(readPrice, 4) != readPrice)
            {
                errors.Add("reading.ocrUnitPrice", "The price per litre read must be greater than zero with at most four decimals.");
            }
            else
            {
                unitPrice = readPrice;
            }
        }

        if (request.Confidence is { } confidence && confidence is < 0 or > 1)
        {
            errors.Add("reading.confidence", "The reading's confidence is between 0 and 1.");
        }

        if (source == VehicleExpenseAmountSource.Ocr)
        {
            // Read and unchanged: the amount is the reading. A corrected amount says so (ManualCorrection).
            if (request.Confidence is null)
            {
                errors.Add("reading.confidence", "An amount read from the display comes with the reading's confidence.");
            }

            if (ocrAmount is null)
            {
                errors.Add("reading.ocrAmount", "An amount read from the display is sent as read.");
            }
            else if (total is not null && ocrAmount != total)
            {
                errors.Add("reading.ocrAmount", "The amount differs from the reading: an amount changed by hand is sent with source ManualCorrection.");
            }
        }

        var engine = Clip(Input.Clean(request.Engine), VehicleExpenseConfiguration.OcrEngineMaxLength);
        var text = Clip(Input.Clean(request.Text), VehicleExpenseConfiguration.OcrTextMaxLength);
        var photoNotes = Clip(Input.Clean(request.PhotoNotes), PhotoNotesMaxLength);
        return errors.HasErrors
            ? null
            : new FuelReading(total!.Value, source!.Value, ocrAmount, liters, unitPrice, request.Confidence is { } kept ? decimal.Round(kept, 4) : null, engine, text, photoNotes);
    }

    /// <summary>The moment the driver photographed the fill: required, never ahead of the server, at most <paramref name="maxAgeDays"/> old.</summary>
    public static DateTime? CapturedAt(string? text, DateTime utcNow, int maxAgeDays, ValidationErrors errors)
    {
        if (Input.Clean(text) is not { } value)
        {
            errors.Add("capturedAt", "capturedAt (when the photos were taken on the phone) is required.");
            return null;
        }

        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var moment))
        {
            errors.Add("capturedAt", "capturedAt must be an ISO 8601 moment.");
            return null;
        }

        var utc = moment.UtcDateTime;
        if (utc > utcNow + ClockSkew)
        {
            errors.Add("capturedAt", "capturedAt can't be in the future. Check the phone's date and time.");
            return null;
        }

        if (utcNow - utc > TimeSpan.FromDays(maxAgeDays))
        {
            errors.Add("capturedAt", $"A claim is made within {maxAgeDays} days of the expense.");
            return null;
        }

        return utc;
    }

    /// <summary>Amount ÷ litres, to four decimals: what the Price Control check compares. Decimal arithmetic only.</summary>
    public static decimal EffectiveUnitPrice(decimal amount, decimal liters) => decimal.Round(amount / liters, 4, MidpointRounding.AwayFromZero);

    /// <summary>What the reviewer should look at about the reading itself. Information, never a block.</summary>
    public static IReadOnlyList<ClaimFlag> ReadingFlags(FuelReading reading)
    {
        var flags = new List<ClaimFlag>();
        if (reading.Source == VehicleExpenseAmountSource.ManualCorrection)
        {
            flags.Add(new ClaimFlag(VehicleExpenseFlagKind.AmountManuallyEntered, reading.OcrAmount is { } read
                ? $"The display was read as {read:0.00}; the driver corrected the amount to {reading.Amount:0.00}."
                : $"The display couldn't be read; the driver typed {reading.Amount:0.00}."));
        }

        if (reading.Confidence is { } confidence && confidence < LowConfidence)
        {
            flags.Add(new ClaimFlag(VehicleExpenseFlagKind.LowReadingConfidence, $"The phone read the display with {confidence * 100:0}% confidence."));
        }

        if (reading.PhotoNotes is { } notes)
        {
            flags.Add(new ClaimFlag(VehicleExpenseFlagKind.PhotoQuality, $"Photo checks on the phone: {notes}"));
        }

        if (reading.OcrLiters is { } liters && reading.OcrUnitPrice is { } price)
        {
            var product = decimal.Round(liters * price, 2, MidpointRounding.AwayFromZero);
            if (Math.Abs(product - reading.Amount) > Math.Max(0.5m, reading.Amount * 0.01m))
            {
                flags.Add(new ClaimFlag(VehicleExpenseFlagKind.PumpReadingInconsistent, $"{liters:0.###} L × {price:0.00} = {product:0.00}, not the amount {reading.Amount:0.00}."));
            }
        }

        return flags;
    }

    /// <summary>
    /// The vehicle's fuel in Price Control: the item linked to the vehicle when it is an active litre item; else the only
    /// active litre fuel whose English name starts with the vehicle's fuel type (Diesel); else none (several petrol grades
    /// and no link: the fleet manager links the grade on the vehicle).
    /// </summary>
    public static PriceItemRef? PickFuelItem(VehicleFuelType fuel, PriceItemRef? linked, bool hasLink, IReadOnlyList<PriceItemListing> fuels)
    {
        if (hasLink)
        {
            return linked is { IsActive: true, Unit: MeasurementUnits.Liter } ? linked : null;
        }

        var matches = fuels.Select(listing => listing.Item)
            .Where(item => item.IsActive && item.Unit == MeasurementUnits.Liter && item.NameEn.StartsWith(fuel.ToString(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    private static string? Clip(string? text, int maxLength) => text is null || text.Length <= maxLength ? text : text[..maxLength];
}

/// <summary>The Price Control side of a camera fuel claim.</summary>
internal static class FuelPrices
{
    public static async Task<PriceItemRef?> FuelItemAsync(IPriceControl priceControl, Vehicle vehicle, DateOnly date, CancellationToken cancellationToken)
    {
        if (vehicle.FuelPriceItemId is { } linkedId)
        {
            var items = await priceControl.FindItemsAsync([linkedId], cancellationToken);
            return FuelClaimRules.PickFuelItem(vehicle.FuelType, items.GetValueOrDefault(linkedId), hasLink: true, []);
        }

        return FuelClaimRules.PickFuelItem(vehicle.FuelType, null, hasLink: false, await priceControl.ListItemsAsync([VehicleExpenseEndpoints.FuelCategory], date, cancellationToken));
    }

    /// <summary>
    /// Compares amount ÷ litres with the vehicle's fuel on the expense date. Without litres, or without a fuel item, the
    /// check is Incomplete and says why: the reviewer sees the price is not validated.
    /// </summary>
    public static async Task<FuelPricing> PriceAsync(IPriceControl priceControl, Vehicle vehicle, DateOnly date, FuelReading reading, CancellationToken cancellationToken)
    {
        var item = await FuelItemAsync(priceControl, vehicle, date, cancellationToken);
        var effective = reading.OcrLiters is { } liters ? FuelClaimRules.EffectiveUnitPrice(reading.Amount, liters) : (decimal?)null;
        if (item is null)
        {
            return Incomplete(null, effective, $"No active Price Control fuel item is linked to this vehicle's fuel ({vehicle.FuelType}): the price per litre is not validated.");
        }

        if (effective is null)
        {
            return Incomplete(item.Id, null, "The litres couldn't be read from the pump display: the price per litre is not validated.");
        }

        var verdict = await priceControl.CheckAsync(new PriceCheckRequest(item.Id, null, date, MeasurementUnits.Liter, effective.Value, reading.OcrLiters!.Value), cancellationToken);
        if (verdict.Result == PriceVerdict.UnitMismatch)
        {
            return Incomplete(item.Id, effective, $"The fuel item is per {item.Unit}, not per litre: the price per litre is not validated.");
        }

        var flags = new List<ClaimFlag>();
        if (verdict.IsAboveRange)
        {
            flags.Add(new ClaimFlag(VehicleExpenseFlagKind.PriceAboveRange, $"{verdict.VariancePerUnit:0.00} per litre above the reference maximum {verdict.MaxPrice:0.00} ({verdict.VariancePercent:0.00}%)."));
        }
        else if (verdict.Result == PriceVerdict.NoReference)
        {
            flags.Add(new ClaimFlag(VehicleExpenseFlagKind.NoReferencePrice, "No reference price range applies to this fuel on the expense date."));
        }

        return new FuelPricing(item.Id, verdict, verdict.Result, effective, flags);
    }

    private static FuelPricing Incomplete(int? itemId, decimal? effective, string why) =>
        new(itemId, null, VehiclePriceChecks.Incomplete, effective, [new ClaimFlag(VehicleExpenseFlagKind.PriceNotValidated, why)]);
}

/// <summary>A fuel claim of the vehicle as the claim screen needs it: number and status (amounts only on the claim itself).</summary>
internal sealed record FuelClaimRef(int Id, string Number, string Status, DateTime SubmittedAt, DateTime? CapturedAt, bool Mine);

/// <summary>
/// Whether the caller may start a fuel claim for the vehicle now. <c>Outstanding</c> is the vehicle's fuel claim waiting for
/// review or returned for correction (anyone's: the rule is per vehicle); <c>Latest</c> is the caller's newest fuel claim.
/// </summary>
internal sealed record FuelClaimStateView(
    int VehicleId, string Plate, string FuelType, int? FuelItemId, string? FuelItemNameAr, string? FuelItemNameEn, bool IsDriver, bool CanClaim,
    FuelClaimRef? Outstanding, FuelClaimRef? Latest);

/// <summary><c>GET /api/vehicles/{id}/fuel-claim</c>: the vehicle's fuel and its outstanding fuel claim, for the driver's claim card.</summary>
internal static class FuelClaimState
{
    internal static async Task<Results<Ok<FuelClaimStateView>, ProblemHttpResult>> GetAsync(
        int vehicleId, HttpContext httpContext, FleetDbContext db, IEmployeeDirectory employees, IPriceControl priceControl, CompanyClock clock, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == vehicleId, cancellationToken);
        if (vehicle is null)
        {
            return FleetProblems.VehicleNotFound();
        }

        var me = await employees.FindByUserAsync(caller.UserId, cancellationToken);
        var isDriver = me is not null && await VehicleQueries.CurrentDriver(db, vehicleId).AnyAsync(assignment => assignment.DriverEmployeeId == me.Id, cancellationToken);
        var oversees = caller.CanFleet(PermissionAction.View) || caller.CanFuel(PermissionAction.Approve) || caller.SeesVehicleMoney;
        if (!isDriver && !oversees)
        {
            return FleetProblems.VehicleNotFound();
        }

        var userId = caller.UserId;
        var employeeId = me?.Id;
        var outstanding = await db.VehicleExpenses.AsNoTracking()
            .Where(claim => claim.VehicleId == vehicleId && claim.Type == VehicleExpenseType.Fuel && VehicleExpense.Outstanding.Contains(claim.Status))
            .OrderBy(claim => claim.Id)
            .Select(claim => new FuelClaimRef(claim.Id, claim.Number, claim.Status.ToString(), claim.SubmittedAt, claim.CapturedAt, claim.CreatedBy == userId || claim.DriverEmployeeId == employeeId))
            .FirstOrDefaultAsync(cancellationToken);
        var latest = await db.VehicleExpenses.AsNoTracking()
            .Where(claim => claim.VehicleId == vehicleId && claim.Type == VehicleExpenseType.Fuel && (claim.CreatedBy == userId || claim.DriverEmployeeId == employeeId))
            .OrderByDescending(claim => claim.Id)
            .Select(claim => new FuelClaimRef(claim.Id, claim.Number, claim.Status.ToString(), claim.SubmittedAt, claim.CapturedAt, true))
            .FirstOrDefaultAsync(cancellationToken);
        var item = await FuelPrices.FuelItemAsync(priceControl, vehicle, clock.Today, cancellationToken);
        var mayClaim = isDriver || caller.CanFleet(PermissionAction.Edit);
        return TypedResults.Ok(new FuelClaimStateView(
            vehicle.Id, vehicle.Plate, vehicle.FuelType.ToString(), item?.Id, item?.NameAr, item?.NameEn, isDriver, mayClaim && caller.CanFuel(PermissionAction.Create) && outstanding is null, outstanding, latest));
    }
}
