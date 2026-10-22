using System.Text.RegularExpressions;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Data.Configurations;
using AlMotaheda.Modules.Fleet.Security;
using AlMotaheda.Modules.HR.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Features.Vehicles;

/// <summary>A driver (an HR employee) as the vehicle screens show them.</summary>
internal sealed record DriverRef(int EmployeeId, string EmployeeNo, string NameAr, string NameEn, int? UserId, bool IsActive)
{
    public static DriverRef? From(int? employeeId, IReadOnlyDictionary<int, EmployeeRef> employees) =>
        employeeId is { } id
            ? employees.TryGetValue(id, out var employee)
                ? new DriverRef(employee.Id, employee.EmployeeNo, employee.NameAr, employee.NameEn, employee.UserId, employee.IsActive)
                : new DriverRef(id, $"#{id}", $"#{id}", $"#{id}", null, false)
            : null;
}

internal sealed record DriverAssignmentView(
    int Id, int VehicleId, DriverRef Driver, DateOnly StartDate, DateOnly? EndDate, decimal? StartOdometerKm, decimal? EndOdometerKm, string? Notes,
    string? HandoverNote, decimal? HandoverCustodyBalance, decimal? HandoverPendingClaims, DateTime AssignedAt, int AssignedBy, DateTime? EndedAt, int? EndedBy, string RowVersion)
{
    public static DriverAssignmentView From(VehicleDriverAssignment assignment, IReadOnlyDictionary<int, EmployeeRef> employees, bool seesMoney) => new(
        assignment.Id, assignment.VehicleId, DriverRef.From(assignment.DriverEmployeeId, employees)!, assignment.StartDate, assignment.EndDate,
        assignment.StartOdometerKm, assignment.EndOdometerKm, assignment.Notes, assignment.HandoverNote,
        seesMoney ? assignment.HandoverCustodyBalance : null, seesMoney ? assignment.HandoverPendingClaims : null,
        assignment.AssignedAt, assignment.AssignedBy, assignment.EndedAt, assignment.EndedBy, RowVersions.Format(assignment.RowVersion));
}

/// <summary>A vehicle: the register row's fields and the vehicle's own. Custody figures only with See money or for its driver.</summary>
internal sealed record VehicleView(
    int Id,
    string AssetNo,
    string NameAr,
    string NameEn,
    string Plate,
    string Type,
    int? ModelYear,
    string FuelType,
    string Ownership,
    string Condition,
    string Status,
    decimal OdometerKm,
    DateTime? OdometerUpdatedAt,
    string GpsStatus,
    bool TrackingEnabled,
    string? GpsProvider,
    string? GpsDeviceId,
    string? Notes,
    DriverAssignmentView? CurrentDriver,
    int? CurrentProjectId,
    decimal? CustodyPosted,
    decimal? CustodyPending,
    int PendingClaims,
    DateTime CreatedAt,
    FleetPerson CreatedBy,
    DateTime? UpdatedAt,
    string RowVersion);

internal sealed record VehicleCustodyLineView(
    int Id, DateOnly EntryDate, string Kind, string Status, decimal Amount, decimal BalanceAfter, string SourceType, int SourceId, DriverRef? Driver,
    string? Reference, string? Notes, DateTime CreatedAt, int CreatedBy);

/// <summary>Builds the vehicle views with one lookup per page for people and drivers.</summary>
internal sealed class VehicleViews(FleetDbContext db, IUserDirectory users, IEmployeeDirectory employees)
{
    public async Task<VehicleView?> LoadAsync(int vehicleId, FleetCaller caller, CancellationToken cancellationToken)
    {
        var row = await db.Vehicles.AsNoTracking().Include(vehicle => vehicle.Equipment).SingleOrDefaultAsync(vehicle => vehicle.Id == vehicleId, cancellationToken);
        return row is null ? null : (await FromAsync([row], caller, cancellationToken))[0];
    }

