using System.Text.Json;
using AlMotaheda.BuildingBlocks.Data;
using AlMotaheda.Modules.Platform.Data;
using AlMotaheda.Modules.Platform.Outbox;

namespace AlMotaheda.Modules.Platform.Tests.Unit;

/// <summary>How one outbox message becomes notifications, with no database.</summary>
public sealed class OutboxHandlingTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 8, 0, 0, DateTimeKind.Utc);

    private static OutboxMessage Message(int? actor, params EntityChange[] changes) => new()
    {
        Id = 1,
        MessageId = Guid.NewGuid(),
        Type = PlatformTables.EntitiesChangedMessage,
        Payload = JsonSerializer.Serialize(new EntitiesChangedPayload(changes), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
        OccurredAt = Now,
        UserId = actor,
    };

    private static EntityChange Returned(string key) =>
        new("projects", "ProjectExpense", key, "Modified", 7, "Recorded", "Returned", new Dictionary<string, int> { ["CreatedBy"] = 11 }, ["Status"]);

    [Fact]
    public void Each_changed_record_gives_its_notification_with_a_link_to_the_record()
    {
        var message = Message(22, Returned("40"), Returned("41"));

        var notifications = OutboxDispatcher.Handle(message, Now).ToList();

        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, notification =>
        {
            Assert.Equal(11, notification.RecipientUserId);
            Assert.Equal("ProjectExpense", notification.EntityType);
            Assert.Equal(7, notification.ProjectId);
            Assert.Equal(message.MessageId, notification.SourceMessageId);
            Assert.Equal(Now, notification.CreatedAt);
            Assert.Null(notification.ReadAt);
        });
        Assert.Equal(["40", "41"], notifications.Select(notification => notification.EntityKey));
    }

    [Fact]
    public void The_same_record_twice_in_one_save_gives_one_notification()
    {
        var notification = Assert.Single(OutboxDispatcher.Handle(Message(22, Returned("40"), Returned("40")), Now));

        Assert.Equal("40", notification.EntityKey);
    }

    [Fact]
    public void An_unknown_message_type_gives_nothing_and_a_broken_payload_fails()
    {
        var unknown = Message(22, Returned("40"));
        unknown.Type = "SomethingElse";
        var broken = Message(22);
        broken.Payload = "{not json";

        Assert.Empty(OutboxDispatcher.Handle(unknown, Now));
        Assert.ThrowsAny<JsonException>(() => OutboxDispatcher.Handle(broken, Now).ToList());
    }
}
