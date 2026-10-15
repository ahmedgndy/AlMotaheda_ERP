using AlMotaheda.BuildingBlocks.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AlMotaheda.Modules.Projects.Common;

/// <summary>
/// Error responses of the second part of the workspace: budgets, the price catalog, daily site reports and report
/// requests. Codes shared with the other parts live in <see cref="WorkspaceProblems"/> and <see cref="ProjectProblems"/>.
/// </summary>
internal static class PlanningProblems
{
    public const string BudgetDraftExists = "budget_draft_exists";
    public const string BudgetNotDraft = "budget_not_draft";
    public const string BudgetEmpty = "budget_empty";
    public const string CatalogItemExists = "catalog_item_exists";
    public const string PriceChangePending = "price_change_pending";
    public const string SamePrice = "same_price";
    public const string PriceDateTaken = "price_date_taken";
    public const string ReportExists = "report_exists";

    public static ProblemHttpResult BudgetNotFound() => WorkspaceProblems.NotFound("budget");

    public static ProblemHttpResult CatalogItemNotFound() => WorkspaceProblems.NotFound("catalog item");

    public static ProblemHttpResult PriceChangeNotFound() => WorkspaceProblems.NotFound("price change");

    public static ProblemHttpResult DailyReportNotFound() => WorkspaceProblems.NotFound("daily report");

    public static ProblemHttpResult ReportRequestNotFound() => WorkspaceProblems.NotFound("report request");

    /// <summary>Only an Executive or the project's manager prepares its budget.</summary>
    public static ProblemHttpResult BudgetManagedByManager() =>
        ApiProblem.ForbiddenResult("Only an Executive or this project's manager can prepare its budget.");

    /// <summary>Decision B1: approving a budget is a management decision.</summary>
    public static ProblemHttpResult BudgetApprovedByExecutive() =>
        ApiProblem.ForbiddenResult("Only an Executive can approve a budget.");

    public static ProblemHttpResult DraftAlreadyExists() =>
        ApiProblem.Create(
            StatusCodes.Status409Conflict,
            BudgetDraftExists,
            "This project already has a draft budget. Edit it, or approve it before starting another one.");

    public static ProblemHttpResult NotADraft() =>
        ApiProblem.Create(
            StatusCodes.Status409Conflict,
            BudgetNotDraft,
            "Only a draft budget can be changed. An approved budget never changes: start a new version instead.");

    public static ProblemHttpResult NoLines() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, BudgetEmpty, "A budget with no lines can't be approved.");

    public static ProblemHttpResult CatalogNameTaken() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, CatalogItemExists, "This project's catalog already has an item with this name.");

    /// <summary>Price changes are decided by an Executive only, never by the person who asked.</summary>
    public static ProblemHttpResult PricesDecidedByExecutive() =>
        ApiProblem.ForbiddenResult("Only an Executive can approve or reject a price change.");

    public static ProblemHttpResult PendingChangeExists() =>
        ApiProblem.Create(
            StatusCodes.Status409Conflict,
            PriceChangePending,
            "A price change for this item is already waiting for approval.");

    public static ProblemHttpResult PriceUnchanged() =>
        ApiProblem.Create(StatusCodes.Status409Conflict, SamePrice, "The new price is the same as the current price.");

    /// <summary>One approved price per item and start date (unique index): a second one would make the price unclear.</summary>
    public static ProblemHttpResult PriceDateAlreadyUsed() =>
        ApiProblem.Create(
            StatusCodes.Status409Conflict,
            PriceDateTaken,
            "An approved price of this item already starts on this date. Choose another effective date.");

    public static ProblemHttpResult ReportAlreadyFiled() =>
        ApiProblem.Create(
            StatusCodes.Status409Conflict,
            ReportExists,
            "You already filed a report for this project and date. Edit that report instead.");

    public static ProblemHttpResult NotTheAuthor() =>
        ApiProblem.ForbiddenResult("Only the person who wrote this report can change it.");

    /// <summary>Daily reports are reviewed by an Executive or the project's manager.</summary>
    public static ProblemHttpResult ReportsReviewedByManager() =>
        ApiProblem.ForbiddenResult("Only an Executive or this project's manager can review daily reports.");

    public static ProblemHttpResult ReportRequestsNeedEdit() =>
        ApiProblem.ForbiddenResult("Asking someone for a report needs reports Edit.");

    public static ProblemHttpResult ProactiveReportsNeedCreate() =>
        ApiProblem.ForbiddenResult("Filing a report needs reports Create.");

    public static ProblemHttpResult OnlyTheRecipient() =>
        ApiProblem.ForbiddenResult("Only the person this request was sent to can work on it.");

    public static ProblemHttpResult OnlyTheRequester() =>
        ApiProblem.ForbiddenResult("Only the person who asked for this report can do this.");

    public static ProblemHttpResult OnlyTheRequesterOrExecutive() =>
        ApiProblem.ForbiddenResult("Only the person who asked for this report, or an Executive, can review the answer.");
}
