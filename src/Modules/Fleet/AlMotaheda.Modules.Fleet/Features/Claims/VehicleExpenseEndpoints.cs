using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Data.Configurations;
using AlMotaheda.Modules.Fleet.Features.Attachments;
using AlMotaheda.Modules.Fleet.Features.Vehicles;
using AlMotaheda.Modules.Fleet.Security;
using AlMotaheda.Modules.HR.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Fleet.Features.Claims;

/// <summary>
/// Vehicle expense claims: <c>GET /api/vehicle-expenses</c> and <c>/{id}</c> (fuel View: your own, every one with
/// See money or Approve), <c>POST /api/vehicle-expenses</c> (fuel Create: the vehicle's driver on that day, or a
/// fleet manager on their behalf), <c>POST /{id}/resubmit</c> (the submitter, after an evidence request) and
/// <c>GET /api/vehicles/{id}/fuel-claim</c> (the vehicle's fuel and its outstanding fuel claim). The custody account
/// is always the vehicle's: nobody chooses it, nobody claims against another vehicle's cash.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Fuel (the camera claim): three photos (the receipt, the pump display, the vehicle at the pump), the total read
///   from the display on the phone with its reading (or corrected by hand, said so), and the moment it was captured,
///   which gives the expense date. No fuel, price or station is typed: the fuel is the vehicle's, the price per litre is
///   amount ÷ litres computed here (422 <c>evidence_required</c> without the photos).</item>
///   <item>One fuel claim per vehicle not reviewed to the end (Submitted, Escalated, EvidenceRequested): another is
///   refused with 409 <c>fuel_claim_outstanding</c>, under the vehicle's lock and by a filtered unique index. A claim
///   returned for correction is resubmitted, never replaced.</item>
///   <item>Every other type: the amount, an odometer photo and an invoice or receipt photo; the odometer reading is optional.</item>
///   <item>Submitted: a Pending entry in the vehicle's custody (the exposure); the posted balance is unchanged until approval.</item>
///   <item>A fuel claim is compared with the vehicle's fuel in Price Control at the expense date when the litres were
///   read: above the range under Block → 409 <c>price_above_range</c>; under ExecutiveApproval → a case for the
///   Executive when the vehicle is on a project, else the claim is Escalated to the Executive directly. Without litres
///   or a fuel item the check is Incomplete, flagged, and never counts as passed.</item>
///   <item>Review flags (same invoice, same receipt, same amount and day, odometer not increasing, implausible distance)
///   are shown to the reviewer; none of them blocks.</item>
///   <item>The accountants and the Executives hear of a new claim (alert rows saved with it).</item>
/// </list>
/// </remarks>
internal static class VehicleExpenseEndpoints
{
    internal sealed record GpsPoint(decimal? Latitude, decimal? Longitude, string? CapturedAt);

    internal sealed record SubmitRequest(
        int? VehicleId,
        int? DriverEmployeeId,
        string? Type,
        string? ExpenseDate,
        decimal? OdometerKm,
        decimal? FuelQuantity,
        decimal? UnitPrice,
        decimal? Amount,
        string? VendorName,
        int? SupplierId,
        string? InvoiceNo,
        string? Notes,
        int? PriceItemId,
        string? PriceJustification,
        int? OdometerAttachmentId,
        int? InvoiceAttachmentId,
        IReadOnlyList<int>? SupportingAttachmentIds,
        GpsPoint? Gps,
        string? IdempotencyKey,
        string? CapturedAt = null,
        int? PumpDisplayAttachmentId = null,
        int? VehicleAtPumpAttachmentId = null,
        AmountReadingRequest? Reading = null);

    /// <summary>
    /// The correction of a claim returned for it: the photos asked for (added, the earlier ones kept) and, for a camera
    /// fuel claim, a new pump display photo may come with a new <paramref name="Amount"/> and <paramref name="Reading"/>.
    /// </summary>
    internal sealed record ResubmitRequest(
        string? Notes,
        int? OdometerAttachmentId,
        int? InvoiceAttachmentId,
        IReadOnlyList<int>? SupportingAttachmentIds,
        string? RowVersion,
        int? PumpDisplayAttachmentId = null,
        int? VehicleAtPumpAttachmentId = null,
        decimal? Amount = null,
        AmountReadingRequest? Reading = null);

    /// <summary>The reason a fuel price case carries: the driver no longer writes one.</summary>
    internal const string FuelPriceReason = "Price per litre computed by the server from the pump display photo (total ÷ litres).";

    internal sealed record ListResponse(IReadOnlyList<VehicleExpenseView> Items, int Page, int PageSize, int TotalCount, IReadOnlyDictionary<string, int> CountsByStatus);

    /// <summary>A fuel of the company price list, as a driver picks it: no price range (the server compares the price).</summary>
    internal sealed record FuelTypeView(int Id, string NameAr, string NameEn, string Unit);

