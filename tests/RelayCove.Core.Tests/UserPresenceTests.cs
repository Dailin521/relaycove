using RelayCove.Core;

namespace RelayCove.Core.Tests;

public sealed class UserPresenceTests
{
    [Fact]
    public void ResolveStatus_WhenModernTimestampsChange_MapsActiveIdleAndOffline()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_000);

        Assert.Equal(
            UserPresenceStatus.Active,
            new UserPresence(7, now.AddSeconds(-10), now.AddSeconds(-5)).ResolveStatus(now));
        Assert.Equal(
            UserPresenceStatus.Idle,
            new UserPresence(7, now.AddSeconds(-250), now.AddSeconds(-10)).ResolveStatus(now));
        Assert.Equal(
            UserPresenceStatus.Offline,
            new UserPresence(7, now.AddSeconds(-250), now.AddSeconds(-201)).ResolveStatus(now));
    }

    [Fact]
    public void PresenceState_WhenSnapshotAvailableAndUserMissing_ReportsOffline()
    {
        var state = new PresenceState(true);

        Assert.Equal(UserPresenceStatus.Offline, state.ResolveStatus(42, DateTimeOffset.UtcNow));
        Assert.Null(PresenceState.Unavailable.ResolveStatus(42, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Reducer_WhenPresenceEventArrives_UpdatesOnlyThatUserAndAdvancesCursor()
    {
        var first = new UserPresence(7, DateTimeOffset.UnixEpoch.AddSeconds(100), null);
        var second = new UserPresence(8, null, DateTimeOffset.UnixEpoch.AddSeconds(110));
        var state = DomainReducer.Apply(
            new ClientState(presence: new PresenceState(true)),
            new UserPresenceChangedEvent(first, 3));
        state = DomainReducer.Apply(state, new UserPresenceChangedEvent(second, 4));

        Assert.True(state.Presence.IsAvailable);
        Assert.Equal(first, state.Presence.Users[7]);
        Assert.Equal(second, state.Presence.Users[8]);
        Assert.Equal(4, state.LastEventId);
    }

    [Fact]
    public void Reducer_WhenPresenceIsUnavailable_IgnoresPresenceValueButAdvancesCursor()
    {
        var presence = new UserPresence(7, DateTimeOffset.UnixEpoch.AddSeconds(100), null);

        var state = DomainReducer.Apply(ClientState.Empty, new UserPresenceChangedEvent(presence, 3));

        Assert.False(state.Presence.IsAvailable);
        Assert.Empty(state.Presence.Users);
        Assert.Equal(3, state.LastEventId);
    }
}
