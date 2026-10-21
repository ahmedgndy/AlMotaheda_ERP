using AlMotaheda.Modules.Projects.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AlMotaheda.Modules.Finance.Common;

/// <summary>The policies of the treasury and the custody hierarchy (<c>Finance</c> section). Every value has a safe default.</summary>
internal sealed class FinanceOptions
{
    public const string SectionName = "Finance";

    /// <summary>
    /// What a period close does with the balance: <see cref="CarryForwardPolicies.CarryForward"/> (default: the
    /// balance continues into the next period) or <see cref="CarryForwardPolicies.RequireZeroBalance"/> (the
    /// period closes only once the custody was returned in full).
    /// </summary>
    public string CustodyPeriodCarryForward { get; set; } = CarryForwardPolicies.CarryForward;

    public bool RequiresZeroBalanceAtClose => CustodyPeriodCarryForward == CarryForwardPolicies.RequireZeroBalance;
}

internal static class CarryForwardPolicies
{
    public const string CarryForward = "CarryForward";
    public const string RequireZeroBalance = "RequireZeroBalance";
}

internal sealed class FinanceOptionsValidator : IValidateOptions<FinanceOptions>
{
    public ValidateOptionsResult Validate(string? name, FinanceOptions options) =>
        options.CustodyPeriodCarryForward is CarryForwardPolicies.CarryForward or CarryForwardPolicies.RequireZeroBalance
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail($"Finance:CustodyPeriodCarryForward must be CarryForward or RequireZeroBalance, not '{options.CustodyPeriodCarryForward}'.");
}

/// <summary>
/// The company's one rule for an outflow above what a custody (or the treasury) holds: the Projects module's
/// <c>Projects:CustodyBalanceCheck</c> (Warn, the default, or Block), read here so the treasury hierarchy and the
/// project custodies can never disagree.
/// </summary>
internal sealed class CustodyBalancePolicy(IConfiguration configuration)
{
    public const string Setting = "Projects:CustodyBalanceCheck";

    public string Value => configuration[Setting] is { Length: > 0 } value ? value : CustodyBalancePolicies.Warn;

    public bool Blocks => Value == CustodyBalancePolicies.Block;
}
