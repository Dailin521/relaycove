namespace RelayCove.Core.Tests;

public sealed class UserAvatarTests
{
    [Theory]
    [InlineData(UserAvatarSource.Generated, null)]
    [InlineData(UserAvatarSource.Uploaded, "/user_avatars/1/hash.png")]
    [InlineData(UserAvatarSource.Unknown, "/user_avatars/1/hash.png")]
    public void DisplayAvatarUrl_WhenSourcesSharePath_UsesAuthoritativeSource(UserAvatarSource source, string? expected)
    {
        var user = new UserProfile(1, "Ada", avatarUrl: "/user_avatars/1/hash.png", avatarSource: source);
        Assert.Equal(expected, user.DisplayAvatarUrl);
    }

    [Fact]
    public void Apply_WhenAvatarChangesAndThenNameChanges_PreservesLatestAvatar()
    {
        var state = DomainReducer.Apply(ClientState.Empty, new UserUpsertEvent(
            new UserProfile(1, "Ada", avatarUrl: "/user_avatars/1/old.png", avatarVersion: 1, avatarSource: UserAvatarSource.Uploaded)));
        state = DomainReducer.Apply(state, new UserPatchedEvent(1, null, null, null,
            HasAvatar: true, AvatarUrl: "/user_avatars/1/new.png", AvatarVersion: 2, AvatarSource: UserAvatarSource.Generated));
        state = DomainReducer.Apply(state, new UserPatchedEvent(1, "New name", null, null));
        Assert.Null(state.Users[1].DisplayAvatarUrl);
        Assert.Equal(2, state.Users[1].AvatarVersion);
        state = DomainReducer.Apply(state, new UserPatchedEvent(1, null, null, null,
            HasAvatar: true, AvatarUrl: "/user_avatars/1/upload.png", AvatarVersion: 3, AvatarSource: UserAvatarSource.Uploaded));
        Assert.Equal("/user_avatars/1/upload.png", state.Users[1].DisplayAvatarUrl);
        state = DomainReducer.Apply(state, new UserPatchedEvent(1, null, null, null,
            HasAvatar: true, AvatarUrl: null, AvatarVersion: 4, AvatarSource: UserAvatarSource.Gravatar));
        Assert.Null(state.Users[1].DisplayAvatarUrl);
    }

    [Theory]
    [InlineData(1, UserAvatarSource.Generated)]
    [InlineData(2, UserAvatarSource.Unknown)]
    public void PreserveAvatarSource_WhenSnapshotOmitsSource_PreservesOnlySameVersion(int version, UserAvatarSource expected)
    {
        var previous = new UserProfile(1, "Ada", avatarUrl: "/user_avatars/1/hash.png", avatarVersion: 1, avatarSource: UserAvatarSource.Generated);
        var current = new UserProfile(1, "Ada", avatarUrl: previous.AvatarUrl, avatarVersion: version);
        Assert.Equal(expected, current.PreserveAvatarSource(previous).AvatarSource);
        Assert.Equal(UserAvatarSource.Uploaded,
            (current with { AvatarSource = UserAvatarSource.Uploaded }).PreserveAvatarSource(previous).AvatarSource);
    }
}
