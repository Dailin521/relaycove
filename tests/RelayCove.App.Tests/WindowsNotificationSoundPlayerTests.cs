using RelayCove.App.Platforms.Windows;

namespace RelayCove.App.Tests;

public sealed class WindowsNotificationSoundPlayerTests
{
    [Fact]
    public void TryPlay_WhenSystemSoundPolicyChanges_RechecksBeforeEverySound()
    {
        var allowed = true;
        var plays = 0;
        using var player = new WindowsNotificationSoundPlayer(() => allowed, () => plays++);

        Assert.True(player.TryPlay());
        allowed = false;
        Assert.False(player.TryPlay());
        allowed = true;
        Assert.True(player.TryPlay());
        Assert.Equal(2, plays);
    }

    [Fact]
    public void TryPlay_WhenPolicyCannotBeRead_RemainsSilent()
    {
        var plays = 0;
        using var player = new WindowsNotificationSoundPlayer(
            () => throw new InvalidOperationException("Policy unavailable."), () => plays++);

        Assert.False(player.TryPlay());
        Assert.Equal(0, plays);
    }

    [Fact]
    public void TryPlay_WhenAudioDeviceFails_DoesNotEscapeIntoMessageDelivery()
    {
        using var player = new WindowsNotificationSoundPlayer(
            () => true, () => throw new InvalidOperationException("Audio unavailable."));

        Assert.False(player.TryPlay());
    }

    [Fact]
    public void TryPlay_WhenDisposed_DoesNotStartAudio()
    {
        var policyReads = 0;
        var plays = 0;
        using var player = new WindowsNotificationSoundPlayer(
            () => { policyReads++; return true; },
            () => plays++);
        player.Dispose();

        Assert.False(player.TryPlay());
        Assert.Equal(0, policyReads);
        Assert.Equal(0, plays);
    }
}
