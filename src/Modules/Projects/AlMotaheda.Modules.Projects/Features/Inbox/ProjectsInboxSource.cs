using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Projects.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Catalog;
using AlMotaheda.Modules.Projects.Features.DailyReports;
using AlMotaheda.Modules.Projects.Security;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Projects.Features.Inbox;

/// <summary>
/// The Projects module's part of the work inbox: expenses and supplies to review or fix, daily reports to review or
/// fix, reports asked of the caller or waiting for the caller to complete, and price changes for an Executive.
/// </summary>
/// <remarks>The same rules as the actions themselves: the recorder never reviews their own record.</remarks>
internal sealed class ProjectsInboxSource(ProjectsDbContext db) : IWorkInboxSource
{
    public async Task<IReadOnlyList<WorkInboxItem>> ListAsync(WorkInboxQuery query, CancellationToken cancellationToken)
    {
        var ids = query.ProjectIds.ToList();
        var me = query.UserId;
        var items = new List<WorkInboxItem>();
        var seesMoney = query.Caller.HasPermission(ProjectPermissions.Module, PermissionAction.SeeMoney);

        if (query.Caller.HasPermission(ProjectPermissions.Module, PermissionAction.Approve))
        {
            var toReview = await db.ProjectExpenses.AsNoTracking()
                .Where(expense => ids.Contains(expense.ProjectId) && expense.Status == ExpenseStatus.Recorded && expense.CreatedBy != me)
                .OrderBy(expense => expense.CreatedAt)
                .Take(query.Limit)
                .Select(expense => new { expense.Id, expense.ProjectId, expense.Kind, expense.ItemName, expense.Description, expense.ExpenseDate, expense.Amount, expense.CreatedAt, expense.UpdatedAt, expense.CreatedBy })
                .ToListAsync(cancellationToken);
            items.AddRange(toReview.Select(expense => new WorkInboxItem(
                WorkInboxReasons.Review, WorkInboxRecordTypes.Expense, expense.ProjectId, expense.Id, null, expense.ExpenseDate,
                expense.UpdatedAt ?? expense.CreatedAt, Summary(expense.Kind, expense.ItemName, expense.Description),
                seesMoney ? expense.Amount : null, expense.CreatedBy, null)));
        }

        var returnedExpenses = await db.ProjectExpenses.AsNoTracking()
            .Where(expense => ids.Contains(expense.ProjectId) && expense.Status == ExpenseStatus.Returned && expense.CreatedBy == me)
            .OrderBy(expense => expense.ReviewedAt)
            .Take(query.Limit)
            .Select(expense => new { expense.Id, expense.ProjectId, expense.Kind, expense.ItemName, expense.Description, expense.ExpenseDate, expense.Amount, expense.ReviewedAt, expense.CreatedAt, expense.ReviewedBy, expense.ReviewNote })
            .ToListAsync(cancellationToken);
        items.AddRange(returnedExpenses.Select(expense => new WorkInboxItem(
            WorkInboxReasons.Returned, WorkInboxRecordTypes.Expense, expense.ProjectId, expense.Id, null, expense.ExpenseDate,
            expense.ReviewedAt ?? expense.CreatedAt, Summary(expense.Kind, expense.ItemName, expense.Description),
            seesMoney ? expense.Amount : null, expense.ReviewedBy, expense.ReviewNote)));

        if (query.Caller.HasPermission(SitePermissions.Module, PermissionAction.Approve))
        {
            var reports = await db.DailyReports.AsNoTracking()
                .Where(report => ids.Contains(report.ProjectId) && report.Status == DailyReportStatus.Submitted && report.AuthorUserId != me)
                .OrderBy(report => report.SubmittedAt)
                .Take(query.Limit)
                .Select(report => new { report.Id, report.ProjectId, report.ReportDate, report.SubmittedAt, report.AuthorUserId, report.Notes })
                .ToListAsync(cancellationToken);
            items.AddRange(reports.Select(report => new WorkInboxItem(
                WorkInboxReasons.Review, WorkInboxRecordTypes.DailyReport, report.ProjectId, report.Id, null, report.ReportDate,
                report.SubmittedAt, Trim(report.Notes) ?? "", null, report.AuthorUserId, null)));
        }

        var returnedReports = await db.DailyReports.AsNoTracking()
            .Where(report => ids.Contains(report.ProjectId) && report.Status == DailyReportStatus.Returned && report.AuthorUserId == me)
            .OrderBy(report => report.ReviewedAt)
            .Take(query.Limit)
            .Select(report => new { report.Id, report.ProjectId, report.ReportDate, report.ReviewedAt, report.SubmittedAt, report.ReviewedBy, report.ReviewNote, report.Notes })
            .ToListAsync(cancellationToken);
        items.AddRange(returnedReports.Select(report => new WorkInboxItem(
            WorkInboxReasons.Returned, WorkInboxRecordTypes.DailyReport, report.ProjectId, report.Id, null, report.ReportDate,
            report.ReviewedAt ?? report.SubmittedAt, Trim(report.Notes) ?? "", null, report.ReviewedBy, report.ReviewNote)));

        // Reports: asked of me and not submitted (or returned to me), and submitted to me for completion.
        var requests = await db.ReportRequests.AsNoTracking()
            .Where(request => ids.Contains(request.ProjectId)
                && ((request.RecipientUserId == me && (request.Status == ReportRequestStatus.Requested || request.Status == ReportRequestStatus.InProgress || request.Status == ReportRequestStatus.Returned))
                    || (request.RequesterUserId == me && request.RecipientUserId != me && request.Status == ReportRequestStatus.Submitted)))
            .OrderBy(request => request.CreatedAt)
            .Take(query.Limit)
            .Select(request => new { request.Id, request.ProjectId, request.Title, request.DueDate, request.Status, request.CreatedAt, request.SubmittedAt, request.ReviewedAt, request.RequesterUserId, request.RecipientUserId, request.ReturnNote })
            .ToListAsync(cancellationToken);
        foreach (var request in requests)
        {
            var date = request.DueDate ?? DateOnly.FromDateTime(request.CreatedAt);
            items.Add(request.Status switch
            {
                ReportRequestStatus.Submitted => new WorkInboxItem(WorkInboxReasons.Review, WorkInboxRecordTypes.ReportRequest, request.ProjectId, request.Id, null,
                    date, request.SubmittedAt ?? request.CreatedAt, request.Title, null, request.RecipientUserId, null),
                ReportRequestStatus.Returned => new WorkInboxItem(WorkInboxReasons.Returned, WorkInboxRecordTypes.ReportRequest, request.ProjectId, request.Id, null,
                    date, request.ReviewedAt ?? request.CreatedAt, request.Title, null, request.RequesterUserId, request.ReturnNote),
                _ => new WorkInboxItem(WorkInboxReasons.Requested, WorkInboxRecordTypes.ReportRequest, request.ProjectId, request.Id, null,
                    date, request.CreatedAt, request.Title, null, request.RequesterUserId, null),
            });
        }

        // Price changes and price variances are decided with catalog Approve (the Financial Manager or an Executive),
        // never on one's own request or supply.
        if (query.Caller.HasPermission(CatalogPermissions.Module, PermissionAction.Approve))
        {
            var prices = await db.ItemPrices.AsNoTracking()
                .Where(price => ids.Contains(price.CatalogItem.ProjectId) && price.Status == ItemPriceStatus.Pending && price.RequestedBy != me)
                .OrderBy(price => price.RequestedAt)
                .Take(query.Limit)
                .Select(price => new { price.Id, price.CatalogItemId, price.CatalogItem.ProjectId, price.CatalogItem.NameAr, price.CatalogItem.NameEn, price.EffectiveFrom, price.RequestedAt, price.RequestedBy, price.Price })
                .ToListAsync(cancellationToken);
            items.AddRange(prices.Select(price => new WorkInboxItem(
                WorkInboxReasons.Review, WorkInboxRecordTypes.PriceChange, price.ProjectId, price.Id, price.CatalogItemId, price.EffectiveFrom,
                price.RequestedAt, string.IsNullOrWhiteSpace(price.NameEn) ? price.NameAr : $"{price.NameAr} / {price.NameEn}",
                price.Price, price.RequestedBy, null)));

            var variances = await db.PriceVariances.AsNoTracking()
                .Where(variance => ids.Contains(variance.ProjectId) && variance.Status == PriceVarianceStatus.Open
                    && variance.RaisedBy != me && variance.Expense.CreatedBy != me)
                .OrderBy(variance => variance.RaisedAt)
                .Take(query.Limit)
                .Select(variance => new
                {
                    variance.Id, variance.ProjectId, variance.ExpenseId, variance.Expense.ExpenseDate, variance.RaisedAt, variance.RaisedBy,
                    variance.Expense.ItemName, variance.TotalVariance,
                })
                .ToListAsync(cancellationToken);
            var seesCatalogMoney = query.Caller.HasPermission(CatalogPermissions.Module, PermissionAction.SeeMoney);
            items.AddRange(variances.Select(variance => new WorkInboxItem(
                WorkInboxReasons.Review, WorkInboxRecordTypes.PriceVariance, variance.ProjectId, variance.Id, variance.ExpenseId, variance.ExpenseDate,
                variance.RaisedAt, variance.ItemName ?? "", seesCatalogMoney ? variance.TotalVariance : null, variance.RaisedBy, null)));
        }

        return items;
    }

    private static string Summary(ExpenseKind kind, string? itemName, string? description) =>
        (kind == ExpenseKind.Supply ? Trim(itemName) : Trim(description)) ?? Trim(description) ?? Trim(itemName) ?? "";

    private static string? Trim(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        return value.Length <= 120 ? value : value[..120] + "…";
    }
}