    public async Task<IReadOnlyList<VehicleView>> FromAsync(IReadOnlyList<Vehicle> vehicles, FleetCaller caller, CancellationToken cancellationToken)
    {
        var ids = vehicles.Select(vehicle => vehicle.Id).ToList();
        var drivers = await db.VehicleDriverAssignments.AsNoTracking().Where(assignment => ids.Contains(assignment.VehicleId) && assignment.EndDate == null).ToListAsync(cancellationToken);
        var projects = await db.EquipmentAssignments.AsNoTracking().Where(stay => ids.Contains(stay.EquipmentId) && stay.EndDate == null)
            .Select(stay => new { stay.EquipmentId, stay.ProjectId }).ToDictionaryAsync(stay => stay.EquipmentId, stay => stay.ProjectId, cancellationToken);
        var entries = await db.VehicleCustodyEntries.AsNoTracking().Where(entry => ids.Contains(entry.VehicleId)).ToListAsync(cancellationToken);
        var people = await Lookups.UsersAsync(users, vehicles.Select(vehicle => (int?)vehicle.CreatedBy), cancellationToken);
        var driverIds = drivers.Select(driver => driver.DriverEmployeeId).Distinct().ToList();
        var driverRefs = driverIds.Count == 0 ? new Dictionary<int, EmployeeRef>() : await employees.FindAsync(driverIds, cancellationToken);
        var me = caller.SeesVehicleMoney ? null : await employees.FindByUserAsync(caller.UserId, cancellationToken);

        return vehicles.Select(vehicle =>
        {
            var driver = drivers.SingleOrDefault(candidate => candidate.VehicleId == vehicle.Id);
            var figures = VehicleCustodyBalance.Compute(entries.Where(entry => entry.VehicleId == vehicle.Id));
            var seesMoney = caller.SeesVehicleMoney || (me is not null && driver?.DriverEmployeeId == me.Id);
            return new VehicleView(
                vehicle.Id, vehicle.Equipment.AssetNo, vehicle.Equipment.NameAr, vehicle.Equipment.NameEn, vehicle.Plate, vehicle.Type.ToString(), vehicle.ModelYear,
                vehicle.FuelType.ToString(), vehicle.Equipment.Ownership.ToString(), vehicle.Equipment.Condition.ToString(), vehicle.Equipment.Status.ToString(),
                vehicle.OdometerKm, vehicle.OdometerUpdatedAt, vehicle.GpsStatus.ToString(), vehicle.TrackingEnabled, vehicle.GpsProvider, vehicle.GpsDeviceId, vehicle.Notes,
                driver is null ? null : DriverAssignmentView.From(driver, driverRefs, seesMoney), projects.GetValueOrDefault(vehicle.Id),
                seesMoney ? figures.Posted : null, seesMoney ? figures.Pending : null, figures.PendingCount,
                vehicle.CreatedAt, FleetPerson.From(vehicle.CreatedBy, people), vehicle.UpdatedAt, RowVersions.Format(vehicle.RowVersion));
        }).ToList();
    }

    /// <summary>True when the caller is the employee driving the vehicle now.</summary>
    public async Task<bool> IsCurrentDriverAsync(int vehicleId, FleetCaller caller, CancellationToken cancellationToken)
    {
        var me = await employees.FindByUserAsync(caller.UserId, cancellationToken);
        return me is not null && await VehicleQueries.CurrentDriver(db, vehicleId).AnyAsync(assignment => assignment.DriverEmployeeId == me.Id, cancellationToken);
    }
}

/// <summary>
/// Vehicles: the register (<c>GET/POST /api/vehicles</c>, <c>GET/PUT /api/vehicles/{id}</c>, fleet View/Create/Edit:
/// a vehicle is an equipment row of category Vehicle plus its plate, odometer and GPS status), its drivers over time
/// (<c>GET/POST /api/vehicles/{id}/drivers</c>, <c>POST …/drivers/{aid}/end</c>: a hand-over records the custody
/// balance and the open claims at that moment) and its operating custody (<c>GET /api/vehicles/{id}/custody</c>).
/// </summary>
internal static partial class VehicleEndpoints
{
    internal sealed record VehicleRequest(
        string? AssetNo, string? NameAr, string? NameEn, string? Plate, string? Type, int? ModelYear, string? FuelType, string? Ownership, string? Condition,
        string? Status, decimal? OdometerKm, string? GpsProvider, string? GpsDeviceId, string? Notes, string? RowVersion);

    internal sealed record OdometerRequest(decimal? OdometerKm, string? RowVersion);

    internal sealed record AssignDriverRequest(int? DriverEmployeeId, string? StartDate, decimal? StartOdometerKm, string? Notes);

