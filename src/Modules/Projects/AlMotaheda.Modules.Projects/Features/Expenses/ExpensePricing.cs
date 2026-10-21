using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Prices;
using AlMotaheda.Modules.Access.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Expenses;

/// <summary>
/// What the price check of a supply allows, for record and for fix-and-resubmit. The comparison itself is
/// <see cref="PriceCheck"/>; this applies the configured <see cref="PriceCheckPolicy"/>.
/// </summary>
internal static class ExpensePricing
{
    /// <summary>
    /// Returns the conflict to answer, or <c>null</c>. A missing justification is added to <paramref name="errors"/>
    /// (a form error on <c>priceJustification</c>), so the caller checks <c>errors</c> first.
    /// </summary>
    /// <param name="keptPriceItemId">On resubmit, the record's current price item: keeping an item that was deactivated since is allowed.</param>
    public static ProblemHttpResult? Check(
        ExpenseFields fields, ExpenseReferences references, PriceCheckPolicy policy, ValidationErrors errors, int? keptPriceItemId = null)
    {
        if (references.Price is not { } price)
        {
            return null;
        }

        if (!price.IsActive && price.PriceItemId != keptPriceItemId)
        {
            return PriceProblems.ItemInactive();
        }

        switch (PriceCheck.Refusal(price.Comparison, policy, fields.PriceJustification))
        {
            case PriceCheckRefusal.Blocked:
                return PriceProblems.AboveRangeBlocked(fields.UnitPrice!.Value, price.Range!.MaxPrice);
            case PriceCheckRefusal.NeedsJustification:
                errors.Add(
                    "priceJustification",
                    $"The unit price is {price.Comparison.DifferencePerUnit:0.00} above the highest reference price {price.Range!.MaxPrice:0.00}: say why.");
                break;
        }

        return null;
    }
}

/// <summary>
/// Adds the alerts a cost record causes, in the same save as the record, so they commit or roll back together. The
/// Platform outbox turns them into stored notifications after the commit (<see cref="CostAlert"/>).
/// </summary>
internal sealed class CostAlerts(ProjectsDbContext db, IUserDirectory users)
{
    public IUserDirectory Users => users;

    /// <summary>The project's active managers, never the actor.</summary>
    public async Task<IReadOnlyList<int>> ManagersAsync(int projectId, int actorUserId, CancellationToken cancellationToken) =>
        await db.ProjectMembers.AsNoTracking()
            .Where(member => member.ProjectId == projectId && member.EndDate == null && member.Responsibility == MemberResponsibility.ProjectManager
                             && member.UserId != actorUserId)
            .Select(member => member.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);

    /// <summary>The project's active managers and every active Executive, never the actor.</summary>
    public async Task<IReadOnlyList<int>> ManagersAndExecutivesAsync(int projectId, int actorUserId, CancellationToken cancellationToken)
    {
        var managers = await ManagersAsync(projectId, actorUserId, cancellationToken);
        var executives = await users.ListActiveAsync([AccessRoles.Executive], cancellationToken);
        return managers.Concat(executives.Select(executive => executive.Id)).Where(id => id != actorUserId).Distinct().Order().ToList();
    }

    /// <summary>
    /// For cash paid from a custody: the project's active managers, every active Executive and every active
    /// accountant, never the actor.
    /// </summary>
    /// <summary>Company overhead (no project, e.g. an office expense): the active Executives and Accountants, never the actor.</summary>
    public async Task<IReadOnlyList<int>> CompanyWatchersAsync(int actorUserId, CancellationToken cancellationToken)
    {
        var company = await users.ListActiveAsync([AccessRoles.Executive, AccessRoles.Accountant], cancellationToken);
        return company.Select(user => user.Id).Where(id => id != actorUserId).Distinct().Order().ToList();
    }

    public async Task<IReadOnlyList<int>> CustodyWatchersAsync(int projectId, int actorUserId, CancellationToken cancellationToken)
    {
        var managers = await ManagersAsync(projectId, actorUserId, cancellationToken);
        var company = await users.ListActiveAsync([AccessRoles.Executive, AccessRoles.Accountant], cancellationToken);
        return managers.Concat(company.Select(user => user.Id)).Where(id => id != actorUserId).Distinct().Order().ToList();
    }

    /// <summary>
    /// Adds one alert of <typeparamref name="TAlert"/> per recipient who doesn't have one about this record yet.
    /// Saved by the caller. Returns how many were added.
    /// </summary>
    public async Task<int> AddAsync<TAlert>(ProjectExpense expense, IReadOnlyList<int> recipients, DateTime now, CancellationToken cancellationToken)
        where TAlert : CostAlert, new()
    {
        var already = expense.Id == 0
            ? []
            : await db.CostAlerts.OfType<TAlert>().Where(alert => alert.ExpenseId == expense.Id).Select(alert => alert.RecipientUserId).ToListAsync(cancellationToken);

        var added = 0;
        foreach (var recipient in recipients.Except(already))
        {
            db.CostAlerts.Add(new TAlert { ProjectId = expense.ProjectId, Expense = expense, RecipientUserId = recipient, CreatedAt = now });
            added++;
        }

        return added;
    }
}
