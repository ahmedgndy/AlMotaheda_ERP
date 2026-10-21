using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.BuildingBlocks.Time;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Custody;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Overview;

/// <summary>
/// One person's home page (<c>GET /api/me/overview</c>, section 12.A): the projects they are on, the cash custody
/// they hold on each (what they received, spent, have pending and hold now), their purchases and expenses waiting
/// for review, the latest custody movements, and each module's own section (funding requests, labor sheets and
/// settlements, HR requests). Everything is the caller's own; nothing of anyone else is shown. The person's salary
/// is not here: that is HR's, and never the money they hold for the company.
/// </summary>
internal static class PersonalOverview
{
    private const int Limit = 20;

    internal sealed record ProjectRow(int ProjectId, string Code, string NameAr, string NameEn, string Status, string Responsibility, DateOnly MemberSince);

    internal sealed record CustodyRow(int ProjectId, string Code, decimal Received, decimal Returned, decimal SpentApproved, decimal SpentPending, decimal Adjusted, decimal Balance, int PendingCount, DateOnly? LastMovementDate);

    internal sealed record PendingExpenseRow(int ProjectId, int ExpenseId, string Kind, DateOnly ExpenseDate, decimal Amount, string Status, string? Description, string? ReviewNote);

    internal sealed record MovementRow(int ProjectId, DateOnly Date, string Type, decimal Amount, bool Pending, string Status, string SourceType, int SourceId, string? Description);

    internal sealed record SectionView(string Section, IReadOnlyDictionary<string, decimal> Figures, IReadOnlyList<PersonalOverviewItem> Items);

    internal sealed record Response(
        int UserId,
        DateOnly Today,
        IReadOnlyList<ProjectRow> Projects,
        IReadOnlyList<CustodyRow> Custody,
        decimal CustodyBalanceTotal,
        decimal CustodyPendingTotal,
        IReadOnlyList<PendingExpenseRow> PendingExpenses,
        IReadOnlyList<MovementRow> RecentMovements,
        IReadOnlyList<SectionView> Sections,
        string BalancePolicy);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/me/overview", HandleAsync)
            .WithName("GetPersonalOverview")
            .WithSummary("The caller's own home page: projects, custody per project, pending expenses, recent movements, and each module's section (funding, labor, HR)");

    internal static async Task<Results<Ok<Response>, ProblemHttpResult>> HandleAsync(
        HttpContext httpContext,
        ProjectsDbContext db,
        ProjectAccess access,
        IEnumerable<IPersonalOverviewSource> sources,
        CompanyClock clock,
        Microsoft.Extensions.Options.IOptions<Common.ProjectsOptions> settings,
        CancellationToken cancellationToken)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var me = caller.UserId;
        var visible = access.VisibleProjects(caller).AsNoTracking();
        var memberships = await db.ProjectMembers.AsNoTracking()
            .Where(member => member.UserId == me && member.EndDate == null)
            .Join(visible, member => member.ProjectId, project => project.Id, (member, project) => new { member, project })
            .OrderBy(row => row.project.Code)
            .Select(row => new ProjectRow(row.project.Id, row.project.Code, row.project.NameAr, row.project.NameEn, row.project.Status.ToString(), row.member.Responsibility.ToString(), row.member.StartDate))
            .ToListAsync(cancellationToken);

        // The custody the person holds on each project they are on, and on any other visible project where they still hold cash.
        var projectIds = memberships.Select(row => row.ProjectId).ToList();
        var holding = await db.CustodyEntries.AsNoTracking().Where(entry => entry.HolderUserId == me && !projectIds.Contains(entry.ProjectId))
            .Join(visible, entry => entry.ProjectId, project => project.Id, (entry, project) => project.Id).Distinct().ToListAsync(cancellationToken);
        var codes = memberships.ToDictionary(row => row.ProjectId, row => row.Code);
        foreach (var code in await visible.Where(project => holding.Contains(project.Id)).Select(project => new { project.Id, project.Code }).ToListAsync(cancellationToken))
        {
            codes[code.Id] = code.Code;
        }

        var custody = new List<CustodyRow>();
        var movements = new List<MovementRow>();
        foreach (var projectId in projectIds.Concat(holding))
        {
            var rows = (await CustodyQueries.MovementsAsync(db, projectId, me, cancellationToken)).Select(row => row.Movement).ToList();
            if (rows.Count == 0)
            {
                continue;
            }

            var figures = CustodyBalance.Compute(rows);
            custody.Add(new CustodyRow(projectId, codes[projectId], figures.Received, figures.Returned, figures.SpentApproved, figures.SpentPending, figures.Adjusted, figures.Balance, figures.PendingCount, rows.Max(row => row.Date)));
            movements.AddRange(rows.Select(row => new MovementRow(projectId, row.Date, row.Type, row.Kind is { } kind && CustodyEntryKinds.IsCashIn(kind) ? row.Amount : -row.Amount, row.Pending, row.Status, row.SourceType, row.SourceId, row.Description)));
        }

        var pending = await db.ProjectExpenses.AsNoTracking()
            .Where(expense => expense.CreatedBy == me && projectIds.Contains(expense.ProjectId) && (expense.Status == ExpenseStatus.Recorded || expense.Status == ExpenseStatus.Returned))
            .OrderByDescending(expense => expense.ExpenseDate).ThenByDescending(expense => expense.Id)
            .Take(Limit)
            .Select(expense => new PendingExpenseRow(expense.ProjectId, expense.Id, expense.Kind.ToString(), expense.ExpenseDate, expense.Amount, expense.Status.ToString(), expense.Description, expense.ReviewNote))
            .ToListAsync(cancellationToken);

        var sections = new List<SectionView>();
        var query = new PersonalOverviewQuery(httpContext.User, me, projectIds, clock.Today, Limit);
        foreach (var source in sources)
        {
            if (await source.LoadAsync(query, cancellationToken) is { } section)
            {
                sections.Add(new SectionView(section.Section, section.Figures, section.Items));
            }
        }

        return TypedResults.Ok(new Response(
            me, clock.Today, memberships, custody, custody.Sum(row => row.Balance), custody.Sum(row => row.SpentPending), pending,
            movements.OrderByDescending(row => row.Date).Take(Limit).ToList(), sections, settings.Value.CustodyBalanceCheck));
    }
}