    internal sealed record EndDriverRequest(string? EndDate, decimal? EndOdometerKm, string? HandoverNote, string? RowVersion);

    internal sealed record ListResponse(IReadOnlyList<VehicleView> Items, int Page, int PageSize, int TotalCount);

    internal sealed record DriversResponse(IReadOnlyList<DriverAssignmentView> Items);

    /// <summary>The vehicle's operating custody. Finance's account number for it is on the Finance side (<c>GET /api/custody-accounts?kind=Vehicle</c>).</summary>
    internal sealed record CustodyResponse(int VehicleId, string Plate, VehicleCustodyFigures Figures, DriverRef? CurrentDriver, IReadOnlyList<VehicleCustodyLineView> Lines);

    public static void Map(IEndpointRouteBuilder api)
    {
        api.MapGet("/vehicles", ListAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.View)
            .WithName("ListVehicles")
            .WithSummary("The vehicles (search: plate, asset no., names; status; driverEmployeeId: the current driver; page, pageSize)");

        api.MapGet("/vehicles/{vehicleId:int}", GetAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.View)
            .WithName("GetVehicle")
            .WithSummary("One vehicle with its current driver, project and custody figures (figures with See money or for its driver)");

        api.MapPost("/vehicles", CreateAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.Create)
            .WithName("CreateVehicle")
            .WithSummary("Register a vehicle: one equipment row of category Vehicle plus plate, type and odometer. 409 asset_no_taken, plate_taken");

        api.MapPut("/vehicles/{vehicleId:int}", UpdateAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.Edit)
            .WithName("UpdateVehicle")
            .WithSummary("Edit a vehicle (fleet Edit); the odometer never decreases. rowVersion");

        api.MapPost("/vehicles/{vehicleId:int}/odometer", OdometerAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.Edit)
            .WithName("RecordVehicleOdometer")
            .WithSummary("Record a trusted odometer reading (fleet Edit); never below the last one");

        api.MapGet("/vehicles/{vehicleId:int}/drivers", ListDriversAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.View)
            .WithName("ListVehicleDrivers")
            .WithSummary("The vehicle's drivers over time, newest first");

        api.MapPost("/vehicles/{vehicleId:int}/drivers", AssignDriverAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.Edit)
            .WithName("AssignVehicleDriver")
            .WithSummary("Give the vehicle to a driver (an HR employee) from a date (fleet Edit). 409 driver_assigned");

        api.MapPost("/vehicles/{vehicleId:int}/drivers/{assignmentId:int}/end", EndDriverAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.Edit)
            .WithName("EndVehicleDriver")
            .WithSummary("End a driver's stay (fleet Edit): records the custody balance and the open claims handed over; history is kept");

        api.MapGet("/vehicles/{vehicleId:int}/custody", CustodyAsync)
            .RequirePermission(FleetPermissions.Fleet, PermissionAction.View)
            .WithName("GetVehicleCustody")
            .WithSummary("The vehicle's operating custody: fundings, returns, claims (pending apart), adjustments, running balance (fleet or fuel See money, or its driver)");
    }

