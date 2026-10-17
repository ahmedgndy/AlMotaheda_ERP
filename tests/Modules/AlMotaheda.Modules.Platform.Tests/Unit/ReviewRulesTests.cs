using AlMotaheda.BuildingBlocks.Data;
using AlMotaheda.Modules.Platform.Notifications;

namespace AlMotaheda.Modules.Platform.Tests.Unit;

/// <summary>Which changes mean "something waits for your review" (docs/cost-flow §2.8), from the changed record alone.</summary>
public sealed class ReviewRulesTests
{
    private static EntityChange Change(string entity, string action, string? before, string? after) =>
        new("projects", entity, "5", action, 3, before, after, new Dictionary<string, int> { ["CreatedBy"] = 11 }, ["Status"]);

    [Theory]
    [InlineData("ProjectExpense", "Added", null, "Recorded", "projects")]
    [InlineData("ProjectExpense", "Modified", "Returned", "Recorded", "projects")]
    [InlineData("EquipmentCost", "Added", null, "Recorded", "equipment")]
    [InlineData("RepairRequest", "Added", null, "Open", "equipment")]
    [InlineData("DailySheet", "Modified", "Draft", "Submitted", "workers")]
    [InlineData("ItemPrice", "Added", null, "Pending", "catalog")]
    [InlineData("MasterItemPrice", "Added", null, "Pending", "catalog")]
    [InlineData("PriceVariance", "Added", null, "Open", "catalog")]
    [InlineData("SalarySheet", "Modified", "Draft", "Submitted", "hr")]
    [InlineData("ProjectWorkDays", "Added", null, "Recorded", "hr")]
    public void A_record_submitted_for_review_tells_the_holders_of_the_reviewing_permission(string entity, string action, string? before, string after, string module) =>
        Assert.Equal(module, ReviewRules.For(Change(entity, action, before, after))!.Module);

    [Theory]
    [InlineData("ProjectExpense", "Modified", "Recorded", "Recorded")]   // an edit while it waits: already announced
    [InlineData("ProjectExpense", "Added", null, "Approved")]            // posted from a matched invoice: nothing to review
    [InlineData("ProjectExpense", "Modified", "Recorded", "Approved")]
    [InlineData("ItemPrice", "Added", null, "Approved")]                 // an item's first price
    [InlineData("PriceVariance", "Modified", "Open", "Accepted")]
    [InlineData("Budget", "Added", null, "Draft")]
    public void Other_changes_ask_nobody_to_review(string entity, string action, string? before, string after) =>
        Assert.Null(ReviewRules.For(Change(entity, action, before, after)));

    [Fact]
    public void The_titles_name_the_record_and_never_an_amount()
    {
        var (en, ar) = ReviewRules.Titles(ReviewRules.For(Change("PriceVariance", "Added", null, "Open"))!);
        Assert.Equal("A new price variance waits for your review", en);
        Assert.DoesNotMatch("[0-9]", en + ar);
    }
}
