using AlMotaheda.Modules.Access.Common;
using AlMotaheda.Modules.Access.Data;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Access.Features.Users;

/// <summary>The editable user fields, cleaned and validated the same way by create and update.</summary>
internal sealed record UserFields(
    string? Email,
    string? Phone,
    string? NameAr,
    string? NameEn,
    string? JobTitleAr,
    string? JobTitleEn,
    string? Role)
{
    public static UserFields Validate(
        string? email,
        string? phone,
        string? nameAr,
        string? nameEn,
        string? jobTitleAr,
        string? jobTitleEn,
        string? role,
        ValidationErrors errors)
    {
        var cleanRole = InputRules.Clean(role);
        if (cleanRole is null)
        {
            errors.Add("role", "Role is required.");
        }
        else if (!PermissionCatalog.IsKnownRole(cleanRole))
        {
            errors.Add("role", "Unknown role.");
        }

        return new UserFields(
            InputRules.Email(email, errors),
            InputRules.Phone(phone, errors),
            InputRules.RequiredText(nameAr, InputRules.NameMaxLength, errors, "nameAr", "Arabic name"),
            InputRules.RequiredText(nameEn, InputRules.NameMaxLength, errors, "nameEn", "English name"),
            InputRules.OptionalText(jobTitleAr, InputRules.JobTitleMaxLength, errors, "jobTitleAr", "Arabic job title"),
            InputRules.OptionalText(jobTitleEn, InputRules.JobTitleMaxLength, errors, "jobTitleEn", "English job title"),
            cleanRole);
    }

    /// <summary>
    /// 409 when another user already has the email or phone. The unique indexes still catch a race between
    /// this check and the save.
    /// </summary>
    public static async Task<ProblemHttpResult?> FindConflictAsync(
        AccessDbContext db, UserFields fields, int? exceptUserId, CancellationToken cancellationToken)
    {
        var normalizedEmail = InputRules.NormalizeEmail(fields.Email!);

        if (await db.Users.AnyAsync(user => user.NormalizedEmail == normalizedEmail && user.Id != exceptUserId, cancellationToken))
        {
            return AccessProblems.EmailAlreadyUsed();
        }

        if (fields.Phone is not null
            && await db.Users.AnyAsync(user => user.Phone == fields.Phone && user.Id != exceptUserId, cancellationToken))
        {
            return AccessProblems.PhoneAlreadyUsed();
        }

        return null;
    }
}
