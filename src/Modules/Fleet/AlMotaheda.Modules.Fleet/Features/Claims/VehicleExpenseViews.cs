using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Features.Attachments;
using AlMotaheda.Modules.Fleet.Features.Vehicles;
using AlMotaheda.Modules.Fleet.Security;
using AlMotaheda.Modules.HR.Contracts;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Fleet.Features.Claims;

internal sealed record ClaimFlagView(int Id, string Kind, string Detail, int? RelatedExpenseId);

/// <summary>
/// A claim as the screens show it. Amounts are <c>null</c> for a caller who may not see money (and is not its claimant).
/// <c>SubmittedAt</c> is when the server received it; <c>CapturedAt</c> (camera fuel claims) when its photos were taken.
/// </summary>
internal sealed record VehicleExpenseView(
    int Id,
    string Number,
    int VehicleId,
    string Plate,
    string VehicleNameAr,
    string VehicleNameEn,
    DriverRef Driver,
    int? DriverAssignmentId,
    int? ProjectId,
    string Type,
    DateOnly ExpenseDate,
    decimal? OdometerKm,
    decimal? FuelQuantity,
    decimal? UnitPrice,
    decimal? Amount,
    string? VendorName,
    int? SupplierId,
    string? InvoiceNo,
    string? Notes,
    int? PriceItemId,
    string? PriceCheck,
    decimal? ReferenceMaxPrice,
    string? PriceJustification,
    int? PriceExceptionId,
    string? PriceExceptionStatus,
    decimal? GpsLatitude,
    decimal? GpsLongitude,
    DateTime? GpsCapturedAt,
    string Status,
    int Round,
    string PostingStatus,
    int? CustodyEntryId,
    DateTime? PostedAt,
    DateTime? ReversedAt,
    FleetPerson? ReversedBy,
    string? ReversalReason,
    int? HrReferralId,
    DateTime SubmittedAt,
    FleetPerson Submitter,
    DateTime? ReviewedAt,
    FleetPerson? Reviewer,
    string? ReviewNote,
    IReadOnlyList<ClaimFlagView> Flags,
    IReadOnlyList<FleetAttachmentView> Attachments,
    string RowVersion,
    DateTime? CapturedAt,
    string? AmountSource,
    decimal? OcrAmount,
    decimal? OcrLiters,
    decimal? OcrUnitPrice,
    decimal? OcrConfidence,
    decimal? EffectiveUnitPrice);

/// <summary>Builds claim views with one lookup per page for people, drivers and price cases.</summary>
internal sealed class VehicleExpenseViews(FleetDbContext db, IUserDirectory users, IEmployeeDirectory employees, IPriceControl priceControl)
{
    public async Task<VehicleExpenseView?> LoadAsync(int claimId, FleetCaller caller, int? callerEmployeeId, CancellationToken cancellationToken)
    {
        var claim = await db.VehicleExpenses.AsNoTracking().Include(candidate => candidate.Vehicle).ThenInclude(vehicle => vehicle.Equipment).Include(candidate => candidate.Flags)
            .SingleOrDefaultAsync(candidate => candidate.Id == claimId, cancellationToken);
        return claim is null ? null : (await FromAsync([claim], caller, callerEmployeeId, cancellationToken))[0];
    }

    public async Task<IReadOnlyList<VehicleExpenseView>> FromAsync(IReadOnlyList<VehicleExpense> claims, FleetCaller caller, int? callerEmployeeId, CancellationToken cancellationToken)
    {
        var people = await Lookups.UsersAsync(users, claims.SelectMany(claim => new[] { (int?)claim.CreatedBy, claim.ReviewedBy, claim.ReversedBy }), cancellationToken);
        var driverIds = claims.Select(claim => claim.DriverEmployeeId).Distinct().ToList();
        var drivers = driverIds.Count == 0 ? new Dictionary<int, EmployeeRef>() : await employees.FindAsync(driverIds, cancellationToken);
        var ids = claims.Select(claim => claim.Id).ToList();
        var attachments = await db.Attachments.AsNoTracking()
            .Where(attachment => attachment.OwnerType == FleetAttachmentOwners.VehicleExpense && attachment.OwnerId != null && ids.Contains(attachment.OwnerId.Value))
            .OrderBy(attachment => attachment.Id).ToListAsync(cancellationToken);
        var cases = await priceControl.StatesAsync(PriceExceptionSources.VehicleExpense, claims.Where(claim => claim.PriceExceptionId != null).Select(claim => claim.Id).ToList(), cancellationToken);

        return claims.Select(claim =>
        {
            var seesMoney = caller.SeesVehicleMoney || claim.CreatedBy == caller.UserId || claim.DriverEmployeeId == callerEmployeeId;
            return new VehicleExpenseView(
                claim.Id, claim.Number, claim.VehicleId, claim.Vehicle.Plate, claim.Vehicle.Equipment.NameAr, claim.Vehicle.Equipment.NameEn,
                DriverRef.From(claim.DriverEmployeeId, drivers)!, claim.DriverAssignmentId, claim.ProjectId, claim.Type.ToString(), claim.ExpenseDate, claim.OdometerKm,
                claim.FuelQuantity, seesMoney ? claim.UnitPrice : null, seesMoney ? claim.Amount : null, claim.VendorName, claim.SupplierId, claim.InvoiceNo, claim.Notes,
                claim.PriceItemId, claim.PriceCheck, seesMoney ? claim.ReferenceMaxPrice : null, claim.PriceJustification, claim.PriceExceptionId,
                cases.TryGetValue(claim.Id, out var state) ? state.Status : null,
                claim.GpsLatitude, claim.GpsLongitude, claim.GpsCapturedAt,
                claim.Status.ToString(), claim.Round, claim.PostingStatus.ToString(), claim.CustodyEntryId, claim.PostedAt, claim.ReversedAt,
                FleetPerson.FromOptional(claim.ReversedBy, people), claim.ReversalReason, claim.HrReferralId,
                claim.SubmittedAt, FleetPerson.From(claim.CreatedBy, people), claim.ReviewedAt, FleetPerson.FromOptional(claim.ReviewedBy, people), claim.ReviewNote,
                claim.Flags.OrderBy(flag => flag.Id).Select(flag => new ClaimFlagView(flag.Id, flag.Kind.ToString(), flag.Detail, flag.RelatedExpenseId)).ToList(),
                attachments.Where(attachment => attachment.OwnerId == claim.Id).Select(FleetAttachmentView.From).ToList(),
                RowVersions.Format(claim.RowVersion),
                claim.CapturedAt, claim.AmountSource?.ToString(), seesMoney ? claim.OcrAmount : null, claim.OcrLiters, seesMoney ? claim.OcrUnitPrice : null, claim.OcrConfidence,
                seesMoney ? claim.EffectiveUnitPrice : null);
        }).ToList();
    }
}
