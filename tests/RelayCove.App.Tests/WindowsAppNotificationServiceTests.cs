using RelayCove.App.Platforms.Windows;
using RelayCove.App.Services;

namespace RelayCove.App.Tests;

public sealed class WindowsAppNotificationServiceTests
{
    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(1, false, 1)]
    [InlineData(120, false, 1)]
    [InlineData(0, true, 2)]
    [InlineData(3, true, 1)]
    public void ResolveBadgeMode_WhenUnreadAuthorityVaries_ShowsKnownCountWhenAvailable(
        int count,
        bool isTruncated,
        int expected)
    {
        Assert.Equal((TaskbarBadgeMode)expected, WindowsAppNotificationService.ResolveBadgeMode(count, isTruncated));
    }

    [Theory]
    [InlineData(2, false, "2 条未读消息")]
    [InlineData(120, false, "99+ 条未读消息")]
    [InlineData(0, true, "有未读消息")]
    public void TaskbarUnreadIconRenderer_WhenUnreadExists_RendersAccessibleOverlay(
        int count,
        bool isTruncated,
        string expectedDescription)
    {
        var rendered = TaskbarUnreadIconRenderer.Render(count, isTruncated);

        Assert.Equal(expectedDescription, rendered.Description);
        Assert.Equal(
            TaskbarUnreadIconRenderer.AndMaskStride * TaskbarUnreadIconRenderer.IconSize,
            rendered.AndMask.Length);
        Assert.Contains(rendered.AndMask, value => value != byte.MaxValue);
        Assert.Contains(rendered.XorBits, value => value != 0);
    }

    [Theory]
    [InlineData("file:///C:/RelayCove/avatar.png", true)]
    [InlineData("https://zulip.example/avatar/8", false)]
    public void CanUseAvatarUri_WhenUriVaries_AcceptsOnlyControlledLocalFiles(string value, bool expected)
    {
        Assert.Equal(expected, WindowsAppNotificationService.CanUseAvatarUri(new Uri(value)));
    }
}
