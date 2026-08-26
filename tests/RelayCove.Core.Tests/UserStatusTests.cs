using RelayCove.Core;

namespace RelayCove.Core.Tests;

public sealed class UserStatusTests
{
    private static readonly UserStatusContent MeetingStatus = new(
        "会议中",
        new EmojiReactionIdentity("calendar", "1f4c5", "unicode_emoji"));

    [Fact]
    public void Reducer_WhenUserStatusChanges_AddsAndClearsOnlyThatUser()
    {
        var initial = new ClientState(userStatuses: new UserStatusState(
            true,
            new Dictionary<long, UserStatusContent>
            {
                [8] = new UserStatusContent("专注中")
            }));

        var added = DomainReducer.Apply(initial, new UserStatusChangedEvent(7, MeetingStatus, 3));
        var cleared = DomainReducer.Apply(added, new UserStatusChangedEvent(7, null, 4));

        Assert.Equal(MeetingStatus, added.UserStatuses.Users[7]);
        Assert.Equal("专注中", added.UserStatuses.Users[8].StatusText);
        Assert.False(cleared.UserStatuses.Users.ContainsKey(7));
        Assert.Equal("专注中", cleared.UserStatuses.Users[8].StatusText);
        Assert.Equal(4, cleared.LastEventId);
    }

    [Fact]
    public void Reducer_WhenUserStatusSnapshotIsUnavailable_IgnoresValueButAdvancesCursor()
    {
        var state = DomainReducer.Apply(
            ClientState.Empty,
            new UserStatusChangedEvent(7, MeetingStatus, 3));

        Assert.False(state.UserStatuses.IsAvailable);
        Assert.Empty(state.UserStatuses.Users);
        Assert.Equal(3, state.LastEventId);
    }

    [Fact]
    public void UserStatusContent_WhenTextExceedsOfficialLimit_RejectsValue()
    {
        var tooLong = string.Concat(Enumerable.Repeat("😀", 61));

        Assert.Throws<ArgumentOutOfRangeException>(() => new UserStatusContent(tooLong));
    }
}
