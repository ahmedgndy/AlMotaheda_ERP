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

/// <summary>
/// The engineer's project custody as Finance's custody hierarchy sees it (<see cref="ICustodyLedgerSource"/>, kind
/// EngineerProject): the money transferred down from the accountant, or back up, is written here as a funding or a
/// return of the same <c>projects.CustodyEntry</c> ledger the purchases and weekly payroll draw from, in Finance's
/// transaction and under the custody's lock. One ledger per custody: nothing is kept twice.
/// </summary>
internal sealed class ProjectCustodyLedgerSource(
    DbContextOptions<ProjectsDbContext> options,
    ProjectsDbContext db,
    IOptions<ProjectsOptions> settings,
    IEnumerable<IRecordDocuments> documents,
    CompanyClock clock,
    ProjectsLog log) : ICustodyLedgerSource
{
    public string Kind => CustodyAccountKinds.EngineerProject;

    /// <summary>
    /// The custody's period figures for the central custody page. Unsupported = approved purchases and expenses
    /// paid from the custody with no document linked in the Archive (the Archive answers; without it nothing is
    /// flagged). Wages are supported by their approved sheets and the settlement's review.
    /// </summary>
    public async Task<CustodyActivity> ActivityAsync(int referenceId, int? holderUserId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        if (holderUserId is not { } holder)
        {
            return CustodyActivity.Empty;
        }

        var movements = (await CustodyQueries.MovementsAsync(db, referenceId, holder, cancellationToken)).Select(row => row.Movement).ToList();
        var evidence = await EvidenceAsync(movements, cancellationToken);
        var inPeriod = movements.Where(movement => !movement.Pending && movement.Date >= from && movement.Date <= to && movement.Status != nameof(CustodyEntryStatus.Released)).ToList();
        var spent = inPeriod.Where(movement => movement.Kind is null or CustodyEntryKind.Payroll or CustodyEntryKind.AdjustmentOut).ToList();
        var unsupported = spent.Where(movement => movement.Kind is null && !evidence.Contains((movement.SourceType, movement.SourceId))).ToList();
        var pending = movements.Where(movement => movement.Pending).ToList();
        return new CustodyActivity(
            inPeriod.Where(movement => movement.Kind is CustodyEntryKind.Funding or CustodyEntryKind.AdjustmentIn).Sum(movement => movement.Amount),
            inPeriod.Where(movement => movement.Kind == CustodyEntryKind.Return).Sum(movement => movement.Amount),
            0m,
            spent.Sum(movement => movement.Amount),
            pending.Sum(movement => movement.Amount),
            pending.Count,
            unsupported.Sum(movement => movement.Amount),
            unsupported.Count,
            movements.Count == 0 ? null : movements.Max(movement => movement.Date));
    }

    public async Task<CustodyStatementPage> StatementAsync(int referenceId, int? holderUserId, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken cancellationToken)
    {
        if (holderUserId is not { } holder)
        {
            return new CustodyStatementPage([], 0);
        }

        var movements = (await CustodyQueries.MovementsAsync(db, referenceId, holder, cancellationToken)).Select(row => row.Movement)
            .Where(movement => (from == null || movement.Date >= from) && (to == null || movement.Date <= to))
            .OrderByDescending(movement => movement.Date).ThenByDescending(movement => movement.RecordedAt)
            .ToList();
        var pageRows = movements.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        var evidence = await EvidenceAsync(pageRows, cancellationToken);
        return new CustodyStatementPage(
            pageRows.Select(movement => new CustodyStatementLine(
                movement.Date, movement.Type, movement.Status, movement.Kind is { } kind && CustodyEntryKinds.IsCashIn(kind) ? movement.Amount : -movement.Amount,
                movement.SourceType, movement.SourceId, null, movement.Description,
                movement.Kind is not null || evidence.Contains((movement.SourceType, movement.SourceId)), movement.RecordedBy, referenceId)).ToList(),
            movements.Count);
    }

    /// <summary>The (source type, id) pairs among the purchases and expenses that have a document in the Archive.</summary>
    private async Task<HashSet<(string, int)>> EvidenceAsync(IReadOnlyList<Features.Custody.CustodyMovement> movements, CancellationToken cancellationToken)
    {
        var supported = new HashSet<(string, int)>();
        var expenses = movements.Where(movement => movement.Kind is null && movement.SourceType == nameof(ProjectExpense)).Select(movement => movement.SourceId).Distinct().ToList();
        if (expenses.Count == 0 || documents.FirstOrDefault() is not { } archive)
        {
            return supported;
        }

        foreach (var id in (await archive.CountAsync("Expense", expenses, cancellationToken)).Keys)
        {
            supported.Add((nameof(ProjectExpense), id));
        }

        return supported;
    }

    public async Task<CustodyTarget?> FindAsync(int referenceId, int? holderUserId, CancellationToken cancellationToken)
    {
        if (holderUserId is not { } holder)
        {
            return null;
        }

        var project = await db.Projects.AsNoTracking()
            .Where(candidate => candidate.Id == referenceId)
            .Select(candidate => new { candidate.Id, candidate.Code, candidate.NameAr, candidate.NameEn })
            .SingleOrDefaultAsync(cancellationToken);
        if (project is null)
        {
            return null;
        }

        var member = await db.ProjectMembers.AsNoTracking().AnyAsync(row => row.ProjectId == referenceId && row.UserId == holder && row.EndDate == null, cancellationToken);
        return member ? new CustodyTarget(project.Id, holder, $"{project.Code} {project.NameAr}", $"{project.Code} {project.NameEn}", project.Id) : null;
    }

    public async Task<CustodyLedgerFigures> FiguresAsync(int referenceId, int? holderUserId, CancellationToken cancellationToken)
    {
        var figures = CustodyBalance.Compute((await CustodyQueries.MovementsAsync(db, referenceId, holderUserId, cancellationToken)).Select(row => row.Movement));
        return new CustodyLedgerFigures(figures.Balance, figures.SpentPending);
    }

    public Task ReceiveAsync(CustodyLedgerWrite write, SharedTransaction transaction, CancellationToken cancellationToken) =>
        WriteAsync(write, cashIn: true, transaction, cancellationToken);

    public Task WithdrawAsync(CustodyLedgerWrite write, SharedTransaction transaction, CancellationToken cancellationToken) =>
        WriteAsync(write, cashIn: false, transaction, cancellationToken);

    private async Task WriteAsync(CustodyLedgerWrite write, bool cashIn, SharedTransaction transaction, CancellationToken cancellationToken)
    {
        if (write.Amount <= 0 || write.HolderUserId is not { } holder)
        {
            throw new ArgumentOutOfRangeException(nameof(write), "A project custody movement is above zero and names its holder.");
        }

        await using var joined = await transaction.JoinAsync(options, ProjectsDatabase.Configure, joinedOptions => new ProjectsDbContext(joinedOptions), cancellationToken);
        if (!await CustodyLock.TryAcquireAsync(joined, write.ReferenceId, holder, cancellationToken))
        {
            throw new CustodyLedgerRefusedException(CustodyLedgerRefusedException.Busy, "This custody is being updated by someone else. Try again in a moment.");
        }

        if (!cashIn && settings.Value.BlocksCustodyOverdraft)
        {
            var balance = await CustodyQueries.BalanceAsync(joined, write.ReferenceId, holder, cancellationToken);
            if (write.Amount > balance)
            {
                throw new CustodyLedgerRefusedException(CustodyLedgerRefusedException.Insufficient, $"The custody holds {Math.Max(balance, 0):0.00}, less than {write.Amount:0.00}.");
            }
        }

        var adjustment = write.SourceType == CustodyLedgerSources.Adjustment;
        var entry = new CustodyEntry
        {
            ProjectId = write.ReferenceId,
            HolderUserId = holder,
            Kind = adjustment ? (cashIn ? CustodyEntryKind.AdjustmentIn : CustodyEntryKind.AdjustmentOut) : cashIn ? CustodyEntryKind.Funding : CustodyEntryKind.Return,
            Amount = write.Amount,
            EntryDate = write.EntryDate,
            Method = PaymentMethods.Cash,
            Reference = Clip(write.Reference, CustodyEntryConfiguration.ReferenceMaxLength),
            Notes = Clip(write.Notes, CustodyEntryConfiguration.TextMaxLength),
            Status = CustodyEntryStatus.Posted,
            SourceType = write.SourceType,
            SourceId = write.SourceId,
            CreatedAt = clock.UtcNow,
            CreatedBy = write.ActorUserId,
        };
        joined.CustodyEntries.Add(entry);
        await joined.SaveChangesAsync(cancellationToken);
        log.CustodyTransferWritten(write.ActorUserId, write.ReferenceId, entry.Id, holder, write.SourceType, write.SourceId, entry.Kind.ToString());
    }

    private static string? Clip(string? text, int maxLength) => text is null || text.Length <= maxLength ? text : text[..maxLength];
}
