using AlMotaheda.BuildingBlocks.Authorization;
using AlMotaheda.BuildingBlocks.Http;
using AlMotaheda.Modules.Access.Common;
using AlMotaheda.Modules.Access.Data;
using AlMotaheda.Modules.Access.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Access.Features.Users;

/// <summary>
/// <c>PUT /api/users/{id}</c>: an Executive edits a user's details and role.
/// </summary>
/// <remarks>
/// A new role applies to a signed-in user at their next refresh, within 15 minutes (Q8).
/// Nobody can change their own role, and the last active Executive can't be given another role.
/// </remarks>
internal static class UpdateUser
{
    internal sealed record Request(
        string? Email,
        string? Phone,
        string? NameAr,
        string? NameEn,
        string? JobTitleAr,
        string? JobTitleEn,
        string? Role,
        string? RowVersion);

    public static void Map(IEndpointRouteBuilder users) =>
        users.MapPut("/{id:int}", HandleAsync)
            .RequireExecutive()
            .WithName("UpdateUser")
            .WithSummary("Edit a user's details and role (Executive only)");

    internal static async Task<Results<Ok<UserDetails>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        int id,
        Request request,
        HttpContext httpContext,
        AccessDbContext db,
        UserManagementGate gate,
        TimeProvider time,
        SecurityLog log,
        CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var fields = UserFields.Validate(request.Email, request.Phone, request.NameAr, request.NameEn,
            request.JobTitleAr, request.JobTitleEn, request.Role, errors);
        var rowVersion = RowVersions.Parse(request.RowVersion, errors);

        if (errors.HasErrors)
        {
            return AccessProblems.Validation(errors);
        }

        var entry = await gate.EnterAsync(httpContext.User, "UpdateUser", cancellationToken);
        if (entry.Refusal is not null)
        {
            return entry.Refusal;
        }

        await using var scope = entry.Scope!;

        var user = await UserRowLock.LoadForUpdateAsync(db, id, cancellationToken);
        if (user is null)
        {
            return ApiProblem.NotFoundResult("The user was not found.");
        }

        var oldRole = user.Role.Code;
        var roleChanges = fields.Role != oldRole;

        if (roleChanges && user.Id == scope.ActorId)
        {
            log.ActionRefused(scope.ActorId, "UpdateUser", "changing own role");
            return AccessProblems.SelfChange();
        }

        if (roleChanges && oldRole == RoleCodes.Executive && scope.IsLastActiveExecutive(user.Id))
        {
            log.ActionRefused(scope.ActorId, "UpdateUser", "would leave no active Executive");
            return AccessProblems.WouldRemoveLastExecutive();
        }

        if (await UserFields.FindConflictAsync(db, fields, exceptUserId: user.Id, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var changedFields = ChangedFields(user, fields);

        user.Email = fields.Email!;
        user.NormalizedEmail = InputRules.NormalizeEmail(fields.Email!);
        user.Phone = fields.Phone;
        user.NameAr = fields.NameAr!;
        user.NameEn = fields.NameEn!;
        user.JobTitleAr = fields.JobTitleAr;
        user.JobTitleEn = fields.JobTitleEn;
        user.UpdatedAt = now;
        user.UpdatedBy = scope.ActorId;

        if (roleChanges)
        {
            user.RoleId = await db.Roles.Where(role => role.Code == fields.Role).Select(role => role.Id).SingleAsync(cancellationToken);

            // Executives can't have their own permissions (Q9), so a promotion clears them.
            if (fields.Role == RoleCodes.Executive)
            {
                await db.UserPermissionOverrides.Where(link => link.UserId == user.Id).ExecuteDeleteAsync(cancellationToken);
            }
        }

        // Saves only if nobody changed the user since the caller read it.
        db.Entry(user).Property(candidate => candidate.RowVersion).OriginalValue = rowVersion!;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ApiProblem.ConcurrencyConflictResult();
        }
        catch (DbUpdateException exception) when (UniqueViolations.Classify(exception) is UniqueViolation.Email or UniqueViolation.Phone)
        {
            return UniqueViolations.Classify(exception) == UniqueViolation.Email
                ? AccessProblems.EmailAlreadyUsed()
                : AccessProblems.PhoneAlreadyUsed();
        }

        await scope.CommitAsync(cancellationToken);
        log.UserUpdated(scope.ActorId, user.Id, changedFields, oldRole, fields.Role!);

        var details = await UserDetails.LoadAsync(db, user.Id, now, cancellationToken);
        return TypedResults.Ok(details!);
    }

    /// <summary>For the security log: which fields change, by name only (never the values).</summary>
    private static string ChangedFields(User user, UserFields fields)
    {
        var changed = new List<string>();
        if (user.NormalizedEmail != InputRules.NormalizeEmail(fields.Email!)) changed.Add("email");
        if (user.Phone != fields.Phone) changed.Add("phone");
        if (user.NameAr != fields.NameAr || user.NameEn != fields.NameEn) changed.Add("name");
        if (user.JobTitleAr != fields.JobTitleAr || user.JobTitleEn != fields.JobTitleEn) changed.Add("jobTitle");
        if (user.Role.Code != fields.Role) changed.Add("role");
        return changed.Count == 0 ? "none" : string.Join(",", changed);
    }
}
