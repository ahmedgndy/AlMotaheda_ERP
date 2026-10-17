using AlMotaheda.BuildingBlocks.Data;
using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Contracts;

namespace AlMotaheda.Modules.Platform.Notifications;

/// <summary>
/// "Something waits for your review" (docs/cost-flow §2.8): when a record is submitted for review, the people who
/// may decide it hear about it. Recipients are chosen by permission and project, never by role names: everyone who
/// holds the reviewing permission right now (after their personal overrides) and may see the record's project, never
/// the person who submitted it. Records without a project (master prices, salary sheets) go to every holder.
/// </summary>
/// <remarks>
/// Titles carry no amounts or names: the notification opens the record, where the reviewer's own permissions decide
/// what is shown. The work inbox still lists the same records; a notification is the "it just arrived" signal.
/// </remarks>
internal static class ReviewRules
{
    /// <param name="Trigger">The status the record reaches when it is submitted (as added, or by a later move).</param>
    /// <param name="SubmitterField">The record's user field of the person who submitted it.</param>
    internal sealed record Rule(string Entity, string Trigger, string Module, string SubmitterField, string NounEn, string NounAr);

    private static readonly Rule[] Rules =
    [
        new("ProjectExpense", "Recorded", "projects", "CreatedBy", "expense or supply", "مصروف أو توريد"),
        new("EquipmentCost", "Recorded", "equipment", "CreatedBy", "equipment cost", "تكلفة معدة"),
        new("RepairRequest", "Open", "equipment", "RequestedBy", "repair request", "طلب إصلاح"),
        new("DailySheet", "Submitted", "workers", "RecordedBy", "daily labor sheet", "كشف عمالة يومي"),
        new("ItemPrice", "Pending", "catalog", "RequestedBy", "price change request", "طلب تغيير سعر"),
        new("MasterItemPrice", "Pending", "catalog", "RequestedBy", "master price change request", "طلب تغيير سعر في القائمة الرئيسية"),
        new("PriceVariance", "Open", "catalog", "RaisedBy", "price variance", "فرق سعر"),
        new("SalarySheet", "Submitted", "hr", "SubmittedBy", "salary sheet", "كشف رواتب"),
        new("ProjectWorkDays", "Recorded", "hr", "CreatedBy", "employee's project days", "أيام عمل موظف على مشروع"),
    ];

    /// <summary>The rule a change matches: the record was added in, or moved into, its "waiting for review" status.</summary>
    public static Rule? For(EntityChange change)
    {
        foreach (var rule in Rules)
        {
            if (rule.Entity != change.Entity || change.StatusAfter != rule.Trigger)
            {
                continue;
            }

            var submitted = change.Action == "Added" || (change.Action == "Modified" && change.StatusBefore != change.StatusAfter);
            if (submitted)
            {
                return rule;
            }
        }

        return null;
    }

    public static (string En, string Ar) Titles(Rule rule) =>
        ($"A new {rule.NounEn} waits for your review", $"{rule.NounAr} جديد بانتظار مراجعتك");
}

/// <summary>Finds the reviewers of a change, from Access (who holds the permission) and Projects (who sees the project).</summary>
internal sealed class ReviewRecipients(IUserDirectory users, IProjectAudience audience)
{
    public async Task<IReadOnlyList<NotificationDraft>> DraftsAsync(EntityChange change, int? actorUserId, CancellationToken cancellationToken)
    {
        if (ReviewRules.For(change) is not { } rule)
        {
            return [];
        }

        IReadOnlyCollection<int> holders = await users.ListActiveHoldingAsync(rule.Module, "Approve", cancellationToken);
        if (change.ProjectId is { } projectId)
        {
            holders = (await audience.WhoSeeAsync(projectId, holders, cancellationToken)).ToList();
        }

        change.People.TryGetValue(rule.SubmitterField, out var submitter);
        var (en, ar) = ReviewRules.Titles(rule);

        return holders
            .Where(userId => userId != actorUserId && userId != submitter)
            .Order()
            .Select(userId => new NotificationDraft(userId, NotificationTypes.ReviewRequested, ar, en))
            .ToList();
    }
}
