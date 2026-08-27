using System.Reflection;
using System.Runtime.InteropServices;
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

    [Theory]
    [InlineData(0, false, "RichChat")]
    [InlineData(1, false, "RichChat · 1 条未读消息")]
    [InlineData(120, false, "RichChat · 99+ 条未读消息")]
    [InlineData(0, true, "RichChat · 有未读消息")]
    public void TrayTooltip_WhenUnreadVaries_UsesAuthoritativeCount(
        int count,
        bool isTruncated,
        string expected)
    {
        Assert.Equal(expected, WindowsTrayIconController.FormatTooltip(count, isTruncated));
    }

    [Theory]
    [InlineData(360, 96, 360)]
    [InlineData(360, 144, 540)]
    [InlineData(112, 192, 224)]
    public void TrayPreviewScale_WhenDpiVaries_PreservesDipSize(int dip, uint dpi, int expected) =>
        Assert.Equal(expected, WindowsTrayIconController.ScaleDipToPixels(dip, dpi));

    [Fact]
    public void TrayCallback_WhenPlatformActionThrows_DoesNotEscapeNativeBoundary()
    {
        var invoked = false;

        var succeeded = WindowsTrayIconController.TryInvokeCallback(() =>
        {
            invoked = true;
            throw new InvalidOperationException("simulated tray activation failure");
        });

        Assert.True(invoked);
        Assert.False(succeeded);
    }

    [Theory]
    [InlineData(0x0406)]
    [InlineData(0x0200)]
    public void TrayHover_WhenShellUsesPopupOrMouseMove_RequestsPreview(uint notification) =>
        Assert.True(WindowsTrayIconController.IsPreviewOpenCallback(notification));

    [Fact]
    public void TrayIconRectangleInterop_WhenDeclared_UsesExactShellExportName()
    {
        var method = typeof(WindowsTrayIconController).GetMethod(
            "ShellNotifyIconGetRect",
            BindingFlags.NonPublic | BindingFlags.Static);
        var attribute = method?.GetCustomAttribute<DllImportAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal("Shell_NotifyIconGetRect", attribute.EntryPoint);
        Assert.True(attribute.ExactSpelling);
    }

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0, true, true)]
    [InlineData(1, false, true)]
    public void TrayPreview_WhenUnreadAuthorityVaries_ShowsOnlyForUnread(
        int count,
        bool isTruncated,
        bool expected) =>
        Assert.Equal(expected, WindowsTrayIconController.ShouldShowPreview(count, isTruncated));

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void TrayPreviewVisibility_WhenMouseMoveRepeats_DoesNotReopenVisibleCard(
        bool requestedVisible,
        bool currentlyVisible,
        bool expected) =>
        Assert.Equal(
            expected,
            WindowsTrayIconController.ShouldApplyPreviewVisibility(requestedVisible, currentlyVisible));

    [Theory]
    [InlineData(true, "dm:8")]
    [InlineData(false, null)]
    public void TrayActivation_WhenClicked_RoutesOnlyUnreadPreviewConversation(
        bool hasUnread,
        string? expected)
    {
        var notification = new AppMessageNotification("dm:8", "Bea", "hello");

        Assert.Equal(
            expected,
            WindowsTrayIconController.ResolveActivationConversation(notification, hasUnread));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void TrayContextMenu_WhenCommandVaries_ExitsOnlyForExplicitExitCommand(
        uint command,
        bool expected) =>
        Assert.Equal(expected, WindowsTrayIconController.IsExitMenuCommand(command));
}
