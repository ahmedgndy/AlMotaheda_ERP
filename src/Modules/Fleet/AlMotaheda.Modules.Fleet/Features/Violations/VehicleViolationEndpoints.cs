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
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Features.Violations;

internal sealed record VehicleViolationView(
    int Id, string Number, int VehicleId, string Plate, DriverRef? Driver, int? DriverAssignmentId, DateOnly ViolationDate, string Category, string Description,
    decimal? Amount, string? TicketNo, string? TripReference, decimal? GpsLatitude, decimal? GpsLongitude, string Status, DateTime? ReviewedAt, FleetPerson? Reviewer,
    string? ReviewNote, string? Resolution, int? HrReferralId, string? HrReferralStatus, DateTime CreatedAt, FleetPerson Recorder, IReadOnlyList<FleetAttachmentView> Attachments, string RowVersion);

/// <summary>
/// Vehicle violations: <c>GET /api/vehicle-violations</c> and <c>/{id}</c> (fleet View; amounts with fleet See money),
/// <c>POST /api/vehicle-violations</c> (fleet Create: the responsible driver is read from the assignment history at
/// the violation date, never typed), <c>POST /{id}/review</c> (fleet Approve, not the recorder: Resolved or Dismissed
/// with a note) and <c>POST /{id}/refer-to-hr</c> (fleet Approve: an HR referral for the driver; HR decides on its
/// own whether anything is deducted).
/// </summary>
internal static class VehicleViolationEndpoints
{
    internal sealed record RecordRequest(
        int? VehicleId, string? ViolationDate, string? Category, string? Description, decimal? Amount, string? TicketNo, string? TripReference,
        decimal? GpsLatitude, decimal? GpsLongitude, IReadOnlyList<int>? AttachmentIds);

    internal sealed record ReviewRequest(string? Outcome, string? Note, string? Resolution, string? RowVersion);

    internal sealed record ReferRequest(string? Details, string? RowVersion);

    internal sealed record ListResponse(IReadOnlyList<VehicleViolationView> Items, IReadOnlyDictionary<string, int> CountsByStatus);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/vehicle-violations", ListAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.View)
            .WithName("ListVehicleViolations")
            .WithSummary("Vehicle violations (vehicleId, status, driverEmployeeId), newest first, with counts per status");

        api.MapGet("/vehicle-violations/{violationId:int}", GetAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.View)
            .WithName("GetVehicleViolation")
            .WithSummary("One violation with its evidence and review");

        api.MapPost("/vehicle-violations", RecordAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.Create)
            .WithName("RecordVehicleViolation")
            .WithSummary("Record a violation (fleet Create): the responsible driver comes from the assignment history at that date");

        api.MapPost("/vehicle-violations/{violationId:int}/review", ReviewAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.Approve)
            .WithName("ReviewVehicleViolation")
            .WithSummary("Resolve or dismiss a violation with a note (fleet Approve; not the recorder)");

