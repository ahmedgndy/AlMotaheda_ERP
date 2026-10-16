using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Expenses;

/// <summary>
/// <c>GET /api/projects/{pid}/expenses</c>: one page of a project's supplies and site expenses, with the summary
/// of the whole project at the top.
/// </summary>
/// <remarks>
/// A caller without projects SeeMoney sees only the records they recorded themselves (they typed those amounts),
/// and no summary. Every filter, the sort and the paging run in SQL.
/// </remarks>
internal static class ListExpenses
{
    /// <param name="Summary">The whole project, whatever the filters. <c>null</c> without projects SeeMoney.</param>
    internal sealed record Response(IReadOnlyList<ExpenseView> Items, int Page, int PageSize, int TotalCount, ExpenseSummary? Summary);

    public static void Map(IEndpointRouteBuilder projects) =>
        projects.MapGet("/{projectId:int}/expenses", HandleAsync)
            .RequirePermission(ProjectPermissions.Module, PermissionAction.View)
            .WithName("ListProjectExpenses")
            .WithSummary("List a project's supplies and site expenses (filters: kind, status, channel, supplierId, itemId, "
                         + "unlinked, paymentState, from, to, search; sort: date, amount or status). Without See money: only your own records");

    internal static async Task<Results<Ok<Response>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int projectId,
        HttpContext httpContext,
        ProjectsDbContext db,
        IProjectAccess access,
        IUserDirectory users,
        CancellationToken cancellationToken,
        string? kind = null,
        string? status = null,
        string? channel = null,
        int? supplierId = null,
        int? itemId = null,
        bool unlinked = false,
        string? paymentState = null,
        string? from = null,
        string? to = null,
        string? search = null,
        string? sort = null,
        int page = 1,
        int pageSize = Paging.DefaultPageSize)
    {
        if (ProjectCaller.From(httpContext.User) is not { } caller)
        {
            return ApiProblem.ForbiddenResult();
        }

        var errors = new ValidationErrors();
        Paging.Validate(page, pageSize, errors);

        var kindFilter = ParseOptional<ExpenseKind>(kind, "kind", errors);
        var statusFilter = ParseOptional<ExpenseStatus>(status, "status", errors);
        var channelFilter = ParseOptional<PaymentChannel>(channel, "channel", errors);

        if (supplierId is < 1)
        {
            errors.Add("supplierId", "supplierId is not valid.");
        }

        if (itemId is < 1)
        {
            errors.Add("itemId", "itemId is not valid.");
        }

        if (itemId is not null && unlinked)
        {
            errors.Add("unlinked", "unlinked can't be combined with itemId.");
        }

        string? paymentStateFilter = null;
        if (Input.Clean(paymentState) is { } stateText)
        {
            if (PaymentStates.TryParse(stateText, out var parsedState))
            {
                paymentStateFilter = parsedState;
            }
            else
            {
                errors.Add("paymentState", $"paymentState must be one of: {string.Join(", ", PaymentStates.All)}.");
            }
        }

        var fromDate = Input.OptionalDate(from, errors, "from", "from");
        var toDate = Input.OptionalDate(to, errors, "to", "to");
        if (fromDate is { } f && toDate is { } t && t < f)
        {
            errors.Add("to", "to must not be before from.");
        }

        var sortKey = Input.Clean(sort)?.ToLowerInvariant() ?? "date";
        if (sortKey is not ("date" or "amount" or "status"))
        {
            errors.Add("sort", "sort must be date, amount or status.");
        }

        if (errors.HasErrors)
        {
            return ProjectProblems.Validation(errors);
        }

        if (await access.FindVisibleAsync(httpContext.User, projectId, cancellationToken) is null)
        {
            return ProjectProblems.ProjectNotFound();
        }

        var seesMoney = caller.Can(PermissionAction.SeeMoney);
        var filter = new ExpenseFilter(
            kindFilter,
            statusFilter,
            channelFilter,
            supplierId,
            itemId,
            unlinked,
            paymentStateFilter,
            fromDate,
            toDate,
            Input.Clean(search),
            sortKey,
            RecordedBy: seesMoney ? null : caller.UserId);

        var query = ExpenseQueries.Filtered(db, projectId, filter);
        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var items = await ExpenseView.FromAsync(rows, users, seesMoney, cancellationToken);

        ExpenseSummary? summary = null;
        if (seesMoney)
        {
            summary = ExpenseSummary.Compute(await ExpenseQueries.SettlementRows(db, projectId).ToListAsync(cancellationToken));
        }

        return TypedResults.Ok(new Response(items, page, pageSize, totalCount, summary));
    }

    private static TEnum? ParseOptional<TEnum>(string? value, string field, ValidationErrors errors)
        where TEnum : struct, Enum
    {
        if (Input.Clean(value) is not { } text)
        {
            return null;
        }

        if (Input.TryParseName<TEnum>(text, out var parsed))
        {
            return parsed;
        }

        errors.Add(field, $"{field} must be one of: {string.Join(", ", Enum.GetNames<TEnum>())}.");
        return null;
    }
}