    internal static async Task<Results<Ok<ListResponse>, ValidationProblem, ProblemHttpResult>> ListAsync(
        HttpContext httpContext, FleetDbContext db, VehicleViews views, CancellationToken cancellationToken,
        string? search = null, string? status = null, int? driverEmployeeId = null, int page = 1, int pageSize = Paging.DefaultPageSize)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var wantedStatus = Input.OptionalName<EquipmentStatus>(status, errors, "status");
        Paging.Validate(page, pageSize, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var query = db.Vehicles.AsNoTracking().Include(vehicle => vehicle.Equipment).AsQueryable();
        if (Input.Clean(search) is { } text)
        {
            var upper = text.ToUpperInvariant();
            query = query.Where(vehicle => vehicle.Plate.Contains(upper) || vehicle.Equipment.AssetNo.Contains(upper)
                                           || vehicle.Equipment.NameAr.Contains(text) || vehicle.Equipment.NameEn.Contains(text));
        }

        if (wantedStatus is { } wanted)
        {
            query = query.Where(vehicle => vehicle.Equipment.Status == wanted);
        }

        if (driverEmployeeId is { } driverId)
        {
            query = query.Where(vehicle => db.VehicleDriverAssignments.Any(assignment => assignment.VehicleId == vehicle.Id && assignment.EndDate == null && assignment.DriverEmployeeId == driverId));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(vehicle => vehicle.Plate).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return TypedResults.Ok(new ListResponse(await views.FromAsync(rows, caller, cancellationToken), page, pageSize, totalCount));
    }

    internal static async Task<Results<Ok<VehicleView>, ProblemHttpResult>> GetAsync(
        int vehicleId, HttpContext httpContext, VehicleViews views, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var view = await views.LoadAsync(vehicleId, caller, cancellationToken);
        return view is null ? FleetProblems.VehicleNotFound() : TypedResults.Ok(view);
    }

    internal static async Task<Results<Created<VehicleView>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        VehicleRequest request, HttpContext httpContext, FleetDbContext db, VehicleViews views, CompanyClock clock, FleetLog log, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = VehicleFields.Validate(request, errors, forCreate: true);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        if (await db.Equipment.AnyAsync(equipment => equipment.AssetNo == fields!.AssetNo, cancellationToken))
        {
            return FleetProblems.AssetNoAlreadyUsed();
        }

        if (await db.Vehicles.AnyAsync(vehicle => vehicle.Plate == fields!.Plate, cancellationToken))
        {
            return FleetProblems.PlateAlreadyUsed();
        }

        var now = clock.UtcNow;
        var equipment = new Equipment
        {
            AssetNo = fields!.AssetNo!,
            NameAr = fields.NameAr!,
            NameEn = fields.NameEn!,
            Category = EquipmentCategory.Vehicle,
            TypeName = fields.Type.ToString(),
            SerialNo = fields.Plate,
            Ownership = fields.Ownership,
            Condition = fields.Condition,
            Status = fields.Status,
            Notes = fields.Notes,
            CreatedAt = now,
            CreatedBy = caller.UserId,
        };
        var vehicle = new Vehicle
        {
            Equipment = equipment,
            Plate = fields.Plate,
            Type = fields.Type,
            ModelYear = fields.ModelYear,
            FuelType = fields.FuelType,
            OdometerKm = fields.OdometerKm ?? 0,
            OdometerUpdatedAt = fields.OdometerKm is null ? null : now,
            OdometerUpdatedBy = fields.OdometerKm is null ? null : caller.UserId,
            GpsProvider = fields.GpsProvider,
            GpsDeviceId = fields.GpsDeviceId,
            GpsStatus = fields.GpsProvider is null ? VehicleGpsStatus.NotConfigured : VehicleGpsStatus.Configured,
            Notes = fields.Notes,
            CreatedAt = now,
            CreatedBy = caller.UserId,
        };
        db.Equipment.Add(equipment);
        db.Vehicles.Add(vehicle);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, EquipmentConfiguration.AssetNoIndex))
        {
            return FleetProblems.AssetNoAlreadyUsed();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, VehicleConfiguration.PlateIndex))
        {
            return FleetProblems.PlateAlreadyUsed();
        }

        log.VehicleCreated(caller.UserId, vehicle.Id);
        return TypedResults.Created($"/api/vehicles/{vehicle.Id}", (await views.LoadAsync(vehicle.Id, caller, cancellationToken))!);
    }

    internal static async Task<Results<Ok<VehicleView>, ValidationProblem, ProblemHttpResult>> UpdateAsync(
        int vehicleId, VehicleRequest request, HttpContext httpContext, FleetDbContext db, VehicleViews views, CompanyClock clock, FleetLog log, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var fields = VehicleFields.Validate(request, errors, forCreate: false);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var vehicle = await db.Vehicles.Include(candidate => candidate.Equipment).SingleOrDefaultAsync(candidate => candidate.Id == vehicleId, cancellationToken);
        if (vehicle is null)
        {
            return FleetProblems.VehicleNotFound();
        }

        if (fields!.OdometerKm is { } odometer && odometer < vehicle.OdometerKm)
        {
            errors.Add("odometerKm", $"The odometer never decreases; it reads {vehicle.OdometerKm:0.0} km.");
            return FleetProblems.Validation(errors);
        }

        var now = clock.UtcNow;
        vehicle.Equipment.NameAr = fields.NameAr!;
        vehicle.Equipment.NameEn = fields.NameEn!;
        vehicle.Equipment.TypeName = fields.Type.ToString();
        vehicle.Equipment.SerialNo = fields.Plate;
        vehicle.Equipment.Ownership = fields.Ownership;
        vehicle.Equipment.Condition = fields.Condition;
        vehicle.Equipment.Status = fields.Status;
        vehicle.Equipment.Notes = fields.Notes;
        vehicle.Equipment.UpdatedAt = now;
        vehicle.Equipment.UpdatedBy = caller.UserId;
        vehicle.Plate = fields.Plate;
        vehicle.Type = fields.Type;
        vehicle.ModelYear = fields.ModelYear;
        vehicle.FuelType = fields.FuelType;
        if (fields.OdometerKm is { } reading && reading > vehicle.OdometerKm)
        {
            vehicle.OdometerKm = reading;
            vehicle.OdometerUpdatedAt = now;
            vehicle.OdometerUpdatedBy = caller.UserId;
        }

        vehicle.GpsProvider = fields.GpsProvider;
        vehicle.GpsDeviceId = fields.GpsDeviceId;
        vehicle.GpsStatus = fields.GpsProvider is null ? VehicleGpsStatus.NotConfigured : vehicle.GpsStatus == VehicleGpsStatus.NotConfigured ? VehicleGpsStatus.Configured : vehicle.GpsStatus;
        vehicle.Notes = fields.Notes;
        vehicle.UpdatedAt = now;
        vehicle.UpdatedBy = caller.UserId;
        db.Entry(vehicle).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, VehicleConfiguration.PlateIndex))
        {
            return FleetProblems.PlateAlreadyUsed();
        }

        log.VehicleUpdated(caller.UserId, vehicle.Id);
        return TypedResults.Ok((await views.LoadAsync(vehicle.Id, caller, cancellationToken))!);
    }

    internal static async Task<Results<Ok<VehicleView>, ValidationProblem, ProblemHttpResult>> OdometerAsync(
        int vehicleId, OdometerRequest request, HttpContext httpContext, FleetDbContext db, VehicleViews views, CompanyClock clock, FleetLog log, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var reading = VehicleFields.Odometer(request.OdometerKm, errors, required: true);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var vehicle = await db.Vehicles.SingleOrDefaultAsync(candidate => candidate.Id == vehicleId, cancellationToken);
        if (vehicle is null)
        {
            return FleetProblems.VehicleNotFound();
        }

        if (reading < vehicle.OdometerKm)
        {
            errors.Add("odometerKm", $"The odometer never decreases; it reads {vehicle.OdometerKm:0.0} km.");
            return FleetProblems.Validation(errors);
        }

        vehicle.OdometerKm = reading!.Value;
        vehicle.OdometerUpdatedAt = clock.UtcNow;
        vehicle.OdometerUpdatedBy = caller.UserId;
        vehicle.UpdatedAt = clock.UtcNow;
        vehicle.UpdatedBy = caller.UserId;
        db.Entry(vehicle).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        log.VehicleUpdated(caller.UserId, vehicle.Id);
        return TypedResults.Ok((await views.LoadAsync(vehicle.Id, caller, cancellationToken))!);
    }

    internal static async Task<Results<Ok<DriversResponse>, ProblemHttpResult>> ListDriversAsync(
        int vehicleId, HttpContext httpContext, FleetDbContext db, IEmployeeDirectory employees, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        if (!await db.Vehicles.AnyAsync(vehicle => vehicle.Id == vehicleId, cancellationToken))
        {
            return FleetProblems.VehicleNotFound();
        }

        var rows = await db.VehicleDriverAssignments.AsNoTracking().Where(assignment => assignment.VehicleId == vehicleId)
            .OrderByDescending(assignment => assignment.StartDate).ThenByDescending(assignment => assignment.Id).ToListAsync(cancellationToken);
        var drivers = await employees.FindAsync(rows.Select(row => row.DriverEmployeeId).Distinct().ToList(), cancellationToken);
        return TypedResults.Ok(new DriversResponse(rows.Select(row => DriverAssignmentView.From(row, drivers, caller.SeesVehicleMoney)).ToList()));
    }

    /// <summary>Under the vehicle's lock: one driver at a time, stays never overlap, the driver is an active employee.</summary>
    internal static async Task<Results<Created<DriverAssignmentView>, ValidationProblem, ProblemHttpResult>> AssignDriverAsync(
        int vehicleId, AssignDriverRequest request, HttpContext httpContext, FleetDbContext db, IEmployeeDirectory employees, CompanyClock clock, FleetLog log, CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        if (request.DriverEmployeeId is null or < 1)
        {
            errors.Add("driverEmployeeId", "driverEmployeeId is required.");
        }

        var startDate = Input.OptionalDate(request.StartDate, errors, "startDate", "The start date") ?? clock.Today;
        if (startDate > clock.Today)
        {
            errors.Add("startDate", "The start date can't be in the future.");
        }

        var startOdometer = VehicleFields.Odometer(request.StartOdometerKm, errors, required: false, "startOdometerKm");
        var notes = Input.OptionalText(request.Notes, VehicleDriverAssignmentConfiguration.NotesMaxLength, errors, "notes", "Notes");
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        var driver = (await employees.FindAsync([request.DriverEmployeeId!.Value], cancellationToken)).GetValueOrDefault(request.DriverEmployeeId.Value);
        if (driver is not { IsActive: true })
        {
            errors.Add("driverEmployeeId", "The driver must be an active employee.");
            return FleetProblems.Validation(errors);
        }

        if (!await db.Vehicles.AnyAsync(vehicle => vehicle.Id == vehicleId, cancellationToken))
        {
            return FleetProblems.VehicleNotFound();
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Equipment(vehicleId), cancellationToken))
        {
            return FleetProblems.TryAgainLater();
        }

        if (await db.VehicleDriverAssignments.AnyAsync(assignment => assignment.VehicleId == vehicleId && assignment.EndDate == null, cancellationToken))
        {
            return FleetProblems.DriverAlreadyAssigned();
        }

        var lastEnd = await db.VehicleDriverAssignments.Where(assignment => assignment.VehicleId == vehicleId && assignment.EndDate != null)
            .MaxAsync(assignment => (DateOnly?)assignment.EndDate, cancellationToken);
        if (lastEnd is { } end && startDate < end)
        {
            errors.Add("startDate", $"The vehicle had a driver until {end:yyyy-MM-dd}; the start date can't be before that.");
            return FleetProblems.Validation(errors);
        }

        var assignment = new VehicleDriverAssignment
        {
            VehicleId = vehicleId,
            DriverEmployeeId = driver.Id,
            StartDate = startDate,
            StartOdometerKm = startOdometer,
            Notes = notes,
            AssignedAt = clock.UtcNow,
            AssignedBy = caller.UserId,
        };
        db.VehicleDriverAssignments.Add(assignment);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (SqlViolations.IsUnique(exception, VehicleDriverAssignmentConfiguration.OpenIndex))
        {
            return FleetProblems.DriverAlreadyAssigned();
        }

        await transaction.CommitAsync(cancellationToken);
        log.DriverAssigned(caller.UserId, vehicleId, driver.Id, assignment.Id);
        return TypedResults.Created($"/api/vehicles/{vehicleId}/drivers", DriverAssignmentView.From(assignment, new Dictionary<int, EmployeeRef> { [driver.Id] = driver }, caller.SeesVehicleMoney));
    }

    /// <summary>
    /// The hand-over: the stay ends, the custody balance and the claims still pending are written on it. The custody
    /// stays with the vehicle (it is never the driver's wallet); claims already made keep their driver.
    /// </summary>
    internal static async Task<Results<Ok<DriverAssignmentView>, ValidationProblem, ProblemHttpResult>> EndDriverAsync(
        int vehicleId, int assignmentId, EndDriverRequest request, HttpContext httpContext, FleetDbContext db, IEmployeeDirectory employees, CompanyClock clock, FleetLog log,
        CancellationToken cancellationToken)
    {
        if (FleetCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        var endDate = Input.OptionalDate(request.EndDate, errors, "endDate", "The end date") ?? clock.Today;
        if (endDate > clock.Today)
        {
            errors.Add("endDate", "The end date can't be in the future.");
        }

        var endOdometer = VehicleFields.Odometer(request.EndOdometerKm, errors, required: false, "endOdometerKm");
        var note = Input.OptionalText(request.HandoverNote, VehicleDriverAssignmentConfiguration.NotesMaxLength, errors, "handoverNote", "The hand-over note");
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);
        if (errors.HasErrors)
        {
            return FleetProblems.Validation(errors);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AppLock.TryAcquireAsync(db, AppLock.Equipment(vehicleId), cancellationToken))
        {
            return FleetProblems.TryAgainLater();
        }

        var assignment = await db.VehicleDriverAssignments.SingleOrDefaultAsync(candidate => candidate.Id == assignmentId && candidate.VehicleId == vehicleId, cancellationToken);
        if (assignment is null)
        {
            return FleetProblems.DriverAssignmentNotFound();
        }

        if (assignment.EndDate is not null)
        {
            return FleetProblems.WrongStatus("This driver assignment has already ended.");
        }

        if (endDate < assignment.StartDate)
        {
            errors.Add("endDate", $"The end date can't be before the start date ({assignment.StartDate:yyyy-MM-dd}).");
            return FleetProblems.Validation(errors);
        }

        var vehicle = await db.Vehicles.SingleAsync(candidate => candidate.Id == vehicleId, cancellationToken);
        if (endOdometer is { } reading && reading < vehicle.OdometerKm)
        {
            errors.Add("endOdometerKm", $"The odometer never decreases; it reads {vehicle.OdometerKm:0.0} km.");
            return FleetProblems.Validation(errors);
        }

        var figures = await VehicleQueries.FiguresAsync(db, vehicleId, cancellationToken);
        var now = clock.UtcNow;
        assignment.EndDate = endDate;
        assignment.EndOdometerKm = endOdometer;
        assignment.HandoverNote = note;
        assignment.HandoverCustodyBalance = figures.Posted;
        assignment.HandoverPendingClaims = figures.Pending;
        assignment.EndedAt = now;
        assignment.EndedBy = caller.UserId;
        db.Entry(assignment).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;
        if (endOdometer is { } trusted && trusted > vehicle.OdometerKm)
        {
            vehicle.OdometerKm = trusted;
            vehicle.OdometerUpdatedAt = now;
            vehicle.OdometerUpdatedBy = caller.UserId;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }

        await transaction.CommitAsync(cancellationToken);
        log.DriverAssignmentEnded(caller.UserId, vehicleId, assignment.Id);
        var drivers = await employees.FindAsync([assignment.DriverEmployeeId], cancellationToken);
        return TypedResults.Ok(DriverAssignmentView.From(assignment, drivers, caller.SeesVehicleMoney));
    }

    internal static async Task<Results<Ok<CustodyResponse>, ProblemHttpResult>> CustodyAsync(
        int vehicleId, HttpContext httpContext, FleetDbContext db, VehicleViews views, IEmployeeDirectory employees, CancellationToken cancellationToken)
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

        // The custody of a vehicle is read by whoever sees vehicle money, or by the person driving it now.
        if (!caller.SeesVehicleMoney && !await views.IsCurrentDriverAsync(vehicleId, caller, cancellationToken))
        {
            return FleetProblems.VehicleNotFound();
        }

        var entries = await VehicleQueries.CustodyEntries(db, vehicleId).OrderBy(entry => entry.EntryDate).ThenBy(entry => entry.Id).ToListAsync(cancellationToken);
        var current = await VehicleQueries.CurrentDriver(db, vehicleId).SingleOrDefaultAsync(cancellationToken);
        var driverIds = entries.Select(entry => entry.DriverEmployeeId).Append(current?.DriverEmployeeId).OfType<int>().Distinct().ToList();
        var drivers = driverIds.Count == 0 ? new Dictionary<int, EmployeeRef>() : await employees.FindAsync(driverIds, cancellationToken);

        decimal balance = 0;
        var lines = new List<VehicleCustodyLineView>();
        foreach (var entry in entries)
        {
            if (entry.Status == VehicleCustodyStatus.Posted)
            {
                balance += VehicleCustodyBalance.Signed(entry);
            }

            lines.Add(new VehicleCustodyLineView(
                entry.Id, entry.EntryDate, entry.Kind.ToString(), entry.Status.ToString(), VehicleCustodyBalance.Signed(entry), balance, entry.SourceType, entry.SourceId,
                DriverRef.From(entry.DriverEmployeeId, drivers), entry.Reference, entry.Notes, entry.CreatedAt, entry.CreatedBy));
        }

        return TypedResults.Ok(new CustodyResponse(vehicleId, vehicle.Plate, VehicleCustodyBalance.Compute(entries), DriverRef.From(current?.DriverEmployeeId, drivers), lines));
    }
}