        api.MapPost("/vehicle-violations/{violationId:int}/refer-to-hr", ReferAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.Approve)
            .WithName("ReferVehicleViolationToHr")
            .WithSummary("Refer a violation to HR for investigation (fleet Approve). 409 already_referred, no_driver_on_date");
    }

    internal static async Task<Results<Ok<ListResponse>, ValidationProblem, ProblemHttpResult>> ListAsync(
        HttpContext httpContext, FleetDbContext db, ViolationViews views, CancellationToken cancellationToken, int? vehicleId = null, string? status = null, int? driverEmployeeId = null)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var wanted = Input.OptionalName<VehicleViolationStatus>(status, errors, "status");
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var query = db.VehicleViolations.AsNoTracking()
            .Where(violation => (vehicleId == null || violation.VehicleId == vehicleId) && (driverEmployeeId == null || violation.DriverEmployeeId == driverEmployeeId));
        var counts = await query.GroupBy(violation => violation.Status).Select(group => new { group.Key, Count = group.Count() }).ToListAsync(cancellationToken);
        var rows = await query.Where(violation => wanted == null || violation.Status == wanted)
            .Include(violation => violation.Vehicle).OrderByDescending(violation => violation.ViolationDate).ThenByDescending(violation => violation.Id).Take(200).ToListAsync(cancellationToken);
        return TypedResults.Ok(new ListResponse(await views.FromAsync(rows, caller, cancellationToken), counts.ToDictionary(entry => entry.Key.ToString(), entry => entry.Count)));
    }

    internal static async Task<Results<Ok<VehicleViolationView>, ProblemHttpResult>> GetAsync(
        int violationId, HttpContext httpContext, ViolationViews views, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var view = await views.LoadAsync(violationId, caller, cancellationToken);
        return view is null ? FleetProblems.ViolationNotFound() : TypedResults.Ok(view);
    }

    internal static async Task<Results<Created<VehicleViolationView>, ValidationProblem, ProblemHttpResult>> RecordAsync(
        RecordRequest request, HttpContext httpContext, FleetDbContext db, ViolationViews views, CompanyClock clock, FleetLog log, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        if (request.VehicleId is null or < 1)
        {
            errors.Add("vehicleId", "vehicleId is required.");
        }

        var date = Input.RequiredDate(request.ViolationDate, errors, "violationDate", "The violation date");
        if (date > clock.Today)
        {
            errors.Add("violationDate", "The violation date can't be in the future.");
        }

        var category = Input.RequiredName<VehicleViolationCategory>(request.Category, errors, "category");
        var description = Input.RequiredText(request.Description, VehicleViolationConfiguration.DescriptionMaxLength, errors, "description", "The description");
        var amount = NumberInput.OptionalMoney(request.Amount, errors, "amount", "The amount");
        var ticket = Input.OptionalText(request.TicketNo, VehicleViolationConfiguration.TicketMaxLength, errors, "ticketNo", "The ticket number");
        var trip = Input.OptionalText(request.TripReference, VehicleViolationConfiguration.TripMaxLength, errors, "tripReference", "The trip reference");
        if ((request.GpsLatitude is null) != (request.GpsLongitude is null) || request.GpsLatitude is < -90 or > 90 || request.GpsLongitude is < -180 or > 180)
        {
            errors.Add("gpsLatitude", "A GPS point needs a latitude between -90 and 90 and a longitude between -180 and 180.");
        }

        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var vehicle = await db.Vehicles.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == request.VehicleId, cancellationToken);
        if (vehicle is null)
        {
            return FleetProblems.VehicleNotFound();
        }

        var (attachments, refused) = await AttachmentBinding.LoadAsync(db, request.AttachmentIds ?? [], caller.UserId, FleetAttachmentOwners.VehicleViolation, null, cancellationToken);
        if (refused is not null)
        {
            return refused;
        }

        // The responsible driver on that day, from the history: never typed by the recorder.
        var assignment = await VehicleQueries.DriverOn(db, vehicle.Id, date!.Value).SingleOrDefaultAsync(cancellationToken);
        var now = clock.UtcNow;
        var violation = new VehicleViolation
        {
            VehicleId = vehicle.Id,
            DriverEmployeeId = assignment?.DriverEmployeeId,
            DriverAssignmentId = assignment?.Id,
            ViolationDate = date.Value,
            Category = category!.Value,
            Description = description!,
            Amount = amount,
            TicketNo = ticket,
            TripReference = trip,
            GpsLatitude = request.GpsLatitude is { } latitude ? decimal.Round(latitude, 6) : null,
            GpsLongitude = request.GpsLongitude is { } longitude ? decimal.Round(longitude, 6) : null,
            CreatedAt = now,
            CreatedBy = caller.UserId,
        };
        db.VehicleViolations.Add(violation);
        await db.SaveChangesAsync(cancellationToken);
        AttachmentBinding.Bind(attachments, FleetAttachmentOwners.VehicleViolation, violation.Id, now);
        await db.SaveChangesAsync(cancellationToken);

        log.ViolationRecorded(caller.UserId, vehicle.Id, violation.Id, violation.DriverEmployeeId);
        return TypedResults.Created($"/api/vehicle-violations/{violation.Id}", (await views.LoadAsync(violation.Id, caller, cancellationToken))!);
    }

    internal static async Task<Results<Ok<VehicleViolationView>, ValidationProblem, ProblemHttpResult>> ReviewAsync(
        int violationId, ReviewRequest request, HttpContext httpContext, FleetDbContext db, ViolationViews views, CompanyClock clock, FleetLog log, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var outcome = Input.RequiredName<VehicleViolationStatus>(request.Outcome, errors, "outcome");
        if (outcome is VehicleViolationStatus.Recorded or VehicleViolationStatus.UnderReview)
        {
            errors.Add("outcome", "outcome must be Resolved or Dismissed.");
        }

        var note = Input.RequiredText(request.Note, VehicleViolationConfiguration.NoteMaxLength, errors, "note", "The note");
        var resolution = Input.OptionalText(request.Resolution, VehicleViolationConfiguration.NoteMaxLength, errors, "resolution", "The resolution");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var violation = await db.VehicleViolations.SingleOrDefaultAsync(candidate => candidate.Id == violationId, cancellationToken);
        if (violation is null)
        {
            return FleetProblems.ViolationNotFound();
        }

        if (violation.CreatedBy == caller.UserId)
        {
            return FleetProblems.ReviewerIsRecorder();
        }

        if (!violation.CanMoveTo(outcome!.Value))
        {
            return FleetProblems.WrongStatus($"This violation is already {violation.Status}.");
        }

        violation.Review(outcome.Value, caller.UserId, clock.UtcNow, note!, resolution);
        db.Entry(violation).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.ViolationReviewed(caller.UserId, violation.Id, outcome.ToString()!);
        return TypedResults.Ok((await views.LoadAsync(violation.Id, caller, cancellationToken))!);
    }

    internal static async Task<Results<Ok<VehicleViolationView>, ValidationProblem, ProblemHttpResult>> ReferAsync(
        int violationId, ReferRequest request, HttpContext httpContext, FleetDbContext db, ViolationViews views, IHrReferrals referrals, CompanyClock clock, FleetLog log,
        CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var details = Input.RequiredText(request.Details, 2000, errors, "details", "The details");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var violation = await db.VehicleViolations.Include(candidate => candidate.Vehicle).SingleOrDefaultAsync(candidate => candidate.Id == violationId, cancellationToken);
        if (violation is null)
        {
            return FleetProblems.ViolationNotFound();
        }

        if (violation.DriverEmployeeId is not { } driver)
        {
            return FleetProblems.NobodyDrove(violation.ViolationDate);
        }

        if (violation.HrReferralId is not null)
        {
            return FleetProblems.ReferredAlready();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Entry(violation).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        var now = clock.UtcNow;
        violation.UpdatedAt = now;
        violation.UpdatedBy = caller.UserId;
        if (violation.CanMoveTo(VehicleViolationStatus.UnderReview))
        {
            violation.Review(VehicleViolationStatus.UnderReview, caller.UserId, now, "Referred to HR", null);
        }

        try
        {
            violation.HrReferralId = await referrals.ReferAsync(
                new HrReferralDraft(HrReferralSources.VehicleViolation, violation.Id, driver, violation.VehicleId,
                    $"Vehicle violation {violation.Number} ({violation.Vehicle.Plate}, {violation.Category})", details!, violation.Amount, violation.TicketNo ?? violation.Number, caller.UserId),
                SharedTransaction.Of(db), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsAnyUnique(exception))
        {
            return FleetProblems.ReferredAlready();
        }

        await transaction.CommitAsync(cancellationToken);
        log.ViolationReferred(caller.UserId, violation.Id, violation.HrReferralId!.Value);
        return TypedResults.Ok((await views.LoadAsync(violation.Id, caller, cancellationToken))!);
    }
}

internal sealed class ViolationViews(FleetDbContext db, IUserDirectory users, IEmployeeDirectory employees, IHrReferrals referrals)
{
    public async Task<VehicleViolationView?> LoadAsync(int violationId, FleetCaller caller, CancellationToken cancellationToken)
    {
        var violation = await db.VehicleViolations.AsNoTracking().Include(candidate => candidate.Vehicle).SingleOrDefaultAsync(candidate => candidate.Id == violationId, cancellationToken);
        return violation is null ? null : (await FromAsync([violation], caller, cancellationToken))[0];
    }

    public async Task<IReadOnlyList<VehicleViolationView>> FromAsync(IReadOnlyList<VehicleViolation> violations, FleetCaller caller, CancellationToken cancellationToken)
    {
        var people = await Lookups.UsersAsync(users, violations.SelectMany(violation => new[] { (int?)violation.CreatedBy, violation.ReviewedBy }), cancellationToken);
        var driverIds = violations.Select(violation => violation.DriverEmployeeId).OfType<int>().Distinct().ToList();
        var drivers = driverIds.Count == 0 ? new Dictionary<int, EmployeeRef>() : await employees.FindAsync(driverIds, cancellationToken);
        var ids = violations.Select(violation => violation.Id).ToList();
        var attachments = await db.Attachments.AsNoTracking()
            .Where(attachment => attachment.OwnerType == FleetAttachmentOwners.VehicleViolation && attachment.OwnerId != null && ids.Contains(attachment.OwnerId.Value))
            .OrderBy(attachment => attachment.Id).ToListAsync(cancellationToken);
        var referred = await referrals.StatesAsync(HrReferralSources.VehicleViolation, ids, cancellationToken);
        var seesMoney = caller.SeesVehicleMoney;

        return violations.Select(violation => new VehicleViolationView(
            violation.Id, violation.Number, violation.VehicleId, violation.Vehicle.Plate, DriverRef.From(violation.DriverEmployeeId, drivers), violation.DriverAssignmentId,
            violation.ViolationDate, violation.Category.ToString(), violation.Description, seesMoney ? violation.Amount : null, violation.TicketNo, violation.TripReference,
            violation.GpsLatitude, violation.GpsLongitude, violation.Status.ToString(), violation.ReviewedAt, FleetPerson.FromOptional(violation.ReviewedBy, people),
            violation.ReviewNote, violation.Resolution, violation.HrReferralId, referred.TryGetValue(violation.Id, out var state) ? state.Status : null,
            violation.CreatedAt, FleetPerson.From(violation.CreatedBy, people),
            attachments.Where(attachment => attachment.OwnerId == violation.Id).Select(FleetAttachmentView.From).ToList(), RowVersions.Format(violation.RowVersion))).ToList();
    }
}
