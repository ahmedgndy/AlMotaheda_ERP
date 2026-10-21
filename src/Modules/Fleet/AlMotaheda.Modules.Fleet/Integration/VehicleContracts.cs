using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Fleet.Common;
using AlMotaheda.Modules.Fleet.Data;
using AlMotaheda.Modules.Fleet.Data.Configurations;
using AlMotaheda.Modules.Fleet.Features.Vehicles;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace AlMotaheda.Modules.Fleet.Integration;

/// <summary>
/// A vehicle's operating custody as Finance's hierarchy sees it (<see cref="ICustodyLedgerSource"/>, kind Vehicle):
/// a received transfer is a funding of <c>fleet.VehicleCustodyEntry</c>, a return or a reversal a withdrawal, each
/// naming the driver accountable on that day from the assignment history. In Finance's transaction, under the
/// vehicle's lock. One ledger per vehicle: nothing is kept twice.
/// </summary>
internal sealed class VehicleCustodyLedgerSource(
    DbContextOptions<FleetDbContext> options,
    FleetDbContext db,
    IConfiguration configuration,
    CompanyClock clock,
    FleetLog log) : ICustodyLedgerSource
{
    public string Kind => CustodyAccountKinds.Vehicle;

    public async Task<CustodyTarget?> FindAsync(int referenceId, int? holderUserId, CancellationToken cancellationToken)
    {
        if (holderUserId is not null)
        {
            return null;
        }

        var vehicle = await db.Vehicles.AsNoTracking().Include(candidate => candidate.Equipment).SingleOrDefaultAsync(candidate => candidate.Id == referenceId, cancellationToken);
        if (vehicle is null)
        {
            return null;
        }

        var projectId = await VehicleQueries.ProjectOn(db, referenceId, clock.Today).FirstOrDefaultAsync(cancellationToken);
        return new CustodyTarget(vehicle.Id, null, $"{vehicle.Plate} {vehicle.Equipment.NameAr}", $"{vehicle.Plate} {vehicle.Equipment.NameEn}", projectId == 0 ? null : projectId);
    }

    public async Task<CustodyLedgerFigures> FiguresAsync(int referenceId, int? holderUserId, CancellationToken cancellationToken)
    {
        var figures = await VehicleQueries.FiguresAsync(db, referenceId, cancellationToken);
        return new CustodyLedgerFigures(figures.Posted, figures.Pending);
    }

    /// <summary>A claim always carries its odometer photo and receipt, so a posted expense is never unsupported here.</summary>
    public async Task<CustodyActivity> ActivityAsync(int referenceId, int? holderUserId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var entries = await VehicleQueries.CustodyEntries(db, referenceId)
            .Select(entry => new { entry.EntryDate, entry.Kind, entry.Amount, entry.Status }).ToListAsync(cancellationToken);
        var posted = entries.Where(entry => entry.Status == VehicleCustodyStatus.Posted && entry.EntryDate >= from && entry.EntryDate <= to).ToList();
        var pending = entries.Where(entry => entry.Status == VehicleCustodyStatus.Pending).ToList();
        return new CustodyActivity(
            posted.Where(entry => entry.Kind is VehicleCustodyKind.Funding or VehicleCustodyKind.AdjustmentIn).Sum(entry => entry.Amount),
            posted.Where(entry => entry.Kind == VehicleCustodyKind.Return).Sum(entry => entry.Amount),
            0m,
            posted.Where(entry => entry.Kind is VehicleCustodyKind.Expense or VehicleCustodyKind.AdjustmentOut).Sum(entry => entry.Amount),
            pending.Sum(entry => entry.Amount),
            pending.Count,
            0m,
            0,
            entries.Count == 0 ? null : entries.Max(entry => entry.EntryDate));
    }

    public async Task<CustodyStatementPage> StatementAsync(int referenceId, int? holderUserId, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = VehicleQueries.CustodyEntries(db, referenceId);
        if (from is { } first)
        {
            query = query.Where(entry => entry.EntryDate >= first);
        }

        if (to is { } last)
        {
            query = query.Where(entry => entry.EntryDate <= last);
        }

        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(entry => entry.EntryDate).ThenByDescending(entry => entry.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new CustodyStatementPage(
            rows.Select(entry => new CustodyStatementLine(
                entry.EntryDate, entry.Kind.ToString(), entry.Status.ToString(), VehicleCustodyBalance.Signed(entry), entry.SourceType, entry.SourceId,
                entry.Reference, entry.Notes, true, entry.CreatedBy, null)).ToList(),
            total);
    }

    public Task ReceiveAsync(CustodyLedgerWrite write, SharedTransaction transaction, CancellationToken cancellationToken) =>
        WriteAsync(write, cashIn: true, transaction, cancellationToken);

    public Task WithdrawAsync(CustodyLedgerWrite write, SharedTransaction transaction, CancellationToken cancellationToken) =>
        WriteAsync(write, cashIn: false, transaction, cancellationToken);

    private async Task WriteAsync(CustodyLedgerWrite write, bool cashIn, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        if (write.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(write), "A vehicle custody movement is above zero.");
        }

        await using var joined = await transaction.JoinAsync(options, FleetDatabase.Configure, joinedOptions => new FleetDbContext(joinedOptions), cancellationToken);
        if (!await AppLock.TryAcquireAsync(joined, AppLock.Equipment(write.ReferenceId), cancellationToken))
        {
            throw new CustodyLedgerRefusedException(CustodyLedgerRefusedException.Busy, "This vehicle's custody is being updated by someone else. Try again in a moment.");
        }

        if (!await joined.Vehicles.AnyAsync(vehicle => vehicle.Id == write.ReferenceId, cancellationToken))
        {
            throw new ArgumentException($"Vehicle {write.ReferenceId} doesn't exist.", nameof(write));
        }

        // The company's one overdraft rule (Projects:CustodyBalanceCheck): Block refuses a withdrawal above what the vehicle holds.
        if (!cashIn && string.Equals(configuration["Projects:CustodyBalanceCheck"], CustodyBalancePolicies.Block, StringComparison.Ordinal))
        {
            var figures = await VehicleQueries.FiguresAsync(joined, write.ReferenceId, cancellationToken);
            if (write.Amount > figures.Posted - figures.Pending)
            {
                throw new CustodyLedgerRefusedException(CustodyLedgerRefusedException.Insufficient, $"The vehicle's custody has {Math.Max(figures.Posted - figures.Pending, 0):0.00} available, less than {write.Amount:0.00}.");
            }
        }

        var driver = await VehicleQueries.DriverOn(joined, write.ReferenceId, write.EntryDate).Select(assignment => (int?)assignment.DriverEmployeeId).FirstOrDefaultAsync(cancellationToken);
        var adjustment = write.SourceType == CustodyLedgerSources.Adjustment;
        var entry = new VehicleCustodyEntry
        {
            VehicleId = write.ReferenceId,
            DriverEmployeeId = driver,
            Kind = adjustment ? (cashIn ? VehicleCustodyKind.AdjustmentIn : VehicleCustodyKind.AdjustmentOut) : cashIn ? VehicleCustodyKind.Funding : VehicleCustodyKind.Return,
            Amount = write.Amount,
            EntryDate = write.EntryDate,
            Status = VehicleCustodyStatus.Posted,
            SourceType = write.SourceType == CustodyLedgerSources.TransferReversal ? VehicleCustodySources.TransferReversal : write.SourceType == CustodyLedgerSources.Adjustment ? VehicleCustodySources.Adjustment : VehicleCustodySources.Transfer,
            SourceId = write.SourceId,
            Reference = Clip(write.Reference, VehicleCustodyEntryConfiguration.ReferenceMaxLength),
            Notes = Clip(write.Notes, VehicleCustodyEntryConfiguration.NotesMaxLength),
            CreatedAt = clock.UtcNow,
            CreatedBy = write.ActorUserId,
        };
        joined.VehicleCustodyEntries.Add(entry);
        await joined.SaveChangesAsync(cancellationToken);
        log.VehicleCustodyWritten(write.ActorUserId, write.ReferenceId, entry.Id, entry.Kind.ToString(), entry.SourceType, entry.SourceId);
    }

    private static string? Clip(string? text, int maxLength) => text is null || text.Length <= maxLength ? text : text[..maxLength];
}

/// <summary>
/// Told by Projects when the Executive decides a price case on a vehicle expense claim. The claim itself stays
/// where it is: approval of the price never approves or pays the claim; the reviewer approves it on its own
/// screen, where the gate reads the case's state. Nothing to write here but the log.
/// </summary>
internal sealed class VehicleExpensePriceExceptionSource(FleetLog log) : IPriceExceptionSource
{
    public string SourceType => PriceExceptionSources.VehicleExpense;

    public Task OnDecidedAsync(PriceExceptionDecision decision, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        log.ClaimReviewed(decision.DecidedBy, decision.SourceId, $"PriceCase{decision.Status}");
        return Task.CompletedTask;
    }
}
