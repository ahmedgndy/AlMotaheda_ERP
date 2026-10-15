using AlMotaheda.Modules.Access.Data.Configurations;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Access.Common;

/// <summary>Recognises unique-index violations, so a lost race becomes a 409 instead of a 500.</summary>
internal static class UniqueViolations
{
    public static UniqueViolation Classify(DbUpdateException exception)
    {
        if (!SqlViolations.IsAnyUnique(exception))
        {
            return UniqueViolation.None;
        }

        if (SqlViolations.IsUnique(exception, UserConfiguration.EmailIndex))
        {
            return UniqueViolation.Email;
        }

        return SqlViolations.IsUnique(exception, UserConfiguration.PhoneIndex)
            ? UniqueViolation.Phone
            : UniqueViolation.Other;
    }
}

internal enum UniqueViolation
{
    None,
    Email,
    Phone,
    Other,
}
