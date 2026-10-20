using AlMotaheda.Modules.Projects.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AlMotaheda.Modules.Projects.Features.Custody;

/// <summary>The balance check of a purchase or expense paid from the recorder's custody.</summary>
/// <param name="Transaction">Open under policy Block (with the custody lock held): the caller commits it after its save.</param>
/// <param name="Overdrawn">The record is more than the custody holds (policy Warn: saved, the balance goes negative).</param>
internal sealed record CustodySpend(IDbContextTransaction? Transaction, ProblemHttpResult? Problem, bool Overdrawn)
{
    public static CustodySpend None { get; } = new(null, null, false);
}

internal static class CustodySpendCheck
{
    /// <summary>
    /// Under policy Block: opens the transaction, takes the custody lock and refuses an amount above the balance.
    /// Under Warn: only says whether the record overdraws the custody. On resubmit, the record's own earlier amount
    /// is left out of the balance (<paramref name="existingExpenseId"/>).
    /// </summary>
    public static async Task<CustodySpend> CheckAsync(
        ProjectsDbContext db, int projectId, int holderUserId, decimal amount, int? existingExpenseId, bool block, CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = null;
        if (block)
        {
            transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            if (!await CustodyLock.TryAcquireAsync(db, projectId, holderUserId, cancellationToken))
            {
                await transaction.DisposeAsync();
                return new CustodySpend(null, Common.ProjectProblems.TryAgainLater(), false);
            }
        }

        var movements = (await CustodyQueries.MovementsAsync(db, projectId, holderUserId, cancellationToken))
            .Select(row => row.Movement)
            .Where(movement => !(movement.SourceType == nameof(ProjectExpense) && movement.SourceId == existingExpenseId));
        var balance = CustodyBalance.Compute(movements).Balance;

        if (amount <= balance)
        {
            return new CustodySpend(transaction, null, false);
        }

        if (transaction is not null)
        {
            await transaction.DisposeAsync();
            return new CustodySpend(null, CustodyProblems.NotEnough(balance, amount), true);
        }

        return new CustodySpend(null, null, true);
    }
}
