using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Projects;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Budgets;

/// <summary>One budget line, as the detail returns it.</summary>
internal sealed record BudgetLineView(
    int Id,
    int LineNo,
    string CostGroup,
    int? ProjectItemId,
    string? ProjectItemNameAr,
    string? ProjectItemNameEn,
    string Description,
    string? Unit,
    decimal? Quantity,
    decimal? UnitPrice,
    decimal Amount)
{
    public static BudgetLineView From(BudgetLineRow row) => new(
        row.Id, row.LineNo, row.CostGroup, row.ProjectItemId, row.ProjectItemNameAr, row.ProjectItemNameEn,
        row.Description, row.Unit, row.Quantity, row.UnitPrice, row.Amount);
}

/// <summary>
/// One budget version. <see cref="Total"/> and <see cref="GroupTotals"/> are sums of its lines, never stored.
/// <see cref="Lines"/> is filled by the detail and the writes, and is <c>null</c> in the list.
/// </summary>
internal sealed record BudgetView(
    int Id,
    int ProjectId,
    int Version,
    string Status,
    string? Title,
    string? Notes,
    decimal Total,
    IReadOnlyList<CostGroupAmount> GroupTotals,
    int LineCount,
    DateTime? ApprovedAt,
    PersonRef? Approver,
    DateTime CreatedAt,
    PersonRef Creator,
    DateTime? UpdatedAt,
    int? UpdatedBy,
    string RowVersion,
    IReadOnlyList<BudgetLineView>? Lines)
{
    public static BudgetView From(
        BudgetHeader header,
        IReadOnlyList<BudgetLineAmount> amounts,
        IReadOnlyDictionary<int, UserSummary> people,
        IReadOnlyList<BudgetLineView>? lines) =>
        new(
            header.Id,
            header.ProjectId,
            header.Version,
            header.Status.ToString(),
            header.Title,
            header.Notes,
            amounts.Sum(amount => amount.Amount),
            BudgetComparison.GroupTotals(amounts),
            amounts.Count,
            header.ApprovedAt,
            header.ApprovedBy is { } approver ? PersonRef.From(approver, people) : null,
            header.CreatedAt,
            PersonRef.From(header.CreatedBy, people),
            header.UpdatedAt,
            header.UpdatedBy,
            RowVersions.Format(header.RowVersion),
            lines);

    /// <summary>Builds the views of several versions with one query for their lines and one name lookup.</summary>
    public static async Task<IReadOnlyList<BudgetView>> FromAsync(
        ProjectsDbContext db, IUserDirectory users, int projectId, IReadOnlyList<BudgetHeader> headers, CancellationToken cancellationToken)
    {
        var ids = headers.Select(header => header.Id).ToList();
        var amounts = ids.Count == 0
            ? []
            : await BudgetQueries.Amounts(db, projectId, ids).ToListAsync(cancellationToken);
        var people = await PeopleAsync(users, headers, cancellationToken);

        return headers
            .Select(header => From(header, amounts.Where(amount => amount.BudgetId == header.Id).ToList(), people, lines: null))
            .ToList();
    }

    /// <summary>Reads one version of a project with its lines, or <c>null</c> when it is not on this project.</summary>
    public static async Task<BudgetView?> LoadAsync(
        ProjectsDbContext db, IUserDirectory users, int projectId, int budgetId, CancellationToken cancellationToken)
    {
        var header = await BudgetQueries.One(db, projectId, budgetId).SingleOrDefaultAsync(cancellationToken);
        if (header is null)
        {
            return null;
        }

        var lines = await BudgetQueries.Lines(db, projectId, budgetId).ToListAsync(cancellationToken);
        var amounts = lines.Select(line => new BudgetLineAmount(budgetId, line.CostGroup, line.ProjectItemId, line.Amount)).ToList();
        var people = await PeopleAsync(users, [header], cancellationToken);

        return From(header, amounts, people, lines.Select(BudgetLineView.From).ToList());
    }

    private static async Task<IReadOnlyDictionary<int, UserSummary>> PeopleAsync(
        IUserDirectory users, IReadOnlyList<BudgetHeader> headers, CancellationToken cancellationToken)
    {
        var ids = headers.Select(header => header.CreatedBy)
            .Concat(headers.Where(header => header.ApprovedBy is not null).Select(header => header.ApprovedBy!.Value))
            .Distinct()
            .ToList();

        return ids.Count == 0 ? new Dictionary<int, UserSummary>() : await users.FindAsync(ids, cancellationToken);
    }
}

/// <summary>The rules of who prepares and approves a budget (section A and decision B1).</summary>
internal static class BudgetRules
{
    /// <summary>
    /// Creating or editing a draft: an Executive or the project's manager (projects Edit is checked by the
    /// endpoint), and a project that is not completed (an Executive excepted).
    /// </summary>
    public static ProblemHttpResult? PrepareProblem(ProjectAccessInfo project)
    {
        if (!project.CallerIsExecutive && !project.CallerIsManager)
        {
            return PlanningProblems.BudgetManagedByManager();
        }

        return project.WritesBlocked ? ProjectProblems.CompletedProject() : null;
    }

    /// <summary>
    /// Approving: the Executive role (projects Approve is checked by the endpoint). An Executive may change a
    /// completed project, so there is no completed check here.
    /// </summary>
    public static ProblemHttpResult? ApproveProblem(ProjectAccessInfo project) =>
        project.CallerIsExecutive ? null : PlanningProblems.BudgetApprovedByExecutive();
}
