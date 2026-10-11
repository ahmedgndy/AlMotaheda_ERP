using AlMotaheda.Modules.Access.Contracts;
using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Security;

namespace AlMotaheda.Modules.Projects.Features.Members;

/// <summary>Whether a user may be given a responsibility on a project right now.</summary>
internal static class MemberAssignment
{
    /// <summary>
    /// <c>null</c> when the user exists, is active and has a role that fits the responsibility (decision D6);
    /// otherwise a message that says why not, without revealing anything beyond the user id the caller sent.
    /// </summary>
    public static string? Problem(int userId, MemberResponsibility responsibility, IReadOnlyDictionary<int, UserSummary> people)
    {
        if (!people.TryGetValue(userId, out var user))
        {
            return $"User {userId} was not found.";
        }

        if (!user.IsActive)
        {
            return $"User {userId} is disabled and can't be assigned.";
        }

        return ResponsibilityRoles.Fits(responsibility, user.RoleCode)
            ? null
            : $"User {userId} can't be a {Label(responsibility)}: it must be {ResponsibilityRoles.Describe(responsibility)}.";
    }

    public static string Label(MemberResponsibility responsibility) => responsibility switch
    {
        MemberResponsibility.ProjectManager => "project manager",
        MemberResponsibility.SiteEngineer => "site engineer",
        _ => "team member",
    };
}
