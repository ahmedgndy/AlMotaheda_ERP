using AlMotaheda.BuildingBlocks.Authorization;
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
/// <c>POST /api/users</c>: an Executive creates a user with a first password. The user must change it at
/// their first sign-in (Q4).
/// </summary>
internal static class CreateUser
{
    internal sealed record Request(
        string? Email,
        string? Phone,
        string? NameAr,
        string? NameEn,
        string? JobTitleAr,
        string? JobTitleEn,
        string? Role,
        string? InitialPassword);

    public static void Map(IEndpointRouteBuilder users) =>
        users.MapPost("/", HandleAsync)
            .RequireExecutive()
            .WithName("CreateUser")
            .WithSummary("Create a user (Executive only)");

    internal static async Task<Results<Created<UserDetails>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        Request request,
        HttpContext httpContext,
        AccessDbContext db,
        UserManagementGate gate,
        PasswordService passwords,
        TimeProvider time,
        SecurityLog log,
        CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        var fields = UserFields.Validate(request.Email, request.Phone, request.NameAr, request.NameEn,
            request.JobTitleAr, request.JobTitleEn, request.Role, errors);
        InputRules.NewPassword(request.InitialPassword, fields.Email, errors, "initialPassword");

        if (errors.HasErrors)
        {
            return AccessProblems.Validation(errors, "initialPassword");
        }

        var entry = await gate.EnterAsync(httpContext.User, "CreateUser", cancellationToken);
        if (entry.Refusal is not null)
        {
            return entry.Refusal;
        }

        await using var scope = entry.Scope!;

        if (await UserFields.FindConflictAsync(db, fields, exceptUserId: null, cancellationToken) is { } conflict)
        {
            return conflict;
        }

        var now = time.GetUtcNow().UtcDateTime;
        var user = new User
        {
            Email = fields.Email!,
            NormalizedEmail = InputRules.NormalizeEmail(fields.Email!),
            Phone = fields.Phone,
            NameAr = fields.NameAr!,
            NameEn = fields.NameEn!,
            JobTitleAr = fields.JobTitleAr,
            JobTitleEn = fields.JobTitleEn,
            RoleId = await db.Roles.Where(role => role.Code == fields.Role).Select(role => role.Id).SingleAsync(cancellationToken),
            Status = UserStatus.Active,
            MustChangePassword = true,
            CreatedAt = now,
            CreatedBy = scope.ActorId,
            SignInState = new UserSignInState(),
        };
        user.PasswordHash = passwords.Hash(user, request.InitialPassword!);
        db.Users.Add(user);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (UniqueViolations.Classify(exception) is UniqueViolation.Email or UniqueViolation.Phone)
        {
            // Another request created the same email or phone between the check above and this insert.
            return UniqueViolations.Classify(exception) == UniqueViolation.Email
                ? AccessProblems.EmailAlreadyUsed()
                : AccessProblems.PhoneAlreadyUsed();
        }

        await scope.CommitAsync(cancellationToken);
        log.UserCreated(scope.ActorId, user.Id, fields.Role!);

        var details = await UserDetails.LoadAsync(db, user.Id, now, cancellationToken);
        return TypedResults.Created($"/api/users/{user.Id}", details);
    }
}
