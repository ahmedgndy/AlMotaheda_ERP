using System.Security.Claims;
using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Data;

namespace AlMotaheda.Modules.Access.Common;

/// <summary>Who may read a user: anyone with <c>accounts</c> View, and every user for their own record.</summary>
internal static class UserReadAccess
{
    public static bool CanRead(ClaimsPrincipal caller, int userId) =>
        caller.GetUserId() == userId || caller.HasPermission(PermissionCatalog.AccountsModule, PermissionAction.View);
}
