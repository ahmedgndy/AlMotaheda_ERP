using AlMotaheda.BuildingBlocks.Data;
using AlMotaheda.Modules.Platform.Live;

namespace AlMotaheda.Modules.Platform.Tests.Unit;

/// <summary>Cache removal comes from the same dependency map as the live signals.</summary>
public sealed class CacheTagsTests
{
    private static EntityChange Change(string entity, int? projectId) => new("Module", entity, "1", "Modified", projectId, null, null, new Dictionary<string, int>(), []);

    [Fact]
    public void A_record_on_a_project_removes_that_projects_aggregates()
    {
        var tags = LiveScopes.CacheTagsFor(Change("ProjectExpense", 5)).ToList();

        Assert.Contains("agg:ProjectOverview:5", tags);
        Assert.Contains("agg:SupplierBalances:5", tags);
        Assert.DoesNotContain("agg:ProjectOverview", tags);
    }

    [Fact]
    public void A_record_without_a_project_removes_the_aggregate_on_every_project()
    {
        Assert.Contains("agg:ProjectOverview", LiveScopes.CacheTagsFor(Change("WorkerPayment", null)));
        Assert.Contains("agg:ProjectOverview", LiveScopes.CacheTagsFor(Change("LaborAllocation", null)));
    }

    [Fact]
    public void Reference_data_removes_its_own_tag_and_detail_rows_remove_nothing()
    {
        Assert.Equal(["agg:Trade"], LiveScopes.CacheTagsFor(Change("Trade", null)));
        Assert.Equal(["agg:RolePermission"], LiveScopes.CacheTagsFor(Change("RolePermission", null)));
        Assert.Empty(LiveScopes.CacheTagsFor(Change("AttendanceLine", 3)));
    }

    [Fact]
    public void A_project_entry_is_tagged_for_its_project_and_for_every_project()
    {
        Assert.Equal(["agg:ProjectOverview:9", "agg:ProjectOverview"], CacheTags.ForProject("ProjectOverview", 9));
    }
}
