using System.Security.Claims;

namespace AlMotaheda.Modules.Projects.Contracts;

/// <summary>Which projects a caller may see, for lists that span projects (payment requests, the ledger, reports).</summary>
public interface IVisibleProjects
{
    /// <summary>The ids of the projects the caller may see, or <c>null</c> when the caller sees every project.</summary>
    Task<IReadOnlySet<int>?> IdsAsync(ClaimsPrincipal caller, CancellationToken cancellationToken);
}
