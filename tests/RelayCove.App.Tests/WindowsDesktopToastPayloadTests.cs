using System.Xml.Linq;
using RelayCove.App.Platforms.Windows;
using RelayCove.App.Services;
using Windows.UI.Notifications;

namespace RelayCove.App.Tests;

public sealed class WindowsDesktopToastPayloadTests
{
    [Fact]
    public void Build_WhenTextAndConversationContainReservedCharacters_RoundTripsThroughSystemToastXml()
    {
        const string conversation = "channel:27:设计 & review=1";
        var notification = new AppMessageNotification(conversation, "Alice & Bob", "<message> \"test\"");
        var xml = XDocument.Parse(WindowsDesktopToastPayload.Build(notification, null, "session-token"));

        Assert.Equal("toast", xml.Root!.Name.LocalName);
        Assert.Equal("ToastGeneric", (string?)xml.Descendants("binding").Single().Attribute("template"));
        Assert.Equal(new[] { "Alice & Bob", "<message> \"test\"" }, xml.Descendants("text").Select(text => text.Value));
        Assert.Equal(conversation, WindowsDesktopToastPayload.GetConversation((string)xml.Root.Attribute("launch")!, "session-token"));
        Assert.Empty(xml.Descendants("message"));
    }

    [Fact]
    public void Build_WhenPreviewIsDisabled_KeepsGenericBodyAndAvoidsDuplicateSystemSound()
    {
        var xml = XDocument.Parse(WindowsDesktopToastPayload.Build(
            new AppMessageNotification("dm:8", "Sender", "收到一条新消息"), null, "token"));

        Assert.Equal("收到一条新消息", xml.Descendants("text").Last().Value);
        Assert.Equal("true", (string?)xml.Descendants("audio").Single().Attribute("silent"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("https://example.test/avatar.png", false)]
    [InlineData("file:///C:/cache/avatar.png", true)]
    public void Build_WhenAvatarSourceVaries_OnlyIncludesLocalAvatar(string? source, bool expected)
    {
        var xml = XDocument.Parse(WindowsDesktopToastPayload.Build(
            new AppMessageNotification("dm:8", "Sender", "Text"), source is null ? null : new Uri(source), "token"));
        var images = xml.Descendants("image").ToArray();
        Assert.Equal(expected ? 1 : 0, images.Length);
        if (expected)
        {
            Assert.Equal(source, (string?)images[0].Attribute("src"));
            Assert.Equal("circle", (string?)images[0].Attribute("hint-crop"));
        }
    }

    [Theory]
    [InlineData("conversation=dm%3A8&session=old")]
    [InlineData("conversation=dm%3A8")]
    [InlineData("session=current")]
    [InlineData("session=current&conversation=")]
    [InlineData("")]
    public void GetConversation_WhenSessionIsStaleOrPayloadIsIncomplete_RejectsActivation(string arguments)
    {
        Assert.Null(WindowsDesktopToastPayload.GetConversation(arguments, "current"));
    }

    [Theory]
    [InlineData(NotificationSetting.Enabled, "系统通知已接入")]
    [InlineData(NotificationSetting.DisabledForApplication, "Windows 已关闭 RichChat")]
    [InlineData(NotificationSetting.DisabledForUser, "Windows 已关闭当前用户")]
    [InlineData(NotificationSetting.DisabledByGroupPolicy, "组策略关闭")]
    [InlineData(NotificationSetting.DisabledByManifest, "清单未启用")]
    public void DescribeSetting_WhenWindowsRestrictsNotifications_ReportsActualSystemSetting(NotificationSetting setting, string expected)
    {
        Assert.Contains(expected, WindowsAppNotificationService.DescribeDesktopSetting(setting));
    }

    [Fact]
    public void Startup_WhenDesktopToastNeedsIdentity_SetsItBeforeCreatingWindows()
    {
        var source = ReadSource("App.xaml.cs");
        var prepare = source.IndexOf("WindowsAppNotificationService.PrepareProcessNotificationIdentity();", StringComparison.Ordinal);
        var initialize = source.IndexOf("this.InitializeComponent();", StringComparison.Ordinal);
        Assert.True(prepare >= 0 && prepare < initialize);
    }

    [Fact]
    public void NotificationRouting_WhenElevated_UsesSystemToastWithComActivationAndNoTrayFallback()
    {
        var source = ReadSource("WindowsAppNotificationService.cs");
        Assert.Contains("if (WindowsProcessEnvironment.IsElevated())", source);
        Assert.Contains("RegisterDesktopSystemNotifications();", source);
        Assert.Contains("ToastNotificationManagerCompat.OnActivated += OnDesktopToastActivated", source);
        Assert.Contains("ToastNotificationManagerCompat.OnActivated -= OnDesktopToastActivated", source);
        Assert.Contains("new global::Windows.UI.Notifications.ToastNotification(xml)", source);
        Assert.Contains("_desktopNotifier.Show(toast)", source);
        Assert.DoesNotContain("ShowCompatibilityNotification", source);
        Assert.DoesNotContain("ShowCompatibilityNotification", ReadSource("WindowsTrayIconController.cs"));
    }

    private static string ReadSource(string file)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "src", "RelayCove.App", "Platforms", "Windows", file);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("Unable to locate notification source.");
    }
}
