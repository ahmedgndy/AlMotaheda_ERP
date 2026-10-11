using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.Modules.Access.Common;
using AlMotaheda.Modules.Access.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Access.Features.Users;

/// <summary>
/// <c>GET /api/users</c>: one page of users, with search and filters, for the Accounts → Users screen.
/// </summary>
internal static class ListUsers
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 100;

    /// <summary>Keeps the row offset well inside SQL Server's range.</summary>
    public const int MaxPage = 100_000;

    internal sealed record Item(
        int Id,
        string Email,
        string? Phone,
        string NameAr,
        string NameEn,
        string? JobTitleAr,
        string? JobTitleEn,
        string Role,
        string Status,
        bool IsLockedOut,
        bool HasPermissionOverrides,
        DateTime? LastLoginAt);

    internal sealed record Response(IReadOnlyList<Item> Items, int Page, int PageSize, int TotalCount);

    public static void Map(IEndpointRouteBuilder users) =>
        users.MapGet("/", HandleAsync)
            .RequirePermission(PermissionCatalog.AccountsModule, PermissionAction.View)
            .WithName("ListUsers")
            .WithSummary("List users (search: name, email or phone; sort: name, nameAr, lastLogin or created)");

    internal static async Task<Results<Ok<Response>, ValidationProblem>> HandleAsync(
        AccessDbContext db,
        TimeProvider time,
        CancellationToken cancellationToken,
        string? search = null,
        string? role = null,
        string? status = null,
        string? sort = null,
        int page = 1,
        int pageSize = DefaultPageSize)
    {
        var errors = new ValidationErrors();
        if (page is < 1 or > MaxPage)
        {
            errors.Add("page", $"page must be between 1 and {MaxPage}.");
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            errors.Add("pageSize", $"pageSize must be between 1 and {MaxPageSize}.");
        }

        role = InputRules.Clean(role);
        if (role is not null && !PermissionCatalog.IsKnownRole(role))
        {
            errors.Add("role", "Unknown role.");
        }

        UserStatus? statusFilter = null;
        if (InputRules.Clean(status) is { } statusText)
        {
            statusFilter = Enum.GetValues<UserStatus>()
                .Cast<UserStatus?>()
                .FirstOrDefault(candidate => string.Equals(candidate.ToString(), statusText, StringComparison.OrdinalIgnoreCase));

            if (statusFilter is null)
            {
                errors.Add("status", "status must be Active or Disabled.");
            }
        }

        sort = InputRules.Clean(sort)?.ToLowerInvariant() ?? "name";
        if (sort is not ("name" or "namear" or "lastlogin" or "created"))
        {
            errors.Add("sort", "sort must be name, nameAr, lastLogin or created.");
        }

        if (errors.HasErrors)
        {
            return AccessProblems.Validation(errors);
        }

        var query = db.Users.AsNoTracking();

        if (InputRules.Clean(search) is { } text)
        {
            var upper = text.ToUpperInvariant();
            query = query.Where(user =>
                user.NameEn.Contains(text)
                || user.NameAr.Contains(text)
                || user.NormalizedEmail.Contains(upper)
                || (user.Phone != null && user.Phone.Contains(text)));
        }

        if (role is not null)
        {
            query = query.Where(user => user.Role.Code == role);
        }

        if (statusFilter is not null)
        {
            query = query.Where(user => user.Status == statusFilter);
        }

        var totalCount = await query.CountAsync(cancellationToken);

        query = sort switch
        {
            "namear" => query.OrderBy(user => user.NameAr).ThenBy(user => user.Id),
            "lastlogin" => query.OrderByDescending(user => user.SignInState.LastLoginAt).ThenBy(user => user.Id),
            "created" => query.OrderByDescending(user => user.CreatedAt).ThenBy(user => user.Id),
            _ => query.OrderBy(user => user.NameEn).ThenBy(user => user.Id),
        };

        var now = time.GetUtcNow().UtcDateTime;

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(user => new Item(
                user.Id,
                user.Email,
                user.Phone,
                user.NameAr,
                user.NameEn,
                user.JobTitleAr,
                user.JobTitleEn,
                user.Role.Code,
                user.Status == UserStatus.Active ? nameof(UserStatus.Active) : nameof(UserStatus.Disabled),
                user.SignInState.LockoutEndsAt != null && user.SignInState.LockoutEndsAt > now,
                user.PermissionOverrides.Any(),
                user.SignInState.LastLoginAt))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(new Response(items, page, pageSize, totalCount));
    }
}
