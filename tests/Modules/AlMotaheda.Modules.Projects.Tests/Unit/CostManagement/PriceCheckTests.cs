using AlMotaheda.Modules.Projects.Data;
using AlMotaheda.Modules.Projects.Features.Prices;

namespace AlMotaheda.Modules.Projects.Tests.Unit.CostManagement;

/// <summary>The price check of a purchase against the company price list, to the piastre, with no database.</summary>
public sealed class PriceCheckTests
{
    [Fact]
    public void Cement_at_1400_a_ton_against_a_1000_to_1200_range_is_200_a_ton_above_and_400_over_two_tons()
    {
        var comparison = PriceCheck.Compare(unitPrice: 1_400m, quantity: 2m, minPrice: 1_000m, maxPrice: 1_200m);

        Assert.Equal(PriceCheckResult.AboveRange, comparison.Result);
        Assert.Equal(200m, comparison.DifferencePerUnit);
        Assert.Equal(400m, comparison.TotalDifference);
        Assert.True(comparison.IsAboveRange);
    }

    [Theory]
    [InlineData(1_000, "WithinRange", 0, 0)]
    [InlineData(1_200, "WithinRange", 0, 0)]
    [InlineData(1_150.5, "WithinRange", 0, 0)]
    [InlineData(950, "BelowRange", -50, -125)]
    [InlineData(1_200.01, "AboveRange", 0.01, 0.03)]
    public void The_range_edges_are_inside_and_the_difference_is_rounded_to_the_piastre(decimal unitPrice, string result, decimal perUnit, decimal total)
    {
        var comparison = PriceCheck.Compare(unitPrice, quantity: 2.5m, minPrice: 1_000m, maxPrice: 1_200m);

        Assert.Equal(result, comparison.Result.ToString());
        Assert.Equal(perUnit, comparison.DifferencePerUnit);
        Assert.Equal(total, comparison.TotalDifference);
    }

    [Fact]
    public void Without_an_applicable_range_there_is_nothing_to_compare()
    {
        var comparison = PriceCheck.Compare(1_400m, 2m, null, null);

        Assert.Equal(PriceCheckResult.NoReference, comparison.Result);
        Assert.Equal(0m, comparison.TotalDifference);
    }

    [Theory]
    [InlineData("Warn", null, "NeedsJustification")]
    [InlineData("Warn", "Only supplier with stock this week", null)]
    [InlineData("ExecutiveApproval", null, "NeedsJustification")]
    [InlineData("ExecutiveApproval", "Urgent pour", null)]
    [InlineData("Block", "Urgent pour", "Blocked")]
    public void Above_the_range_the_policy_decides_and_a_justification_is_always_needed_to_go_ahead(
        string policyName, string? justification, string? expected)
    {
        var policy = Enum.Parse<PriceCheckPolicy>(policyName);
        var above = PriceCheck.Compare(1_400m, 2m, 1_000m, 1_200m);

        Assert.Equal(expected, PriceCheck.Refusal(above, policy, justification)?.ToString());
    }

    [Theory]
    [InlineData("Warn")]
    [InlineData("ExecutiveApproval")]
    [InlineData("Block")]
    public void Inside_or_below_the_range_no_policy_refuses(string policyName)
    {
        var policy = Enum.Parse<PriceCheckPolicy>(policyName);
        Assert.Null(PriceCheck.Refusal(PriceCheck.Compare(1_100m, 2m, 1_000m, 1_200m), policy, null));
        Assert.Null(PriceCheck.Refusal(PriceCheck.Compare(900m, 2m, 1_000m, 1_200m), policy, null));
    }

    [Fact]
    public void Only_the_executive_approval_policy_reserves_the_approval_of_a_purchase_above_its_range()
    {
        Assert.True(PriceCheck.NeedsExecutive(PriceCheckResult.AboveRange, PriceCheckPolicy.ExecutiveApproval));
        Assert.False(PriceCheck.NeedsExecutive(PriceCheckResult.AboveRange, PriceCheckPolicy.Warn));
        Assert.False(PriceCheck.NeedsExecutive(PriceCheckResult.WithinRange, PriceCheckPolicy.ExecutiveApproval));
        Assert.False(PriceCheck.NeedsExecutive(null, PriceCheckPolicy.ExecutiveApproval));
    }
}
