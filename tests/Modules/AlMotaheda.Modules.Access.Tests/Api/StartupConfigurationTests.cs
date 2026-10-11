using AlMotaheda.Api.Tests;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Access.Tests.Api;

/// <summary>Missing or weak security settings stop the API at startup, not at the first sign-in.</summary>
public sealed class StartupConfigurationTests
{
    [Theory]
    [InlineData("")]
    [InlineData("dG9vLXNob3J0")]
    [InlineData("a long human passphrase is not a random key!")]
    public async Task Api_refuses_to_start_without_a_strong_signing_key(string key)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(() => TestApplication.StartAsync(configuration: new Dictionary<string, string?>
        {
            ["Access:Tokens:SigningKey"] = key,
        }));

        var validation = Assert.IsType<OptionsValidationException>(error is AggregateException aggregate ? aggregate.InnerException : error);
        Assert.Contains("SigningKey", validation.Message);
        if (key.Length > 0)
        {
            Assert.DoesNotContain(key, validation.Message);
        }
    }

    [Fact]
    public async Task Api_refuses_to_start_without_a_connection_string()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => TestApplication.StartAsync(configuration: new Dictionary<string, string?>
        {
            ["ConnectionStrings:Erp"] = "",
        }));

        Assert.Contains("ConnectionStrings:Erp", error.Message);
    }
}