    /// <summary>The price list category of fuels.</summary>
    internal const string FuelCategory = "Fuel";

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/vehicle-expenses", ListAsync)
            .RequirePermission(FleetPermissions.Fuel, PermissionAction.View)
            .WithName("ListVehicleExpenses")
            .WithSummary("Vehicle expense claims (vehicleId, status, driverEmployeeId, from, to; page, pageSize): yours, or every one with fuel See money or Approve");

        api.MapGet("/vehicle-expenses/{claimId:int}", GetAsync)
            .RequirePermission(FleetPermissions.Fuel, PermissionAction.View)
            .WithName("GetVehicleExpense")
            .WithSummary("One claim with its evidence, flags and review");

        api.MapGet("/fleet/fuel-types", FuelTypesAsync)
            .RequireAnyPermission((FleetPermissions.Fuel, PermissionAction.View), (FleetPermissions.Fuel, PermissionAction.Create))
            .WithName("ListFuelTypes")
            .WithSummary("The fuels a fuel claim names (the active litre items of the price list's Fuel category), without prices");

        api.MapPost("/vehicle-expenses", SubmitAsync)
            .RequirePermission(FleetPermissions.Fuel, PermissionAction.Create)
            .WithName("SubmitVehicleExpense")
            .WithSummary("Submit a claim against the vehicle's custody (the driver on that day, or fleet Edit on their behalf). Idempotent by idempotencyKey");

        api.MapPost("/vehicle-expenses/{claimId:int}/resubmit", ResubmitAsync)
            .RequirePermission(FleetPermissions.Fuel, PermissionAction.Create)
            .WithName("ResubmitVehicleExpense")
            .WithSummary("Add the evidence a reviewer asked for and submit again (the submitter only); earlier evidence is kept. A camera fuel claim may come with a new pump reading");

