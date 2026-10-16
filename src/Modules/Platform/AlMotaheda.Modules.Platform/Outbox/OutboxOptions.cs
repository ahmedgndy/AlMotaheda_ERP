using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Platform.Outbox;

/// <summary>Settings of the outbox dispatcher (<c>Platform:Outbox</c>).</summary>
internal sealed class OutboxOptions
{
    public const string SectionName = "Platform:Outbox";

    /// <summary>Off only for test hosts without a database.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long to wait when there is nothing to handle.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Messages handled in one transaction.</summary>
    public int BatchSize { get; set; } = 50;

    /// <summary>After this many failures a message is skipped and stays in the table for a person to look at.</summary>
    public int MaxAttempts { get; set; } = 10;
}

internal sealed class OutboxOptionsValidator : IValidateOptions<OutboxOptions>
{
    public ValidateOptionsResult Validate(string? name, OutboxOptions options)
    {
        var failures = new List<string>();
        if (options.PollInterval < TimeSpan.FromMilliseconds(100) || options.PollInterval > TimeSpan.FromMinutes(5))
        {
            failures.Add($"{OutboxOptions.SectionName}:PollInterval must be between 00:00:00.1 and 00:05:00.");
        }

        if (options.BatchSize is < 1 or > 500)
        {
            failures.Add($"{OutboxOptions.SectionName}:BatchSize must be between 1 and 500.");
        }

        if (options.MaxAttempts is < 1 or > 100)
        {
            failures.Add($"{OutboxOptions.SectionName}:MaxAttempts must be between 1 and 100.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
