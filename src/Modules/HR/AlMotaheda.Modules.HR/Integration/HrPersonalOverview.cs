using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.HR.Common;
using AlMotaheda.Modules.HR.Data;
using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.HR.Integration;

/// <summary>
/// HR's section of a person's home page (<see cref="IPersonalOverviewSource"/>, section 12.D): their own HR
/// requests by status and the cases referred about them, as far as they may know. Nothing of other employees.
/// </summary>
internal sealed class HrPersonalOverview(HrDbContext db) : IPersonalOverviewSource
{
    public string Section => "Hr";

    public async Task<PersonalOverviewSection?> LoadAsync(PersonalOverviewQuery query, CancellationToken cancellationToken)
    {
        var me = await db.Employees.AsNoTracking().Where(employee => employee.UserId == query.UserId).Select(employee => new { employee.Id }).SingleOrDefaultAsync(cancellationToken);
        if (me is null)
        {
            return null;
        }

        var requests = await db.Requests.AsNoTracking().Where(request => request.EmployeeId == me.Id)
            .OrderByDescending(request => request.Id).Take(50)
            .Select(request => new { request.Id, request.Number, request.Type, request.Subject, request.Status, request.SubmittedAt, request.UpdatedAt, request.DecisionNote, request.FromDate })
            .ToListAsync(cancellationToken);
        var referrals = await db.Referrals.AsNoTracking().Where(referral => referral.EmployeeId == me.Id)
            .OrderByDescending(referral => referral.Id).Take(20)
            .Select(referral => new { referral.Id, referral.Number, referral.Subject, referral.Status, referral.ReferredAt, referral.Outcome, referral.ClosedAt })
            .ToListAsync(cancellationToken);

        var items = requests.Take(query.Limit).Select(request => new PersonalOverviewItem(
                "HrRequest", request.Id, null, request.Status.ToString(), request.FromDate ?? DateOnly.FromDateTime(request.SubmittedAt), request.UpdatedAt ?? request.SubmittedAt,
                $"طلب {request.Number}: {request.Subject}", $"Request {request.Number} ({request.Type}): {request.Subject}", null, request.DecisionNote, $"/api/hr/my/requests/{request.Id}"))
            .Concat(referrals.Select(referral => new PersonalOverviewItem(
                "HrReferral", referral.Id, null, referral.Status.ToString(), DateOnly.FromDateTime(referral.ReferredAt), referral.ClosedAt ?? referral.ReferredAt,
                $"حالة {referral.Number}: {referral.Subject}", $"Case {referral.Number}: {referral.Subject}", null, referral.Outcome?.ToString(), "/api/hr/my/referrals")))
            .OrderByDescending(item => item.Since).Take(query.Limit).ToList();

        var figures = new Dictionary<string, decimal>
        {
            ["requestsOpen"] = requests.Count(request => request.Status is HrRequestStatus.Submitted or HrRequestStatus.UnderReview),
            ["requestsNeedingMyAnswer"] = requests.Count(request => request.Status == HrRequestStatus.NeedsInfo),
            ["requestsApproved"] = requests.Count(request => request.Status == HrRequestStatus.Approved),
            ["requestsRejected"] = requests.Count(request => request.Status == HrRequestStatus.Rejected),
            ["casesOpen"] = referrals.Count(referral => referral.Status != HrReferralStatus.Closed),
        };
        return new PersonalOverviewSection(Section, figures, items);
    }
}

/// <summary>
/// HR's part of the work inbox (section 18): requests waiting for HR (hr Edit; never one's own), and the caller's
/// own requests HR asked more about. Company-wide: no project.
/// </summary>
internal sealed class HrInboxSource(HrDbContext db) : IWorkInboxSource
{
    public async Task<IReadOnlyList<WorkInboxItem>> ListAsync(WorkInboxQuery query, CancellationToken cancellationToken)
    {
        var me = query.UserId;
        var items = new List<WorkInboxItem>();

        if (query.Caller.HasPermission(HrPermissions.Module, PermissionAction.Edit))
        {
            var waiting = await db.Requests.AsNoTracking()
                .Where(request => (request.Status == HrRequestStatus.Submitted || request.Status == HrRequestStatus.UnderReview) && request.SubmittedBy != me)
                .OrderBy(request => request.SubmittedAt)
                .Take(query.Limit)
                .Select(request => new { request.Id, request.Number, request.Type, request.Subject, request.SubmittedAt, request.UpdatedAt, request.SubmittedBy, EmployeeNo = request.Employee.EmployeeNo })
                .ToListAsync(cancellationToken);
            items.AddRange(waiting.Select(request => new WorkInboxItem(
                WorkInboxReasons.Review, WorkInboxRecordTypes.HrRequest, null, request.Id, null, DateOnly.FromDateTime(request.SubmittedAt), request.UpdatedAt ?? request.SubmittedAt,
                $"{request.Number} · {request.Type} · {request.EmployeeNo}: {request.Subject}", null, request.SubmittedBy, null)));
        }

        var mine = await db.Requests.AsNoTracking()
            .Where(request => request.SubmittedBy == me && request.Status == HrRequestStatus.NeedsInfo)
            .OrderByDescending(request => request.UpdatedAt)
            .Take(query.Limit)
            .Select(request => new { request.Id, request.Number, request.Subject, request.SubmittedAt, request.UpdatedAt, request.ReviewedBy })
            .ToListAsync(cancellationToken);
        items.AddRange(mine.Select(request => new WorkInboxItem(
            WorkInboxReasons.Returned, WorkInboxRecordTypes.HrRequest, null, request.Id, null, DateOnly.FromDateTime(request.SubmittedAt), request.UpdatedAt ?? request.SubmittedAt,
            $"{request.Number}: {request.Subject} · HR asked for more", null, request.ReviewedBy, null)));

        return items;
    }
}
