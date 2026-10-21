using AlMotaheda.Modules.Platform.Data;
using AlMotaheda.Modules.Platform.Features.Audit;
using AlMotaheda.Modules.Platform.Features.Notifications;
using Microsoft.EntityFrameworkCore;

namespace AlMotaheda.Modules.Platform.Tests.Unit;

/// <summary>Every Platform query translates to SQL Server SQL, and filters where it should.</summary>
public sealed class PlatformQueryTests
{
    private static PlatformDbContext CreateContext() => new PlatformDbContextDesignTimeFactory().CreateDbContext([]);

    [Fact]
    public void Notifications_are_always_filtered_by_their_recipient()
    {
        using var db = CreateContext();

        var all = NotificationQueries.Views(NotificationQueries.Of(db, 7, unreadOnly: false)).ToQueryString();
        var unread = NotificationQueries.Of(db, 7, unreadOnly: true).ToQueryString();

        Assert.Contains("[n].[RecipientUserId] = @", all);
        Assert.Contains("ORDER BY [n].[Id] DESC", all);
        Assert.Contains("[n].[ReadAt] IS NULL", unread);
    }

    [Fact]
    public void The_audit_trail_applies_every_filter_in_sql()
    {
        using var db = CreateContext();
        var filter = new ListAuditEntries.Filter(
            "projects", "ProjectExpense", "5", 3, 9, new DateTime(2026, 9, 30, 21, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 31, 22, 0, 0, DateTimeKind.Utc));

        var sql = ListAuditEntries.Query(db, filter).ToQueryString();

        Assert.Contains("[a].[Module] = @", sql);
        Assert.Contains("[a].[EntityType] = @", sql);
        Assert.Contains("[a].[EntityKey] = @", sql);
        Assert.Contains("[a].[ProjectId] = @", sql);
        Assert.Contains("[a].[UserId] = @", sql);
        Assert.Contains("[a].[OccurredAt] >= @", sql);
        Assert.Contains("[a].[OccurredAt] < @", sql);
        Assert.Contains("ORDER BY [a].[Id] DESC", sql);
    }

    [Fact]
    public void The_audit_trail_filters_by_action_and_by_the_status_a_change_set()
    {
        using var db = new PlatformDbContextDesignTimeFactory().CreateDbContext([]);
        var filter = new ListAuditEntries.Filter(null, "CustodyTransfer", null, null, null, null, null, "Modified", "Approved");

        var sql = ListAuditEntries.Query(db, filter).ToQueryString();

        Assert.Contains("[Action] = @", sql);
        Assert.Contains("[Changes] LIKE @", sql);
        Assert.Contains("\"Status\":[", sql);
    }
}
