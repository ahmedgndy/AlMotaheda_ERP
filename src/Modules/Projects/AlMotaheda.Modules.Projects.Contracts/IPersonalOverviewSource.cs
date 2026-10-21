namespace AlMotaheda.Modules.Projects.Contracts;

/// <summary>
/// A module's part of one person's home page (<c>GET /api/me/overview</c>, section 12.A): what the signed-in user
/// has waiting, pending or recent in that module, on the projects they may see. Each module answers for its own
/// records and its own permissions; the Projects module composes the sections. Nothing here is money the person
/// is owed as salary: payroll stays in HR.
/// </summary>
public interface IPersonalOverviewSource
{
    /// <summary>The section name, unique across modules (for example <c>Funding</c>, <c>Labor</c>, <c>Hr</c>).</summary>
    string Section { get; }

    /// <summary>The section for this person, or <c>null</c> when the module has nothing to say to them (no permission, no records).</summary>
    Task<PersonalOverviewSection?> LoadAsync(PersonalOverviewQuery query, CancellationToken cancellationToken);
}

/// <param name="ProjectIds">The projects the person may see and work on (active memberships; every project for an Executive or Accountant).</param>
public sealed record PersonalOverviewQuery(System.Security.Claims.ClaimsPrincipal Caller, int UserId, IReadOnlyCollection<int> ProjectIds, DateOnly Today, int Limit);

/// <param name="Figures">Named figures of the section (for example <c>pendingFundingAmount</c>, <c>sheetsDraft</c>, <c>requestsOpen</c>).</param>
/// <param name="Items">The most recent or most pressing records, newest first.</param>
public sealed record PersonalOverviewSection(string Section, IReadOnlyDictionary<string, decimal> Figures, IReadOnlyList<PersonalOverviewItem> Items);

/// <param name="Path">The API path of the record, so the app opens it.</param>
public sealed record PersonalOverviewItem(
    string RecordType, int RecordId, int? ProjectId, string Status, DateOnly Date, DateTime Since, string TitleAr, string TitleEn, decimal? Amount, string? Note, string Path);
