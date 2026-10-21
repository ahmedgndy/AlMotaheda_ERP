using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Office.Data;

namespace AlMotaheda.Modules.Office.Features.Alerts;

/// <summary>
/// Writes alert rows (one per person) in the caller's transaction. The Platform outbox turns each committed row
/// into a stored notification and a push, so nobody is told about something that rolled back.
/// </summary>
internal sealed class OfficeAlerts(IUserDirectory users)
{
    /// <summary>The people who review office records: the active Accountants and Executives.</summary>
    public static readonly IReadOnlyList<string> ReviewerRoles = [AccessRoles.Accountant, AccessRoles.Executive];

    public IUserDirectory Users => users;

    /// <summary>The active reviewers, without <paramref name="except"/> (the person who did the action).</summary>
    public async Task<IReadOnlyList<int>> ReviewersAsync(int? except, CancellationToken cancellationToken) =>
        (await users.ListActiveAsync(ReviewerRoles, cancellationToken))
        .Where(user => user.Id != except)
        .Select(user => user.Id)
        .Distinct()
        .ToList();

    public void Add<TAlert>(OfficeDbContext db, IEnumerable<int> recipients, int custodyId, string sourceType, int sourceId, DateTime now)
        where TAlert : OfficeAlert, new()
    {
        foreach (var recipient in recipients.Distinct())
        {
            db.Alerts.Add(new TAlert
            {
                CustodyId = custodyId,
                SourceType = sourceType,
                SourceId = sourceId,
                RecipientUserId = recipient,
                CreatedAt = now,
            });
        }
    }
}