        api.MapGet("/vehicles/{vehicleId:int}/fuel-claim", FuelClaimState.GetAsync)
            .RequireAnyPermission((FleetPermissions.Fuel, PermissionAction.View), (FleetPermissions.Fuel, PermissionAction.Create))
            .WithName("GetVehicleFuelClaimState")
            .WithSummary("The vehicle's fuel (its Price Control item) and its fuel claim waiting for review or correction, if any: whether a new fuel claim can be made");
    }

    /// <summary>A driver names the fuel without seeing the price list: its ranges are control data the server applies.</summary>
    internal static async Task<Ok<List<FuelTypeView>>> FuelTypesAsync(IPriceControl priceControl, CompanyClock clock, CancellationToken cancellationToken)
    {
        var items = await priceControl.ListItemsAsync([FuelCategory], clock.Today, cancellationToken);
        return TypedResults.Ok(items
            .Where(listing => listing.Item.IsActive && listing.Item.Unit == MeasurementUnits.Liter)
            .Select(listing => new FuelTypeView(listing.Item.Id, listing.Item.NameAr, listing.Item.NameEn, listing.Item.Unit))
            .OrderBy(fuel => fuel.NameEn, StringComparer.Ordinal)
            .ToList());
    }

    internal static async Task<Results<Ok<ListResponse>, ValidationProblem, ProblemHttpResult>> ListAsync(
        HttpContext httpContext,
        FleetDbContext db,
        VehicleExpenseViews views,
        IEmployeeDirectory employees,
        CancellationToken cancellationToken,
        int? vehicleId = null,
        string? status = null,
        int? driverEmployeeId = null,
        string? from = null,
        string? to = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var wantedStatus = Input.OptionalName<VehicleExpenseStatus>(status, errors, "status");
        var fromDate = Input.OptionalDate(from, errors, "from", "from");
        var toDate = Input.OptionalDate(to, errors, "to", "to");
        if (fromDate is { } start && toDate is { } end && end < start)
        {
            errors.Add("to", "to must be on or after from.");
        }

        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var me = await employees.FindByUserAsync(caller.UserId, cancellationToken);
        var query = Query(db, caller, me?.Id, vehicleId, driverEmployeeId, fromDate, toDate);
        var counts = await query.GroupBy(claim => claim.Status).Select(group => new { group.Key, Count = group.Count() }).ToListAsync(cancellationToken);
        if (wantedStatus is { } wanted)
        {
            query = query.Where(claim => claim.Status == wanted);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(claim => claim.ExpenseDate).ThenByDescending(claim => claim.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Include(claim => claim.Vehicle).ThenInclude(vehicle => vehicle.Equipment).Include(claim => claim.Flags).ToListAsync(cancellationToken);
        return TypedResults.Ok(new ListResponse(await views.FromAsync(rows, caller, me?.Id, cancellationToken), page, pageSize, totalCount, counts.ToDictionary(entry => entry.Key.ToString(), entry => entry.Count)));
    }

    /// <summary>The claims the caller may see, in SQL: every one for a reviewer or with See money, else their own (submitted, or as the driver).</summary>
    internal static IQueryable<VehicleExpense> Query(FleetDbContext db, FleetCaller caller, int? callerEmployeeId, int? vehicleId, int? driverEmployeeId, DateOnly? from, DateOnly? to)
    {
        var query = db.VehicleExpenses.AsNoTracking();
        if (!caller.SeesVehicleMoney && !caller.CanFuel(PermissionAction.Approve))
        {
            var me = caller.UserId;
            query = query.Where(claim => claim.CreatedBy == me || (callerEmployeeId != null && claim.DriverEmployeeId == callerEmployeeId));
        }

        if (vehicleId is { } vehicle)
        {
            query = query.Where(claim => claim.VehicleId == vehicle);
        }

        if (driverEmployeeId is { } driver)
        {
            query = query.Where(claim => claim.DriverEmployeeId == driver);
        }

        if (from is { } start)
        {
            query = query.Where(claim => claim.ExpenseDate >= start);
        }

        if (to is { } end)
        {
            query = query.Where(claim => claim.ExpenseDate <= end);
        }

        return query;
    }

    internal static async Task<Results<Ok<VehicleExpenseView>, ProblemHttpResult>> GetAsync(
        int claimId, HttpContext httpContext, FleetDbContext db, VehicleExpenseViews views, IEmployeeDirectory employees, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var me = await employees.FindByUserAsync(caller.UserId, cancellationToken);
        if (!await Query(db, caller, me?.Id, null, null, null, null).AnyAsync(claim => claim.Id == claimId, cancellationToken))
        {
            return FleetProblems.ClaimNotFound();
        }

        return TypedResults.Ok((await views.LoadAsync(claimId, caller, me?.Id, cancellationToken))!);
    }

    internal static async Task<Results<Created<VehicleExpenseView>, Ok<VehicleExpenseView>, ValidationProblem, ProblemHttpResult>> SubmitAsync(
        SubmitRequest request,
        HttpContext httpContext,
        FleetDbContext db,
        VehicleExpenseViews views,
        IEmployeeDirectory employees,
        IUserDirectory users,
        ISupplierDirectory suppliers,
        IPriceControl priceControl,
        IOptions<FleetOptions> settings,
        CompanyClock clock,
        FleetLog log,
        CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var options = settings.Value;
        var errors = new ValidationErrors();
        if (request.VehicleId is null or < 1)
        {
            errors.Add("vehicleId", "vehicleId is required.");
        }

        if (request.DriverEmployeeId is < 1)
        {
            errors.Add("driverEmployeeId", "driverEmployeeId must be an employee id.");
        }

        var type = Input.RequiredName<VehicleExpenseType>(request.Type, errors, "type");
        var fuel = type == VehicleExpenseType.Fuel;

        // A fuel claim's date is the company date of the moment its photos were taken (kept as captured when it was sent
        // later from the phone's queue); any other claim names its date.
        DateTime? capturedAt = null;
        DateOnly? expenseDate;
        if (fuel)
        {
            capturedAt = FuelClaimRules.CapturedAt(request.CapturedAt, clock.UtcNow, options.MaxClaimAgeDays, errors);
            expenseDate = capturedAt is { } moment ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(moment, clock.Zone)) : null;
            if (Input.OptionalDate(request.ExpenseDate, errors, "expenseDate", "The expense date") is { } namedDate && expenseDate is { } captured && namedDate != captured)
            {
                errors.Add("expenseDate", "A fuel claim's date is the day its photos were taken.");
            }
        }
        else
        {
            expenseDate = Input.RequiredDate(request.ExpenseDate, errors, "expenseDate", "The expense date");
        }

        if (expenseDate is { } date)
        {
            if (date > clock.Today)
            {
                errors.Add("expenseDate", "The expense date can't be in the future.");
            }
            else if (clock.Today.DayNumber - date.DayNumber > options.MaxClaimAgeDays)
            {
                errors.Add("expenseDate", $"A claim is made within {options.MaxClaimAgeDays} days of the expense.");
            }
        }

        // The reading is optional: the odometer photo is the evidence the reviewer checks.
        var odometer = VehicleFields.Odometer(request.OdometerKm, errors, required: false);
        decimal? amount = null;
        FuelReading? reading = null;
        if (fuel)
        {
            // The total comes from the pump display; litres and the price per litre are never typed, and the fuel is the vehicle's.
            if (request.FuelQuantity is not null)
            {
                errors.Add("fuelQuantity", "A fuel claim's litres are read from the pump display photo, never typed.");
            }

            if (request.UnitPrice is not null)
            {
                errors.Add("unitPrice", "A fuel claim's price per litre is computed by the server from the pump display reading, never typed.");
            }

            if (request.PriceItemId is not null)
            {
                errors.Add("priceItemId", "A fuel claim is compared with the vehicle's own fuel in Price Control; it names no price item.");
            }

            reading = FuelClaimRules.Reading(request.Amount, request.Reading, errors);
            amount = reading?.Amount;
        }
        else if (type is not null)
        {
            amount = NumberInput.RequiredMoney(request.Amount, errors, "amount", "The amount");
            if (request.FuelQuantity is not null || request.UnitPrice is not null)
            {
                errors.Add("fuelQuantity", "Litres and price per litre are for fuel claims only.");
            }

            if (request.PriceItemId is not null)
            {
                errors.Add("priceItemId", "Only a fuel claim is compared with the price list.");
            }

            if (request.PumpDisplayAttachmentId is not null || request.VehicleAtPumpAttachmentId is not null || request.Reading is not null || Input.Clean(request.CapturedAt) is not null)
            {
                errors.Add("pumpDisplayAttachmentId", "The pump photos and reading are for fuel claims only.");
            }
        }

        var vendor = Input.OptionalText(request.VendorName, VehicleExpenseConfiguration.VendorMaxLength, errors, "vendorName", "The vendor");
        var invoiceNo = Input.OptionalText(request.InvoiceNo, VehicleExpenseConfiguration.InvoiceNoMaxLength, errors, "invoiceNo", "The invoice number");
        var notes = Input.OptionalText(request.Notes, VehicleExpenseConfiguration.NotesMaxLength, errors, "notes", "Notes");
        var justification = Input.OptionalText(request.PriceJustification, VehicleExpenseConfiguration.JustificationMaxLength, errors, "priceJustification", "The justification");
        if (request.SupplierId is < 1)
        {
            errors.Add("supplierId", "supplierId must be a supplier id.");
        }

        if (request.PriceItemId is < 1)
        {
            errors.Add("priceItemId", "priceItemId must be a price item id.");
        }

        if (!fuel && request.OdometerAttachmentId is null or < 1)
        {
            errors.Add("odometerAttachmentId", "The odometer photo is required.");
        }

        if (request.InvoiceAttachmentId is null or < 1)
        {
            errors.Add("invoiceAttachmentId", "The invoice or receipt photo is required.");
        }

        if (fuel && request.PumpDisplayAttachmentId is null or < 1)
        {
            errors.Add("pumpDisplayAttachmentId", "The close-up photo of the pump display is required.");
        }

        if (fuel && request.VehicleAtPumpAttachmentId is null or < 1)
        {
            errors.Add("vehicleAtPumpAttachmentId", "The photo of the vehicle beside the pump is required.");
        }

        var gps = Gps(request.Gps, errors);
        if (!Guid.TryParse(Input.Clean(request.IdempotencyKey), out var key) || key == Guid.Empty)
        {
            errors.Add("idempotencyKey", "idempotencyKey must be a GUID made by the app for this claim.");
        }

        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var me = await employees.FindByUserAsync(caller.UserId, cancellationToken);
        if (await db.VehicleExpenses.AsNoTracking().SingleOrDefaultAsync(claim => claim.IdempotencyKey == key, cancellationToken) is { } repeat)
        {
            return repeat.CreatedBy == caller.UserId ? TypedResults.Ok((await views.LoadAsync(repeat.Id, caller, me?.Id, cancellationToken))!) : FleetProblems.KeyUsedElsewhere();
        }

        var vehicleId = request.VehicleId!.Value;
        var vehicle = await db.Vehicles.AsNoTracking().Include(candidate => candidate.Equipment).SingleOrDefaultAsync(candidate => candidate.Id == vehicleId, cancellationToken);
        if (vehicle is null)
        {
            return FleetProblems.VehicleNotFound();
        }

        // The accountable driver on the expense date, from the history: never typed, never another vehicle's.
        var assignment = await VehicleQueries.DriverOn(db, vehicleId, expenseDate!.Value).SingleOrDefaultAsync(cancellationToken);
        if (assignment is null)
        {
            return FleetProblems.NobodyDrove(expenseDate.Value);
        }

        var onBehalf = request.DriverEmployeeId is { } named && named != me?.Id;
        if (onBehalf && !caller.CanFleet(PermissionAction.Edit))
        {
            return FleetProblems.NotTheVehiclesDriver();
        }

        var driverId = request.DriverEmployeeId ?? me?.Id;
        if (driverId is null || assignment.DriverEmployeeId != driverId)
        {
            log.ActionRefused(caller.UserId, "SubmitVehicleExpense", "not the vehicle's driver on that day");
            return FleetProblems.NotTheVehiclesDriver();
        }

        if (request.SupplierId is { } supplierId)
        {
            SupplierChoice.Check(supplierId, await suppliers.FindAsync([supplierId], cancellationToken), errors, "supplierId");
            if (errors.HasErrors)
            {
                return FleetProblems.Validation(errors);
            }
        }

        // One fuel claim per vehicle waits for review or correction at a time: told before anything else is checked,
        // and checked again under the vehicle's lock below.
        if (fuel && await OutstandingFuelAsync(db, vehicleId, cancellationToken) is { } waiting)
        {
            return FleetProblems.OutstandingFuelClaim(waiting.Id, waiting.Number, waiting.Status.ToString());
        }

        // The evidence: uploaded by the caller, of the right kind, not bound elsewhere.
        var attachmentIds = new[] { request.OdometerAttachmentId, request.InvoiceAttachmentId, request.PumpDisplayAttachmentId, request.VehicleAtPumpAttachmentId }
            .OfType<int>().Concat(request.SupportingAttachmentIds ?? []).Distinct().ToList();
        var (attachments, refused) = await AttachmentBinding.LoadAsync(db, attachmentIds, caller.UserId, FleetAttachmentOwners.VehicleExpense, null, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        if ((!fuel || request.OdometerAttachmentId is not null) && attachments.SingleOrDefault(attachment => attachment.Id == request.OdometerAttachmentId) is not { Purpose: FleetAttachmentPurpose.Odometer })
        {
            return FleetProblems.EvidenceMissing("an odometer photo (purpose Odometer)");
        }

        if (attachments.SingleOrDefault(attachment => attachment.Id == request.InvoiceAttachmentId) is not { Purpose: FleetAttachmentPurpose.Invoice or FleetAttachmentPurpose.Receipt })
        {
            return FleetProblems.EvidenceMissing("an invoice or receipt photo (purpose Invoice or Receipt)");
        }

        if (fuel && attachments.SingleOrDefault(attachment => attachment.Id == request.PumpDisplayAttachmentId) is not { Purpose: FleetAttachmentPurpose.PumpDisplay })
        {
            return FleetProblems.EvidenceMissing("a close-up photo of the pump display (purpose PumpDisplay)");
        }

        if (fuel && attachments.SingleOrDefault(attachment => attachment.Id == request.VehicleAtPumpAttachmentId) is not { Purpose: FleetAttachmentPurpose.VehicleAtPump })
        {
            return FleetProblems.EvidenceMissing("a photo of the vehicle beside the pump (purpose VehicleAtPump)");
        }

        // Price Control for fuel: the vehicle's fuel, amount ÷ litres, compared at the expense date; the comparison is kept as made.
        FuelPricing? pricing = null;
        if (fuel)
        {
            pricing = await FuelPrices.PriceAsync(priceControl, vehicle, expenseDate.Value, reading!, cancellationToken);
            if (pricing.Blocked)
            {
                return FleetProblems.AboveRangeBlocked(pricing.EffectiveUnitPrice!.Value, pricing.Verdict!.MaxPrice ?? 0);
            }
        }

        var verdict = pricing?.Verdict;
        var escalateForPrice = pricing?.Escalate ?? false;
        var projectId = await VehicleQueries.ProjectOn(db, vehicleId, expenseDate.Value).FirstOrDefaultAsync(cancellationToken);
        var now = clock.UtcNow;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Equipment(vehicleId), cancellationToken))
        {
            return FleetProblems.TryAgainLater();
        }

        // Checked again under the lock: a retry with the same key may have landed meanwhile.
        if (await db.VehicleExpenses.AsNoTracking().AnyAsync(claim => claim.IdempotencyKey == key, cancellationToken))
        {
            var first = await db.VehicleExpenses.AsNoTracking().SingleAsync(claim => claim.IdempotencyKey == key, cancellationToken);
            return TypedResults.Ok((await views.LoadAsync(first.Id, caller, me?.Id, cancellationToken))!);
        }

        // And the vehicle's outstanding fuel claim: another phone, another driver or a second tap may have landed meanwhile.
        if (fuel && await OutstandingFuelAsync(db, vehicleId, cancellationToken) is { } landed)
        {
            return FleetProblems.OutstandingFuelClaim(landed.Id, landed.Number, landed.Status.ToString());
        }

        // The review flags, from what is already recorded. Information, never a block.
        var flags = await FlagsAsync(db, vehicleId, expenseDate.Value, odometer, amount!.Value, invoiceNo, attachments, options, cancellationToken);
        if (pricing is not null)
        {
            flags = [.. flags, .. pricing.Flags, .. FuelClaimRules.ReadingFlags(reading!)];
        }

        var claim = new VehicleExpense
        {
            VehicleId = vehicleId,
            DriverEmployeeId = driverId.Value,
            DriverAssignmentId = assignment.Id,
            ProjectId = projectId == 0 ? null : projectId,
            Type = type!.Value,
            ExpenseDate = expenseDate.Value,
            OdometerKm = odometer,
            FuelQuantity = reading?.OcrLiters,
            EffectiveUnitPrice = pricing?.EffectiveUnitPrice,
            Amount = amount.Value,
            CapturedAt = capturedAt,
            AmountSource = reading?.Source,
            OcrAmount = reading?.OcrAmount,
            OcrLiters = reading?.OcrLiters,
            OcrUnitPrice = reading?.OcrUnitPrice,
            OcrConfidence = reading?.Confidence,
            OcrEngine = reading?.Engine,
            OcrText = reading?.Text,
            VendorName = vendor,
            SupplierId = request.SupplierId,
            InvoiceNo = invoiceNo,
            Notes = notes,
            PriceItemId = pricing?.PriceItemId,
            PriceCheck = pricing?.PriceCheck,
            ReferenceMaxPrice = verdict?.MaxPrice,
            PriceJustification = justification,
            GpsLatitude = gps?.Latitude,
            GpsLongitude = gps?.Longitude,
            GpsCapturedAt = gps?.CapturedAt,
            Status = escalateForPrice && projectId == 0 ? VehicleExpenseStatus.Escalated : VehicleExpenseStatus.Submitted,
            SubmittedAt = now,
            CreatedBy = caller.UserId,
            IdempotencyKey = key,
        };
        claim.Flags.AddRange(flags.Select(flag => new VehicleExpenseFlag { Kind = flag.Kind, Detail = flag.Detail, RelatedExpenseId = flag.RelatedExpenseId, CreatedAt = now }));
        db.VehicleExpenses.Add(claim);

        // The accountants and the Executives hear of it; never the submitter.
        var watchers = (await users.ListActiveAsync([AccessRoles.Accountant, AccessRoles.Executive], cancellationToken)).Select(user => user.Id).Where(id => id != caller.UserId).Distinct().ToList();
        db.VehicleExpenseAlerts.AddRange(watchers.Select(recipient => new VehicleExpenseAlert { Expense = claim, RecipientUserId = recipient, CreatedAt = now }));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, VehicleExpenseConfiguration.IdempotencyKeyIndex))
        {
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var first = await db.VehicleExpenses.AsNoTracking().SingleAsync(candidate => candidate.IdempotencyKey == key, cancellationToken);
            return first.CreatedBy == caller.UserId ? TypedResults.Ok((await views.LoadAsync(first.Id, caller, me?.Id, cancellationToken))!) : FleetProblems.KeyUsedElsewhere();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, VehicleExpenseConfiguration.OutstandingFuelIndex))
        {
            // The database's own guard, should the lock ever be bypassed.
            await transaction.RollbackAsync(cancellationToken);
            db.ChangeTracker.Clear();
            var open = await OutstandingFuelAsync(db, vehicleId, cancellationToken);
            return open is null ? FleetProblems.TryAgainLater() : FleetProblems.OutstandingFuelClaim(open.Id, open.Number, open.Status.ToString());
        }

        AttachmentBinding.Bind(attachments, FleetAttachmentOwners.VehicleExpense, claim.Id, now);

        // The exposure: a Pending entry in the vehicle's custody. The posted balance doesn't move until approval.
        db.VehicleCustodyEntries.Add(new VehicleCustodyEntry
        {
            VehicleId = vehicleId,
            DriverEmployeeId = driverId,
            Kind = VehicleCustodyKind.Expense,
            Amount = claim.Amount,
            EntryDate = claim.ExpenseDate,
            Status = VehicleCustodyStatus.Pending,
            SourceType = VehicleCustodySources.Expense,
            SourceId = claim.Id,
            Reference = claim.Number,
            Notes = vendor,
            CreatedAt = now,
            CreatedBy = caller.UserId,
        });
        await db.SaveChangesAsync(cancellationToken);

        // Above the range on a project: a case for the Executive in Price Control, in this transaction.
        if (escalateForPrice && projectId != 0 && verdict is not null)
        {
            claim.PriceExceptionId = await priceControl.OpenExceptionAsync(
                new PriceExceptionDraft(PriceExceptionSources.VehicleExpense, claim.Id, projectId, request.SupplierId, pricing!.PriceItemId!.Value, verdict, MeasurementUnits.Liter,
                    reading!.OcrLiters!.Value, pricing.EffectiveUnitPrice!.Value, justification ?? FuelPriceReason, $"{claim.Number} fuel {vehicle.Plate}", expenseDate.Value, caller.UserId),
                SharedTransaction.Of(db), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        log.ClaimSubmitted(caller.UserId, vehicleId, claim.Id, claim.Type.ToString(), claim.Flags.Count, watchers.Count);
        return TypedResults.Created($"/api/vehicle-expenses/{claim.Id}", (await views.LoadAsync(claim.Id, caller, me?.Id, cancellationToken))!);
    }

    /// <summary>
    /// After an evidence request (returned for correction): more photos are added (never replacing the earlier ones) and
    /// the same claim is submitted again; no second claim is made. A camera fuel claim may come with a new pump display
    /// photo and a new reading: the amount, the price check and the pending custody entry follow it, the earlier reading
    /// stays in the flags' history and the photos.
    /// </summary>
    internal static async Task<Results<Ok<VehicleExpenseView>, ValidationProblem, ProblemHttpResult>> ResubmitAsync(
        int claimId, ResubmitRequest request, HttpContext httpContext, FleetDbContext db, VehicleExpenseViews views, IEmployeeDirectory employees, IPriceControl priceControl,
        CompanyClock clock, FleetLog log, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var notes = Input.OptionalText(request.Notes, VehicleExpenseConfiguration.NotesMaxLength, errors, "notes", "Notes");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        var ids = new[] { request.OdometerAttachmentId, request.InvoiceAttachmentId, request.PumpDisplayAttachmentId, request.VehicleAtPumpAttachmentId }
            .OfType<int>().Concat(request.SupportingAttachmentIds ?? []).Distinct().ToList();
        if (ids.Count == 0)
        {
            errors.Add("attachments", "Add at least one photo the reviewer asked for.");
        }

        FuelReading? reading = null;
        if (request.Reading is not null || request.Amount is not null)
        {
            reading = FuelClaimRules.Reading(request.Amount, request.Reading, errors);
            if (request.PumpDisplayAttachmentId is null)
            {
                errors.Add("pumpDisplayAttachmentId", "A new reading comes with the new pump display photo it was read from.");
            }
        }

        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var found = await db.VehicleExpenses.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == claimId, cancellationToken);
        if (found is null)
        {
            return FleetProblems.ClaimNotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Equipment(found.VehicleId), cancellationToken))
        {
            return FleetProblems.TryAgainLater();
        }

        var claim = await db.VehicleExpenses.Include(candidate => candidate.Flags).Include(candidate => candidate.Vehicle).SingleAsync(candidate => candidate.Id == claimId, cancellationToken);
        switch (VehicleExpenseRules.Resubmit(claim, caller.UserId))
        {
            case ClaimDecision.NotSubmitter:
                return ApiProblem.ForbiddenResult("Only the person who submitted this claim can resubmit it.");
            case ClaimDecision.WrongStatus:
                return FleetProblems.WrongStatus($"Only a claim waiting for more evidence can be resubmitted. This one is {claim.Status}.");
        }

        var cameraFuel = claim.Type == VehicleExpenseType.Fuel && claim.CapturedAt is not null;
        if (reading is not null && !cameraFuel)
        {
            errors.Add("reading", "Only a fuel claim made with the pump photos takes a new reading.");
            return FleetProblems.Validation(errors);
        }

        var (attachments, refused) = await AttachmentBinding.LoadAsync(db, ids, caller.UserId, FleetAttachmentOwners.VehicleExpense, claim.Id, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        foreach (var (id, purposes, what) in new (int?, FleetAttachmentPurpose[], string)[]
        {
            (request.OdometerAttachmentId, [FleetAttachmentPurpose.Odometer], "an odometer photo (purpose Odometer)"),
            (request.InvoiceAttachmentId, [FleetAttachmentPurpose.Invoice, FleetAttachmentPurpose.Receipt], "an invoice or receipt photo (purpose Invoice or Receipt)"),
            (request.PumpDisplayAttachmentId, [FleetAttachmentPurpose.PumpDisplay], "a close-up photo of the pump display (purpose PumpDisplay)"),
            (request.VehicleAtPumpAttachmentId, [FleetAttachmentPurpose.VehicleAtPump], "a photo of the vehicle beside the pump (purpose VehicleAtPump)"),
        })
        {
            if (id is not null && (attachments.SingleOrDefault(attachment => attachment.Id == id) is not { } named || !purposes.Contains(named.Purpose)))
            {
                return FleetProblems.EvidenceMissing(what);
            }
        }

        var now = clock.UtcNow;
        FuelPricing? pricing = null;
        if (reading is not null)
        {
            pricing = await FuelPrices.PriceAsync(priceControl, claim.Vehicle, claim.ExpenseDate, reading, cancellationToken);
            if (pricing.Blocked)
            {
                return FleetProblems.AboveRangeBlocked(pricing.EffectiveUnitPrice!.Value, pricing.Verdict!.MaxPrice ?? 0);
            }

            // The earlier price case no longer describes the claim: withdrawn, so the corrected price starts clean.
            if (claim.PriceExceptionId is not null)
            {
                await priceControl.WithdrawExceptionsAsync(PriceExceptionSources.VehicleExpense, claim.Id, caller.UserId, SharedTransaction.Of(db), cancellationToken);
                claim.PriceExceptionId = null;
            }

            claim.Amount = reading.Amount;
            claim.AmountSource = reading.Source;
            claim.OcrAmount = reading.OcrAmount;
            claim.OcrLiters = reading.OcrLiters;
            claim.OcrUnitPrice = reading.OcrUnitPrice;
            claim.OcrConfidence = reading.Confidence;
            claim.OcrEngine = reading.Engine;
            claim.OcrText = reading.Text;
            claim.FuelQuantity = reading.OcrLiters;
            claim.EffectiveUnitPrice = pricing.EffectiveUnitPrice;
            claim.PriceItemId = pricing.PriceItemId;
            claim.PriceCheck = pricing.PriceCheck;
            claim.ReferenceMaxPrice = pricing.Verdict?.MaxPrice;
            claim.Flags.AddRange(pricing.Flags.Concat(FuelClaimRules.ReadingFlags(reading))
                .Select(flag => new VehicleExpenseFlag { Kind = flag.Kind, Detail = flag.Detail, RelatedExpenseId = flag.RelatedExpenseId, CreatedAt = now }));

            var entry = await db.VehicleCustodyEntries.SingleOrDefaultAsync(candidate => candidate.SourceType == VehicleCustodySources.Expense && candidate.SourceId == claim.Id, cancellationToken);
            if (entry is { Status: VehicleCustodyStatus.Pending })
            {
                entry.Amount = reading.Amount;
                entry.UpdatedAt = now;
                entry.UpdatedBy = caller.UserId;
            }
        }

        claim.Resubmit(caller.UserId, now);
        if (notes is not null)
        {
            claim.Notes = notes;
        }

        AttachmentBinding.Bind(attachments, FleetAttachmentOwners.VehicleExpense, claim.Id, now);
        db.Entry(claim).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        // Above the range again: a new case for the Executive on a project, else the Executive decides the claim itself.
        if (pricing is { Escalate: true })
        {
            if (claim.ProjectId is { } projectId)
            {
                claim.PriceExceptionId = await priceControl.OpenExceptionAsync(
                    new PriceExceptionDraft(PriceExceptionSources.VehicleExpense, claim.Id, projectId, claim.SupplierId, pricing.PriceItemId!.Value, pricing.Verdict!, MeasurementUnits.Liter,
                        reading!.OcrLiters!.Value, pricing.EffectiveUnitPrice!.Value, FuelPriceReason, $"{claim.Number} fuel {claim.Vehicle.Plate}", claim.ExpenseDate, caller.UserId),
                    SharedTransaction.Of(db), cancellationToken);
            }
            else
            {
                claim.EscalateForPrice(caller.UserId, now);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        log.ClaimReviewed(caller.UserId, claim.Id, "Resubmitted");
        var me = await employees.FindByUserAsync(caller.UserId, cancellationToken);
        return TypedResults.Ok((await views.LoadAsync(claim.Id, caller, me?.Id, cancellationToken))!);
    }

    /// <summary>The vehicle's fuel claim waiting for review or returned for correction, if any (every form of claim).</summary>
    internal static Task<VehicleExpense?> OutstandingFuelAsync(FleetDbContext db, int vehicleId, CancellationToken cancellationToken) =>
        db.VehicleExpenses.AsNoTracking()
            .Where(claim => claim.VehicleId == vehicleId && claim.Type == VehicleExpenseType.Fuel && VehicleExpense.Outstanding.Contains(claim.Status))
            .OrderBy(claim => claim.Id).FirstOrDefaultAsync(cancellationToken);

    private static async Task<IReadOnlyList<ClaimFlag>> FlagsAsync(
        FleetDbContext db, int vehicleId, DateOnly expenseDate, decimal? odometerKm, decimal amount, string? invoiceNo, IReadOnlyList<FleetAttachment> attachments,
        FleetOptions options, CancellationToken cancellationToken)
    {
        var sameInvoice = invoiceNo is null
            ? []
            : await db.VehicleExpenses.AsNoTracking().Where(claim => claim.InvoiceNo == invoiceNo && claim.Status != VehicleExpenseStatus.Rejected).Select(claim => claim.Id).ToListAsync(cancellationToken);
        var hashes = attachments.Where(attachment => attachment.Purpose is FleetAttachmentPurpose.Invoice or FleetAttachmentPurpose.Receipt or FleetAttachmentPurpose.PumpDisplay or FleetAttachmentPurpose.VehicleAtPump)
            .Select(attachment => attachment.Sha256).ToList();
        var sameReceipt = hashes.Count == 0
            ? []
            : await db.Attachments.AsNoTracking()
                .Where(attachment => attachment.OwnerType == FleetAttachmentOwners.VehicleExpense && attachment.OwnerId != null && hashes.Contains(attachment.Sha256))
                .Select(attachment => attachment.OwnerId!.Value).Distinct().ToListAsync(cancellationToken);
        var sameAmount = await db.VehicleExpenses.AsNoTracking()
            .Where(claim => claim.VehicleId == vehicleId && claim.ExpenseDate == expenseDate && claim.Amount == amount && claim.Status != VehicleExpenseStatus.Rejected)
            .Select(claim => claim.Id).ToListAsync(cancellationToken);
        var lastOdometer = await VehicleQueries.LastTrustedOdometerAsync(db, vehicleId, cancellationToken);
        return VehicleExpenseRules.Flags(odometerKm, expenseDate, lastOdometer, options.ImplausibleDistanceKmPerDay, sameInvoice, sameReceipt, sameAmount);
    }

    private static (decimal Latitude, decimal Longitude, DateTime? CapturedAt)? Gps(GpsPoint? point, ValidationErrors errors)
    {
        if (point is null || (point.Latitude is null && point.Longitude is null && Input.Clean(point.CapturedAt) is null))
        {
            return null;
        }

        if (point.Latitude is not { } latitude || point.Longitude is not { } longitude || latitude is < -90 or > 90 || longitude is < -180 or > 180)
        {
            errors.Add("gps", "gps needs a latitude between -90 and 90 and a longitude between -180 and 180.");
            return null;
        }

        DateTime? capturedAt = null;
        if (Input.Clean(point.CapturedAt) is { } text)
        {
            if (DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var moment))
            {
                capturedAt = moment.UtcDateTime;
            }
            else
            {
                errors.Add("gps.capturedAt", "gps.capturedAt must be an ISO 8601 moment.");
            }
        }

        return (decimal.Round(latitude, 6), decimal.Round(longitude, 6), capturedAt);
    }
}