/// <summary>The vehicle form after validation.</summary>
internal sealed partial record VehicleFields(
    string? AssetNo, string? NameAr, string? NameEn, string Plate, VehicleType Type, int? ModelYear, VehicleFuelType FuelType, EquipmentOwnership Ownership,
    EquipmentCondition Condition, EquipmentStatus Status, decimal? OdometerKm, string? GpsProvider, string? GpsDeviceId, string? Notes)
{
    public static VehicleFields? Validate(VehicleEndpoints.VehicleRequest request, ValidationErrors errors, bool forCreate)
    {
        string? assetNo = null;
        if (forCreate)
        {
            assetNo = Input.Clean(request.AssetNo)?.ToUpperInvariant();
            if (assetNo is null || assetNo.Length is < 2 or > EquipmentConfiguration.AssetNoMaxLength || !AssetNoPattern().IsMatch(assetNo))
            {
                errors.Add("assetNo", $"The asset number must be 2 to {EquipmentConfiguration.AssetNoMaxLength} letters or digits, in groups joined by single hyphens, for example VH-012.");
            }
        }

        var nameAr = Input.RequiredText(request.NameAr, EquipmentConfiguration.NameMaxLength, errors, "nameAr", "The Arabic name");
        var nameEn = Input.RequiredText(request.NameEn, EquipmentConfiguration.NameMaxLength, errors, "nameEn", "The English name");
        var plate = Input.RequiredText(request.Plate, VehicleConfiguration.PlateMaxLength, errors, "plate", "The plate")?.ToUpperInvariant();
        var type = Input.RequiredName<VehicleType>(request.Type, errors, "type");
        var fuel = Input.RequiredName<VehicleFuelType>(request.FuelType, errors, "fuelType");
        var ownership = Input.OptionalName<EquipmentOwnership>(request.Ownership, errors, "ownership") ?? EquipmentOwnership.Owned;
        var condition = Input.OptionalName<EquipmentCondition>(request.Condition, errors, "condition") ?? EquipmentCondition.Good;
        var status = Input.OptionalName<EquipmentStatus>(request.Status, errors, "status") ?? EquipmentStatus.InService;
        if (request.ModelYear is { } year && year is < 1950 or > 2100)
        {
            errors.Add("modelYear", "modelYear must be between 1950 and 2100.");
        }

        var odometer = Odometer(request.OdometerKm, errors, required: false);
        var provider = Input.OptionalText(request.GpsProvider, VehicleConfiguration.ProviderMaxLength, errors, "gpsProvider", "The GPS provider");
        var device = Input.OptionalText(request.GpsDeviceId, VehicleConfiguration.DeviceMaxLength, errors, "gpsDeviceId", "The GPS device id");
        if ((provider is null) != (device is null))
        {
            errors.Add("gpsDeviceId", "A GPS provider and a device id go together.");
        }

        var notes = Input.OptionalText(request.Notes, VehicleConfiguration.NotesMaxLength, errors, "notes", "Notes");
        return errors.HasErrors ? null : new VehicleFields(assetNo, nameAr, nameEn, plate!, type!.Value, request.ModelYear, fuel!.Value, ownership, condition, status, odometer, provider, device, notes);
    }

    /// <summary>An odometer reading in kilometres: zero or more, at most one decimal, below a million.</summary>
    public static decimal? Odometer(decimal? value, ValidationErrors errors, bool required, string field = "odometerKm")
    {
        if (value is null)
        {
            if (required)
            {
                errors.Add(field, $"{field} is required.");
            }

            return null;
        }

        if (value < 0 || value > 9_999_999 || decimal.Round(value.Value, 1) != value.Value)
        {
            errors.Add(field, $"{field} must be between 0 and 9,999,999 km with at most one decimal.");
            return null;
        }

        return value;
    }

    [GeneratedRegex("^[A-Z0-9]+(-[A-Z0-9]+)*$")]
    private static partial Regex AssetNoPattern();
}
