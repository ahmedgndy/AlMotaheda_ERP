using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Common;
using AlMotaheda.Modules.Access.Data;
using AlMotaheda.Modules.Access.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Access.Features.Auth;

/// <summary>
/// <c>GET /api/me</c>: the signed-in user's profile and effective permissions, read fresh from the database.
/// Every user can read their own, without the <c>accounts</c> permission.
/// </summary>
internal static class GetMe
{
    internal sealed record Response(
        int Id,
        string Email,
        string? Phone,
        string NameAr,
        string NameEn,
        string? JobTitleAr,
        string? JobTitleEn,
        string Role,
        bool MustChangePassword,
        DateTime? LastLoginAt,
        Dictionary<string, string[]> Permissions);

    public static void Map(IEndpointRouteBuilder api) =>
        api.MapGet("/me", HandleAsync)
            .RequireAuthorization()
            .AllowDuringPasswordChange()
            .WithTags("Authentication")
            .WithName("GetMe")
            .WithSummary("Your own profile and permissions");

    internal static async Task<Results<Ok<Response>, ProblemHttpResult>> HandleAsync(
        HttpContext httpContext,
        AccessDbContext db,
        PermissionResolver permissions,
        CancellationToken cancellationToken)
    {
        var userId = httpContext.User.GetUserId();

        var user = userId is null
            ? null
            : await db.Users
                .AsNoTracking()
                .Where(candidate => candidate.Id == userId && candidate.Status == UserStatus.Active)
                .Select(candidate => new
                {
                    candidate.Id,
                    candidate.Email,
                    candidate.Phone,
                    candidate.NameAr,
                    candidate.NameEn,
                    candidate.JobTitleAr,
                    candidate.JobTitleEn,
                    candidate.RoleId,
                    RoleCode = candidate.Role.Code,
                    candidate.MustChangePassword,
                    candidate.SignInState.LastLoginAt,
                })
                .SingleOrDefaultAsync(cancellationToken);

        if (user is null)
        {
            return AccessProblems.SessionEnded();
        }

        var effective = await permissions.ResolveAsync(user.Id, user.RoleId, user.RoleCode, cancellationToken);

        return TypedResults.Ok(new Response(
            user.Id,
            user.Email,
            user.Phone,
            user.NameAr,
            user.NameEn,
            user.JobTitleAr,
            user.JobTitleEn,
            user.RoleCode,
            user.MustChangePassword,
            user.LastLoginAt,
            effective.ToResponse()));
    }
}
