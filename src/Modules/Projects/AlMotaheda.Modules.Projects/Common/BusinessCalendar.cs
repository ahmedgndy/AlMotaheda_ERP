using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Projects.Common;

/// <summary>Settings of the Projects module (section <c>Projects</c>).</summary>
internal sealed class ProjectsOptions
{
    public const string SectionName = "Projects";

    /// <summary>
    /// The company's time zone (IANA id). "Today" for business dates, such as the default start date of a
    /// membership, is the date in this zone, not in UTC.
    /// </summary>
    public string TimeZone { get; set; } = "Africa/Cairo";

    /// <summary>
    /// What a purchase above its reference price range does: <c>Warn</c> (default: saved with the warning and a
    /// justification), <c>ExecutiveApproval</c> (only an Executive may approve it) or <c>Block</c> (refused).
    /// </summary>
    public Data.PriceCheckPolicy PriceCheckPolicy { get; set; } = Data.PriceCheckPolicy.Warn;

    /// <summary>
    /// What a payment out of a cash custody (a purchase, an expense or a weekly payroll) does when it is more than the
    /// custody holds: <c>Warn</c> (default: saved, and the balance shows negative, money the company owes the holder,
    /// until it is funded) or <c>Block</c> (refused with 409 <c>custody_insufficient</c>).
    /// </summary>
    public string CustodyBalanceCheck { get; set; } = Contracts.CustodyBalancePolicies.Warn;

    public bool BlocksCustodyOverdraft => CustodyBalanceCheck == Contracts.CustodyBalancePolicies.Block;
}

internal sealed class ProjectsOptionsValidator : IValidateOptions<ProjectsOptions>
{
    public ValidateOptionsResult Validate(string? name, ProjectsOptions options)
    {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(options.TimeZone ?? "", out _))
        {
            return ValidateOptionsResult.Fail($"Projects:TimeZone '{options.TimeZone}' is not a time zone known to this server.");
        }

        return options.CustodyBalanceCheck is Contracts.CustodyBalancePolicies.Warn or Contracts.CustodyBalancePolicies.Block
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Projects:CustodyBalanceCheck must be Warn or Block, not '{options.CustodyBalanceCheck}'.");
    }
}

/// <summary>The current moment (UTC) and today's business date in the company's time zone.</summary>
internal sealed class BusinessCalendar(TimeProvider time, IOptions<ProjectsOptions> options)
{
    private readonly TimeZoneInfo _zone = TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    public DateOnly Today => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), _zone).DateTime);
}
