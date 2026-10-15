using System.Net.Mail;
using System.Text.RegularExpressions;

namespace AlMotaheda.Modules.Access.Common;

/// <summary>
/// The rules for user fields, shared by every slice that accepts them. The server is the authority:
/// the frontend may check the same rules for convenience, but these always run.
/// </summary>
internal static partial class InputRules
{
    public const int EmailMaxLength = 256;
    public const int NameMaxLength = 150;
    public const int JobTitleMaxLength = 100;

    /// <summary>Q7: at least 10 characters.</summary>
    public const int PasswordMinLength = 10;

    /// <summary>Upper bound so a huge "password" can't be used to make hashing expensive.</summary>
    public const int PasswordMaxLength = 128;

    /// <summary>Trims text and turns empty text into <c>null</c>.</summary>
    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

    /// <summary>Validates and cleans an email. Returns <c>null</c> (and adds an error) when it isn't valid.</summary>
    public static string? Email(string? value, ValidationErrors errors, string field = "email")
    {
        var email = Clean(value);
        if (email is null)
        {
            errors.Add(field, "Email is required.");
            return null;
        }

        if (email.Length > EmailMaxLength
            || !MailAddress.TryCreate(email, out var parsed)
            || !string.Equals(parsed.Address, email, StringComparison.Ordinal))
        {
            errors.Add(field, "Enter a valid email address.");
            return null;
        }

        return email;
    }

    /// <summary>Cleans an optional phone number: spaces, dashes, dots and brackets are removed; <c>+</c> and digits remain.</summary>
    public static string? Phone(string? value, ValidationErrors errors, string field = "phone")
    {
        var phone = Clean(value);
        if (phone is null)
        {
            return null;
        }

        phone = PhoneSeparators().Replace(phone, "");
        if (!PhonePattern().IsMatch(phone))
        {
            errors.Add(field, "Enter a phone number with 7 to 15 digits, optionally starting with +.");
            return null;
        }

        return phone;
    }

    public static string? RequiredText(string? value, int maxLength, ValidationErrors errors, string field, string label)
    {
        var text = Clean(value);
        if (text is null)
        {
            errors.Add(field, $"{label} is required.");
        }
        else if (text.Length > maxLength)
        {
            errors.Add(field, $"{label} must be at most {maxLength} characters.");
        }

        return text;
    }

    public static string? OptionalText(string? value, int maxLength, ValidationErrors errors, string field, string label)
    {
        var text = Clean(value);
        if (text is not null && text.Length > maxLength)
        {
            errors.Add(field, $"{label} must be at most {maxLength} characters.");
        }

        return text;
    }

    /// <summary>
    /// Q7: 10 to 128 characters and not the user's own email. No forced symbols and no expiry,
    /// in line with NIST SP 800-63B. Adds the error under <paramref name="field"/>.
    /// </summary>
    public static void NewPassword(string? password, string? email, ValidationErrors errors, string field)
    {
        if (string.IsNullOrEmpty(password))
        {
            errors.Add(field, "Password is required.");
            return;
        }

        if (password.Length < PasswordMinLength || password.Length > PasswordMaxLength)
        {
            errors.Add(field, $"Password must be {PasswordMinLength} to {PasswordMaxLength} characters long.");
        }

        if (email is not null && string.Equals(password.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(field, "Password must not be the same as the email address.");
        }
    }

    [GeneratedRegex(@"[\s\-\.\(\)]")]
    private static partial Regex PhoneSeparators();

    [GeneratedRegex(@"^\+?[0-9]{7,15}$")]
    private static partial Regex PhonePattern();
}
