using RelayCove.App.Platforms.Windows;

namespace RelayCove.App.Tests;

public sealed class WindowsNotificationSoundPolicyTests
{
    [Theory]
    [InlineData(0, 4, 0u, true)]
    [InlineData(0, 4, 1u, false)] // Priority only, including automatic rules.
    [InlineData(0, 4, 2u, false)] // Alarms only.
    [InlineData(0, 4, 3u, false)] // Unknown future profile.
    [InlineData(unchecked((int)0xC0000022), 4, 0u, false)] // Access denied.
    [InlineData(0, 0, 0u, false)]
    [InlineData(0, 8, 0u, false)]
    public void IsUnrestrictedProfile_WhenSystemStateVaries_OnlyAllowsKnownUnrestrictedState(
        int status, int size, uint profile, bool expected)
    {
        Assert.Equal(expected, WindowsNotificationSoundPolicy.IsUnrestrictedProfile(status, size, profile));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData(1, true)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData("1", false)]
    [InlineData("", false)]
    [InlineData(1L, false)]
    public void IsEnabledSetting_WhenWindowsSoundSettingVaries_HonorsMuteAndRejectsUnknownFormats(
        object? value, bool expected)
    {
        Assert.Equal(expected, WindowsNotificationSoundPolicy.IsEnabledSetting(value));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("*default*", true)]
    [InlineData("", false)]
    [InlineData("*none*", false)]
    [InlineData("unexpected", false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    public void IsApplicationSoundEnabled_WhenPerAppSoundSettingVaries_UsesWindowsStringSetting(
        object? value, bool expected)
    {
        Assert.Equal(expected, WindowsNotificationSoundPolicy.IsApplicationSoundEnabled(value));
    }
}
