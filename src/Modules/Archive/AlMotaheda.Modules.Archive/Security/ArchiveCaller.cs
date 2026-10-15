using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Authorization;

namespace AlMotaheda.Modules.Archive.Security;

/// <summary>The signed-in caller, as read from the access token, with the archive's permission rules.</summary>
/// <remarks>
/// The archive has no permission module of its own (decision A1): it uses <c>projects</c>. Which projects the caller
/// may open is not in the token; it is asked from the Projects module on every request (<c>IProjectAccess</c>).
/// </remarks>
internal sealed class ArchiveCaller
{
    /// <summary>The permission module the archive checks.</summary>
    public const string Module = "projects";

    private readonly ClaimsPrincipal _principal;

    private ArchiveCaller(ClaimsPrincipal principal, int userId)
    {
        _principal = principal;
        UserId = userId;
    }

    public int UserId { get; }

    public bool Can(PermissionAction action) => _principal.HasPermission(Module, action);

    /// <summary>Upload needs projects Create or projects Edit.</summary>
    public bool CanUpload => Can(PermissionAction.Create) || Can(PermissionAction.Edit);

    /// <summary>
    /// Changing or deleting a document: anyone with projects Edit, or the person who uploaded it while they still
    /// may upload. Someone whose upload permission was taken away can no longer change what they uploaded.
    /// </summary>
    public bool CanChange(int uploadedBy) => Can(PermissionAction.Edit) || (uploadedBy == UserId && CanUpload);

    /// <summary>The caller, or <c>null</c> for a token without a user id (never issued by Access).</summary>
    public static ArchiveCaller? From(ClaimsPrincipal principal) =>
        principal.GetUserId() is { } userId ? new ArchiveCaller(principal, userId) : null;
}
