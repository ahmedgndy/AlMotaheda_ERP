using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Finance.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Data.Configurations;
using AlMotaheda.Modules.Projects.Features.Custody;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Projects.Integration;

/// <summary>
/// The custody ledger other modules pay out of (Workforce's weekly payroll), in their own transaction and under the
/// custody's lock, so the balance check and the outflow can't be split by another movement.
/// </summary>
internal sealed class CustodyLedgerContract(
    DbContextOptions<ProjectsDbContext> options,
    ProjectsDbContext db,
    IOptions<ProjectsOptions> settings,
    CompanyClock clock,
    ProjectsLog log) : ICustodyLedger
{
    public Task<decimal> BalanceAsync(int projectId, int holderUserId, CancellationToken cancellationToken) =>
        CustodyQueries.BalanceAsync(db, projectId, holderUserId, cancellationToken);

    public Task<bool> CanHoldAsync(int projectId, int holderUserId, CancellationToken cancellationToken) =>
        db.ProjectMembers.AsNoTracking().AnyAsync(member => member.ProjectId == projectId && member.UserId == holderUserId && member.EndDate == null, cancellationToken);

    public async Task HoldAsync(CustodyOutflow outflow, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        if (outflow.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outflow), "A custody outflow is above zero.");
        }

        await using var joined = await JoinAsync(transaction, cancellationToken);
        if (!await CustodyLock.TryAcquireAsync(joined, outflow.ProjectId, outflow.HolderUserId, cancellationToken))
        {
            throw new CustodyRefusedException(CustodyRefusedException.Busy, "This custody is being updated by someone else. Try again in a moment.");
        }

        if (settings.Value.BlocksCustodyOverdraft)
        {
            var balance = await CustodyQueries.BalanceAsync(joined, outflow.ProjectId, outflow.HolderUserId, cancellationToken);
            if (outflow.Amount > balance)
            {
                throw new CustodyRefusedException(
                    CustodyRefusedException.Insufficient, $"The custody holds {Math.Max(balance, 0):0.00}, less than {outflow.Amount:0.00}.");
            }
        }

        joined.CustodyEntries.Add(new CustodyEntry
        {
            ProjectId = outflow.ProjectId,
            HolderUserId = outflow.HolderUserId,
            Kind = CustodyEntryKind.Payroll,
            Amount = outflow.Amount,
            EntryDate = outflow.EntryDate,
            Method = outflow.Method,
            Reference = outflow.Reference,
            Status = CustodyEntryStatus.Pending,
            SourceType = outflow.SourceType,
            SourceId = outflow.SourceId,
            CreatedAt = clock.UtcNow,
            CreatedBy = outflow.CreatedBy,
        });
        await joined.SaveChangesAsync(cancellationToken);
        log.CustodyOutflowHeld(outflow.CreatedBy, outflow.ProjectId, outflow.HolderUserId, outflow.SourceType, outflow.SourceId);
    }

    public Task PostAsync(string sourceType, int sourceId, SharedTransaction transaction, CancellationToken cancellationToken) =>
        MoveAsync(sourceType, sourceId, CustodyEntryStatus.Posted, transaction, cancellationToken);

    public Task ReleaseAsync(string sourceType, int sourceId, SharedTransaction transaction, CancellationToken cancellationToken) =>
        MoveAsync(sourceType, sourceId, CustodyEntryStatus.Released, transaction, cancellationToken);

    /// <summary>
    /// Pending → Posted (confirmed), Pending or Posted → Released (rejected or reversed). Anything else changes
    /// nothing, so a repeated confirm or reject of the same record is harmless.
    /// </summary>
    private async Task MoveAsync(string sourceType, int sourceId, CustodyEntryStatus to, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        await using var joined = await JoinAsync(transaction, cancellationToken);
        var entry = await joined.CustodyEntries.SingleOrDefaultAsync(entry => entry.SourceType == sourceType && entry.SourceId == sourceId, cancellationToken);
        var moves = entry?.Status switch
        {
            CustodyEntryStatus.Pending => true,
            CustodyEntryStatus.Posted => to == CustodyEntryStatus.Released,
            _ => false,
        };
        if (!moves)
        {
            return;
        }

        if (!await CustodyLock.TryAcquireAsync(joined, entry!.ProjectId, entry.HolderUserId, cancellationToken))
        {
            throw new CustodyRefusedException(CustodyRefusedException.Busy, "This custody is being updated by someone else. Try again in a moment.");
        }

        entry.Status = to;
        entry.UpdatedAt = clock.UtcNow;
        await joined.SaveChangesAsync(cancellationToken);
        log.CustodyOutflowMoved(entry.ProjectId, entry.Id, sourceType, sourceId, to.ToString());
    }

    private Task<ProjectsDbContext> JoinAsync(SharedTransaction transaction, CancellationToken cancellationToken) =>
        transaction.JoinAsync(options, ProjectsDatabase.Configure, joinedOptions => new ProjectsDbContext(joinedOptions), cancellationToken);
}

/// <summary>
/// Writes a custody funding when its Finance request is paid: the money left the company's cash or bank and is now in
/// the holder's hands. In Finance's transaction; the request is the funding's source, so it is written once.
/// </summary>
internal sealed class CustodyFundingSource(
    DbContextOptions<ProjectsDbContext> options,
    IUserDirectory users,
    CompanyClock clock,
    ProjectsLog log) : IPaymentSource
{
    /// <summary>The source type of a funding: the Finance request that paid it.</summary>
    public const string FundingSource = "PaymentRequest";

    public string SourceType => PaymentSourceTypes.CustodyFunding;

    public async Task<int> SettleAsync(PaymentSettlement settlement, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        var projectId = settlement.ProjectId ?? throw new PaymentNotPayableException("A custody belongs to one project.");

        await using var joined = await transaction.JoinAsync(options, ProjectsDatabase.Configure, joinedOptions => new ProjectsDbContext(joinedOptions), cancellationToken);

        if (!await CustodyLock.TryAcquireAsync(joined, projectId, settlement.PayeeId, cancellationToken))
        {
            throw new PaymentNotPayableException("The custody is busy. Try again in a moment.");
        }

        var entry = new CustodyEntry
        {
            ProjectId = projectId,
            HolderUserId = settlement.PayeeId,
            Kind = CustodyEntryKind.Funding,
            Amount = settlement.Amount,
            EntryDate = settlement.PaidOn,
            Method = settlement.Method,
            Reference = Clip(settlement.Reference ?? settlement.RequestNumber, CustodyEntryConfiguration.ReferenceMaxLength),
            Notes = Clip(settlement.Notes, CustodyEntryConfiguration.TextMaxLength),
            Status = CustodyEntryStatus.Posted,
            SourceType = FundingSource,
            SourceId = settlement.RequestId,
            CreatedAt = clock.UtcNow,
            CreatedBy = settlement.ConfirmedBy,
        };
        joined.CustodyEntries.Add(entry);
        await joined.SaveChangesAsync(cancellationToken);

        log.CustodyFunded(settlement.ConfirmedBy, projectId, entry.Id, settlement.PayeeId, settlement.RequestId);
        return entry.Id;
    }

    public async Task<IReadOnlyDictionary<int, PayeeName>> PayeeNamesAsync(IReadOnlyCollection<int> payeeIds, CancellationToken cancellationToken) =>
        (await users.FindAsync(payeeIds, cancellationToken)).ToDictionary(pair => pair.Key, pair => new PayeeName(pair.Value.NameAr, pair.Value.NameEn));

    private static string? Clip(string? text, int maxLength) => text is null || text.Length <= maxLength ? text : text[..maxLength];
}
