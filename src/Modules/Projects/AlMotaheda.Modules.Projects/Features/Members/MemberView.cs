using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Common;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;

namespace AlMotaheda.Modules.Projects.Features.Members;

/// <summary>One membership, with the person's current account details from Access.</summary>
/// <param name="FitsResponsibility">
/// False when the person's role has changed since they were assigned and no longer fits (for example a project
/// manager who is now an Engineer), or the account is disabled. The Executive should replace or end them (D10).
/// </param>
/// <param name="Phone">Shown on the team list, as in the prototype (phone and e-mail columns).</param>
/// <param name="EndReason">Free text written by the Executive who ended the membership. Shown to Executives only.</param>
internal sealed record MemberView(
    int MemberId,
    int UserId,
    string NameAr,
    string NameEn,
    string? JobTitleAr,
    string? JobTitleEn,
    string? RoleCode,
    bool UserIsActive,
    string? Phone,
    string? Email,
    string Responsibility,
    bool FitsResponsibility,
    DateOnly StartDate,
    DateOnly? EndDate,
    DateTime AssignedAt,
    int AssignedBy,
    DateTime? EndedAt,
    int? EndedBy,
    string? EndReason,
    string RowVersion)
{
    /// <param name="showEndDetails">
    /// False for callers other than an Executive: the end reason is free text about a person and stays with the
    /// Executives who write it.
    /// </param>
    public static MemberView From(ProjectMember member, IReadOnlyDictionary<int, UserSummary> people, bool showEndDetails)
    {
        people.TryGetValue(member.UserId, out var user);

        return new MemberView(
            member.Id,
            member.UserId,
            user?.NameAr ?? $"#{member.UserId}",
            user?.NameEn ?? $"#{member.UserId}",
            user?.JobTitleAr,
            user?.JobTitleEn,
            user?.RoleCode,
            user?.IsActive ?? false,
            user?.Phone,
            user?.Email,
            member.Responsibility.ToString(),
            user is not null && user.IsActive && ResponsibilityRoles.Fits(member.Responsibility, user.RoleCode),
            member.StartDate,
            member.EndDate,
            member.AssignedAt,
            member.AssignedBy,
            member.EndedAt,
            member.EndedBy,
            showEndDetails ? member.EndReason : null,
            RowVersions.Format(member.RowVersion));
    }
}
