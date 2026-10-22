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
/// fleet manager on their behalf) and <c>POST /{id}/resubmit</c> (the submitter, after an evidence request). The
/// custody account is always the vehicle's: nobody chooses it, nobody claims against another vehicle's cash.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item>Fuel: litres and price per litre; the amount is their product. Every claim carries an odometer photo and an
///   invoice or receipt photo; the odometer reading itself is optional (422 <c>evidence_required</c>).</item>
///   <item>Submitted: a Pending entry in the vehicle's custody (the exposure); the posted balance is unchanged until approval.</item>
///   <item>A fuel claim naming a Price Control item is compared with the price list at the expense date: above the
///   range under Block → 409 <c>price_above_range</c>; under ExecutiveApproval → a justification, and a case for the
///   Executive when the vehicle is on a project, else the claim is Escalated to the Executive directly.</item>
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
        string? IdempotencyKey);

    internal sealed record ResubmitRequest(string? Notes, int? OdometerAttachmentId, int? InvoiceAttachmentId, IReadOnlyList<int>? SupportingAttachmentIds, string? RowVersion);

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
            .WithSummary("Add the evidence a reviewer asked for and submit again (the submitter only); earlier evidence is kept");
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
        var expenseDate = Input.RequiredDate(request.ExpenseDate, errors, "expenseDate", "The expense date");
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
        decimal? fuelQuantity = null, unitPrice = null, amount = null;
        if (type == VehicleExpenseType.Fuel)
        {
            fuelQuantity = NumberInput.RequiredQuantity(request.FuelQuantity, errors, "fuelQuantity", "The litres");
            unitPrice = NumberInput.RequiredMoney(request.UnitPrice, errors, "unitPrice", "The price per litre");
            if (fuelQuantity is not null && unitPrice is not null)
            {
                amount = VehicleExpenseRules.Amount(VehicleExpenseType.Fuel, fuelQuantity, unitPrice, null);

                // The server computes the total; a total the client sends must agree with it (to the piastre).
                if (request.Amount is { } sent && Math.Abs(sent - amount!.Value) > 0.01m)
                {
                    errors.Add("amount", $"The total {sent:0.00} doesn't match litres × price per litre ({amount:0.00}).");
                }
            }
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

        if (request.OdometerAttachmentId is null or < 1)
        {
            errors.Add("odometerAttachmentId", "The odometer photo is required.");
        }

        if (request.InvoiceAttachmentId is null or < 1)
        {
            errors.Add("invoiceAttachmentId", "The invoice or receipt photo is required.");
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

        // The evidence: uploaded by the caller, of the right kind, not bound elsewhere.
        var attachmentIds = new[] { request.OdometerAttachmentId!.Value, request.InvoiceAttachmentId!.Value }.Concat(request.SupportingAttachmentIds ?? []).Distinct().ToList();
        var (attachments, refused) = await AttachmentBinding.LoadAsync(db, attachmentIds, caller.UserId, FleetAttachmentOwners.VehicleExpense, null, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        if (attachments.SingleOrDefault(attachment => attachment.Id == request.OdometerAttachmentId) is not { Purpose: FleetAttachmentPurpose.Odometer })
        {
            return FleetProblems.EvidenceMissing("an odometer photo (purpose Odometer)");
        }

        if (attachments.SingleOrDefault(attachment => attachment.Id == request.InvoiceAttachmentId) is not { Purpose: FleetAttachmentPurpose.Invoice or FleetAttachmentPurpose.Receipt })
        {
            return FleetProblems.EvidenceMissing("an invoice or receipt photo (purpose Invoice or Receipt)");
        }

        // Price Control for fuel naming a price item: compared at the expense date; the comparison is kept as made.
        PriceVerdict? verdict = null;
        var escalateForPrice = false;
        if (request.PriceItemId is { } priceItemId)
        {
            var items = await priceControl.FindItemsAsync([priceItemId], cancellationToken);
            if (!items.TryGetValue(priceItemId, out var item) || !item.IsActive)
            {
                errors.Add("priceItemId", "The price item was not found or is inactive.");
                return FleetProblems.Validation(errors);
            }

            verdict = await priceControl.CheckAsync(new PriceCheckRequest(priceItemId, request.SupplierId, expenseDate.Value, MeasurementUnits.Liter, unitPrice!.Value, fuelQuantity!.Value), cancellationToken);
            if (verdict.Result == PriceVerdict.UnitMismatch)
            {
                errors.Add("priceItemId", $"The price item is per {item.Unit}, not per litre, so the price can't be compared.");
                return FleetProblems.Validation(errors);
            }

            if (verdict.IsAboveRange)
            {
                if (verdict.Blocked)
                {
                    return FleetProblems.AboveRangeBlocked(unitPrice.Value, verdict.MaxPrice ?? 0);
                }

                if (justification is null)
                {
                    errors.Add("priceJustification", $"The price per litre is {verdict.VariancePerUnit:0.00} above the highest reference price {verdict.MaxPrice:0.00}: say why.");
                    return FleetProblems.Validation(errors);
                }

                escalateForPrice = verdict.Gates;
            }
        }

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

        // The review flags, from what is already recorded. Information, never a block.
        var flags = await FlagsAsync(db, vehicleId, expenseDate.Value, odometer, amount!.Value, invoiceNo, attachments, options, cancellationToken);
        if (verdict is not null)
        {
            if (verdict.IsAboveRange)
            {
                flags = [.. flags, new ClaimFlag(VehicleExpenseFlagKind.PriceAboveRange, $"{verdict.VariancePerUnit:0.00} per litre above the reference maximum {verdict.MaxPrice:0.00} ({verdict.VariancePercent:0.00}%).")];
            }
            else if (verdict.Result == PriceVerdict.NoReference)
            {
                flags = [.. flags, new ClaimFlag(VehicleExpenseFlagKind.NoReferencePrice, "No reference price range applies to this item on the expense date.")];
            }
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
            FuelQuantity = fuelQuantity,
            UnitPrice = unitPrice,
            Amount = amount.Value,
            VendorName = vendor,
            SupplierId = request.SupplierId,
            InvoiceNo = invoiceNo,
            Notes = notes,
            PriceItemId = request.PriceItemId,
            PriceCheck = verdict?.Result,
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
                new PriceExceptionDraft(PriceExceptionSources.VehicleExpense, claim.Id, projectId, request.SupplierId, request.PriceItemId!.Value, verdict, MeasurementUnits.Liter,
                    fuelQuantity!.Value, unitPrice!.Value, justification, $"{claim.Number} fuel {vehicle.Plate}", expenseDate.Value, caller.UserId),
                SharedTransaction.Of(db), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        log.ClaimSubmitted(caller.UserId, vehicleId, claim.Id, claim.Type.ToString(), claim.Flags.Count, watchers.Count);
        return TypedResults.Created($"/api/vehicle-expenses/{claim.Id}", (await views.LoadAsync(claim.Id, caller, me?.Id, cancellationToken))!);
    }

    /// <summary>After an evidence request: more photos are added (never replacing the earlier ones) and the claim is submitted again.</summary>
    internal static async Task<Results<Ok<VehicleExpenseView>, ValidationProblem, ProblemHttpResult>> ResubmitAsync(
        int claimId, ResubmitRequest request, HttpContext httpContext, FleetDbContext db, VehicleExpenseViews views, IEmployeeDirectory employees, CompanyClock clock, FleetLog log,
        CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var notes = Input.OptionalText(request.Notes, VehicleExpenseConfiguration.NotesMaxLength, errors, "notes", "Notes");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        var ids = new[] { request.OdometerAttachmentId, request.InvoiceAttachmentId }.OfType<int>().Concat(request.SupportingAttachmentIds ?? []).Distinct().ToList();
        if (ids.Count == 0)
        {
            errors.Add("attachments", "Add at least one photo the reviewer asked for.");
        }

        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var claim = await db.VehicleExpenses.Include(candidate => candidate.Flags).SingleOrDefaultAsync(candidate => candidate.Id == claimId, cancellationToken);
        if (claim is null)
        {
            return FleetProblems.ClaimNotFound();
        }

        switch (VehicleExpenseRules.Resubmit(claim, caller.UserId))
        {
            case ClaimDecision.NotSubmitter:
                return ApiProblem.ForbiddenResult("Only the person who submitted this claim can resubmit it.");
            case ClaimDecision.WrongStatus:
                return FleetProblems.WrongStatus($"Only a claim waiting for more evidence can be resubmitted. This one is {claim.Status}.");
        }

        var (attachments, refused) = await AttachmentBinding.LoadAsync(db, ids, caller.UserId, FleetAttachmentOwners.VehicleExpense, claim.Id, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        var now = clock.UtcNow;
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

        log.ClaimReviewed(caller.UserId, claim.Id, "Resubmitted");
        var me = await employees.FindByUserAsync(caller.UserId, cancellationToken);
        return TypedResults.Ok((await views.LoadAsync(claim.Id, caller, me?.Id, cancellationToken))!);
    }

    private static async Task<IReadOnlyList<ClaimFlag>> FlagsAsync(
        FleetDbContext db, int vehicleId, DateOnly expenseDate, decimal? odometerKm, decimal amount, string? invoiceNo, IReadOnlyList<FleetAttachment> attachments,
        FleetOptions options, CancellationToken cancellationToken)
    {
        var sameInvoice = invoiceNo is null
            ? []
            : await db.VehicleExpenses.AsNoTracking().Where(claim => claim.InvoiceNo == invoiceNo && claim.Status != VehicleExpenseStatus.Rejected).Select(claim => claim.Id).ToListAsync(cancellationToken);
        var hashes = attachments.Where(attachment => attachment.Purpose is FleetAttachmentPurpose.Invoice or FleetAttachmentPurpose.Receipt).Select(attachment => attachment.Sha256).ToList();
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
