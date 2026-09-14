namespace RelayCove.Core.Tests;

public sealed class RealmEmojiReducerTests
{
    [Fact]
    public void Apply_WhenCustomEmojiSnapshotChanges_ReplacesCatalogAndIgnoresOldEvents()
    {
        var first = new RealmEmoji("1", "party", "/user_avatars/1/emoji/images/1.png", false);
        var second = new RealmEmoji("2", "wave", "/user_avatars/1/emoji/images/2.png", false);
        var initial = DomainReducer.Apply(ClientState.Empty, new RealmEmojiUpdatedEvent([first], 1));
        var updated = DomainReducer.Apply(initial, new RealmEmojiUpdatedEvent([second], 2));
        var stale = DomainReducer.Apply(updated, new RealmEmojiUpdatedEvent([first], 1));
        var heartbeat = DomainReducer.Apply(stale, new HeartbeatEvent(3));

        Assert.Equal(second, Assert.Single(heartbeat.RealmEmojis).Value);
        Assert.Equal(first, Assert.Single(initial.RealmEmojis).Value);
        Assert.Same(updated, stale);
        Assert.Same(updated.RealmEmojis, heartbeat.RealmEmojis);
        Assert.Empty(DomainReducer.Apply(heartbeat, new RealmEmojiUpdatedEvent([], 4)).RealmEmojis);
    }
}
