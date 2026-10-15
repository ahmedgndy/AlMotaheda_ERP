using AlMotaheda.Modules.Workforce.Features.Sheets;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Workforce.Common;

/// <summary>Settings of the Workforce module (section <c>Workforce</c>). Both have the prototype's defaults.</summary>
internal sealed class WorkforceOptions
{
    public const string SectionName = "Workforce";

    /// <summary>The hours of a regular day. Overtime pay per hour is the daily rate divided by this.</summary>
    public decimal RegularHours { get; set; } = 8m;

    /// <summary>How much more an overtime hour pays than a regular hour (decision W3: 1.5).</summary>
    public decimal OvertimeMultiplier { get; set; } = 1.5m;

    public LaborPaySettings PaySettings => new(RegularHours, OvertimeMultiplier);
}

/// <summary>Stops the application at startup when a setting would make the pay arithmetic meaningless.</summary>
internal sealed class WorkforceOptionsValidator : IValidateOptions<WorkforceOptions>
{
    public ValidateOptionsResult Validate(string? name, WorkforceOptions options)
    {
        var failures = new List<string>();

        if (options.RegularHours is <= 0 or > 24)
        {
            failures.Add("Workforce:RegularHours must be above 0 and at most 24.");
        }

        if (options.OvertimeMultiplier is < 1 or > 5)
        {
            failures.Add("Workforce:OvertimeMultiplier must be between 1 and 5.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
