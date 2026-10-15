using AlMotaheda.BuildingBlocks.Validation;
using AlMotaheda.Modules.Access.Common;
using AlMotaheda.Modules.Access.Security;

namespace AlMotaheda.Modules.Access.Tests.Unit;

public sealed class InputRulesTests
{
    [Theory]
    [InlineData("  Ahmed@Example.com ", "Ahmed@Example.com")]
    [InlineData("a.b+c@site.co", "a.b+c@site.co")]
    public void Valid_emails_are_trimmed_and_kept_as_typed(string input, string expected)
    {
        var errors = new ValidationErrors();

        Assert.Equal(expected, InputRules.Email(input, errors));
        Assert.False(errors.HasErrors);
        Assert.Equal(expected.ToUpperInvariant(), InputRules.NormalizeEmail(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("Name <a@b.com>")]
    [InlineData("a@b.com, c@d.com")]
    public void Invalid_emails_are_rejected(string? input)
    {
        var errors = new ValidationErrors();

        Assert.Null(InputRules.Email(input, errors));
        Assert.Contains("email", errors.ToDictionary().Keys);
    }

    [Theory]
    [InlineData("+974 5555-1234", "+97455551234")]
    [InlineData("(012) 345.6789", "0123456789")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Phones_keep_only_plus_and_digits(string? input, string? expected)
    {
        var errors = new ValidationErrors();

        Assert.Equal(expected, InputRules.Phone(input, errors));
        Assert.False(errors.HasErrors);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("12+3456789")]
    [InlineData("phone-number")]
    [InlineData("+1234567890123456")]
    public void Invalid_phones_are_rejected(string input)
    {
        var errors = new ValidationErrors();

        Assert.Null(InputRules.Phone(input, errors));
        Assert.True(errors.HasErrors);
    }

    [Theory]
    [InlineData("short", false)]
    [InlineData("exactly10!", true)]
    [InlineData("a long passphrase with spaces", true)]
    [InlineData("user@site.com", false)]
    [InlineData("USER@SITE.COM", false)]
    public void Password_rules_follow_Q7(string password, bool valid)
    {
        var errors = new ValidationErrors();

        InputRules.NewPassword(password, "user@site.com", errors, "password");

        Assert.Equal(!valid, errors.HasErrors);
    }

    [Fact]
    public void Passwords_longer_than_128_characters_are_rejected()
    {
        var errors = new ValidationErrors();

        InputRules.NewPassword(new string('x', 129), null, errors, "password");

        Assert.True(errors.HasErrors);
    }

    [Fact]
    public void Validation_code_is_weak_password_only_when_password_fields_alone_failed()
    {
        var passwordOnly = new ValidationErrors();
        passwordOnly.Add("newPassword", "too short");
        var mixed = new ValidationErrors();
        mixed.Add("newPassword", "too short");
        mixed.Add("email", "missing");

        Assert.Equal("weak_password", AccessProblems.Validation(passwordOnly, "newPassword").ProblemDetails.Extensions["code"]);
        Assert.Equal("validation_failed", AccessProblems.Validation(mixed, "newPassword").ProblemDetails.Extensions["code"]);
    }

    [Fact]
    public void Password_hashes_are_salted_and_verify()
    {
        var service = new PasswordService();
        var user = new Data.User { Email = "a@b.com", NormalizedEmail = "A@B.COM", NameAr = "أ", NameEn = "A" };

        var first = service.Hash(user, "correct horse battery");
        var second = service.Hash(user, "correct horse battery");
        user.PasswordHash = first;

        Assert.NotEqual(first, second);
        Assert.DoesNotContain("correct", first, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PasswordCheck.Correct, service.Verify(user, "correct horse battery"));
        Assert.Equal(PasswordCheck.Wrong, service.Verify(user, "Correct horse battery"));
    }
}
