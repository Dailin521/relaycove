using System.Reflection;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using RelayCove.App.Platforms.Windows;
using RelayCove.App.Services;
using RelayCove.Core;

namespace RelayCove.App.Tests;

public sealed class WindowsAppNotificationServiceTests
{
    [Fact]
    public void DesktopActivation_WhenAccountChanges_InvalidatesOldNotificationToken()
    {
        var first = AccountId.Create(RealmEndpoint.Parse("https://notification.example.test"), 7);
        var second = AccountId.Create(RealmEndpoint.Parse("https://notification.example.test"), 8);
        var session = new AvatarCacheTests.AvatarSession { Account = first };
        using var service = new WindowsAppNotificationService(
            new TestAvatarFileStore(), new ImmediateDispatcher(), new WindowsWindowShellAdapter(), session);
        var tokenField = typeof(WindowsAppNotificationService).GetField("_desktopActivationToken", BindingFlags.Instance | BindingFlags.NonPublic)!;
        session.Publish();
        var oldToken = (string)tokenField.GetValue(service)!;
        session.Account = second;
        session.Publish();

        Assert.NotEqual(oldToken, (string)tokenField.GetValue(service)!);
        Assert.Null(WindowsDesktopToastPayload.GetConversation($"conversation=dm%3A9&session={oldToken}",
            (string)tokenField.GetValue(service)!));
    }

    [Fact]
    public void AvatarChanged_WhenAccountChanges_DiscardsOldPreviewAndRejectsOtherAccountEvents()
    {
        var first = AccountId.Create(RealmEndpoint.Parse("https://avatar.example.test"), 7);
        var second = AccountId.Create(RealmEndpoint.Parse("https://avatar.example.test"), 8);
        var session = new AvatarCacheTests.AvatarSession { Account = first };
        var avatars = new TestAvatarFileStore();
        using var service = new WindowsAppNotificationService(avatars, new ImmediateDispatcher(), new WindowsWindowShellAdapter(), session);
        const string url = "/user_avatars/shared.png";
        service.UpdateTrayPreview(new AppMessageNotification("dm:8", "First", "first body", url));
        Assert.Equal(1, avatars.Reads);
        session.Account = second;
        session.Publish();
        var previewField = typeof(WindowsAppNotificationService).GetField("_trayPreviewNotification", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.Null(previewField.GetValue(service));
        avatars.Publish(second, url);
        Assert.Equal(1, avatars.Reads);
        service.UpdateTrayPreview(new AppMessageNotification("dm:7", "Second", "second body", url));
        Assert.Equal(2, avatars.Reads);
        avatars.Publish(first, url);
        Assert.Equal(2, avatars.Reads);
        avatars.Publish(second, url);
        Assert.Equal(3, avatars.Reads);
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public void Dispatch(Action action) => action();
        public Task YieldToRenderAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestAvatarFileStore : INotificationAvatarFileStore
    {
        public event EventHandler<AvatarChangedEventArgs>? AvatarChanged;
        public int Reads { get; private set; }
        public void Publish(AccountId accountId, string url) => AvatarChanged?.Invoke(this, new(accountId, url));
        public Task<Uri?> GetAvatarUriAsync(string sourceUrl, CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult<Uri?>(null);
        }
        public Task ClearAccountAsync(AccountId accountId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(0, true)]
    public void UpdateUnreadBadge_WhenPreferenceChangesWithoutVisibleWindow_UpdatesTrayWithoutClearingUnread(
        int count, bool isTruncated)
    {
        const BindingFlags privateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        var controller = new WindowsTrayIconController(_ => { }, () => { });
        var controllerType = typeof(WindowsTrayIconController);
        controllerType.GetField("_iconHandle", privateInstance)!.SetValue(controller, (nint)41);
        controllerType.GetField("_unreadIconHandle", privateInstance)!.SetValue(controller, (nint)42);
        using var service = new WindowsAppNotificationService(
            new UnusedAvatarFileStore(), new MauiUiDispatcher(), new WindowsWindowShellAdapter());
        typeof(WindowsAppNotificationService).GetField("_trayIconController", privateInstance)!
            .SetValue(service, controller);
        try
        {
            service.UpdateTrayUnread(count, isTruncated);
            service.UpdateUnreadBadge(count, isTruncated);
            Assert.Equal((nint)42, ReadIcon());

            service.UpdateUnreadBadge(0, false); // The existing badge preference is off.
            Assert.Equal((nint)41, ReadIcon());
            Assert.Equal(count, controllerType.GetField("_unreadCount", privateInstance)!.GetValue(controller));
            Assert.Equal(isTruncated, controllerType.GetField("_unreadIsTruncated", privateInstance)!.GetValue(controller));

            service.UpdateUnreadBadge(count, isTruncated);
            service.StopTrayFlash(); // Acknowledging the flash must retain the unread dot.
            Assert.Equal((nint)42, ReadIcon());

            service.UpdateTrayUnread(0, false);
            Assert.Equal((nint)41, ReadIcon());
        }
        finally
        {
            // No real HICON/HWND or shell registration is used by this test.
            controllerType.GetField("_iconHandle", privateInstance)!.SetValue(controller, nint.Zero);
            controllerType.GetField("_unreadIconHandle", privateInstance)!.SetValue(controller, nint.Zero);
        }

        nint ReadIcon()
        {
            var data = controllerType.GetMethod("CreateIconData", privateInstance)!.Invoke(controller, [6u])!;
            return (nint)data.GetType().GetField("IconHandle")!.GetValue(data)!;
        }
    }

    [Fact]
    public void BuildNotification_WhenCustomSoundIsPlayed_DoesNotAlsoPlaySystemSound()
    {
        // Activate only the local SDK payload builder. The test host has no app
        // identity; do not register a sender, show a notification or play audio.
        var previousHandler = WinRT.ActivationFactory.ActivationHandler;
        WinRT.ActivationFactory.ActivationHandler = (name, iid) =>
            name.StartsWith("Microsoft.Windows.AppNotifications.", StringComparison.Ordinal)
                ? ActivateNotificationFactory(name, iid)
                : previousHandler?.Invoke(name, iid) ?? 0;
        try
        {
            var notification = WindowsAppNotificationService.BuildNotification(
                new AppMessageNotification("dm:8", "Test sender", "Test message"), null);
            var payload = XDocument.Parse(notification.Payload);

            var audio = Assert.Single(payload.Root!.Elements("audio"));
            Assert.Equal("true", (string?)audio.Attribute("silent"));
            Assert.Null(audio.Attribute("src"));
            Assert.Equal(new[] { "Test sender", "Test message" },
                payload.Descendants("text").Select(element => element.Value));
        }
        finally
        {
            WinRT.ActivationFactory.ActivationHandler = previousHandler;
        }
    }

    [Fact]
    public void FlashTaskbar_WhenMainWindowHandleIsAbsent_StillStartsTrayTimer()
    {
        const BindingFlags privateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        var starts = 0;
        var controller = new WindowsTrayIconController(_ => { }, () => { });
        var controllerType = typeof(WindowsTrayIconController);
        var timer = new WindowsTrayBlinkTimer(
            () => { },
            (_, id, _) => { starts++; return id; },
            (_, _) => { });
        controllerType.GetField("_blinkTimer", privateInstance)!.SetValue(controller, timer);
        controllerType.GetField("_messageWindowHandle", privateInstance)!.SetValue(controller, (nint)42);
        using var service = new WindowsAppNotificationService(
            new UnusedAvatarFileStore(), new MauiUiDispatcher(), new WindowsWindowShellAdapter());
        typeof(WindowsAppNotificationService).GetField("_trayIconController", privateInstance)!
            .SetValue(service, controller);
        try
        {
            service.UpdateTrayUnread(1, false);

            service.FlashTaskbar();

            Assert.True(timer.IsRunning);
            Assert.Equal(1, starts);
        }
        finally
        {
            // The test supplies no real HWND, tray icon, dispatcher or system notification.
            controllerType.GetField("_messageWindowHandle", privateInstance)!.SetValue(controller, nint.Zero);
        }
    }

    [Fact]
    public void UpdateUnread_WhenTrayIsAlreadyBlinking_PreservesPhaseAndTimerDeadline()
    {
        const BindingFlags privateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        var starts = 0;
        var stops = 0;
        using var controller = new WindowsTrayIconController(_ => { }, () => { });
        var controllerType = typeof(WindowsTrayIconController);
        var timer = new WindowsTrayBlinkTimer(
            () => { },
            (_, id, _) => { starts++; return id; },
            (_, _) => stops++);
        controllerType.GetField("_blinkTimer", privateInstance)!.SetValue(controller, timer);
        controllerType.GetField("_messageWindowHandle", privateInstance)!.SetValue(controller, (nint)42);
        try
        {
            controller.UpdateUnread(1, false);
            controller.StartFlashing();
            controllerType.GetField("_iconShowingArtwork", privateInstance)!.SetValue(controller, false);

            controller.UpdateUnread(1, false);
            controller.UpdateUnread(2, false);
            controller.StartFlashing();

            Assert.False((bool)controllerType.GetField("_iconShowingArtwork", privateInstance)!.GetValue(controller)!);
            Assert.Equal(1, starts);
            Assert.Equal(0, stops);

            controller.UpdateUnread(0, false);
            Assert.False(timer.IsRunning);
            Assert.Equal(1, stops);
        }
        finally
        {
            controllerType.GetField("_messageWindowHandle", privateInstance)!.SetValue(controller, nint.Zero);
        }
    }

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
    [InlineData(0u)] // Delete, set version and set focus.
    [InlineData(7u)] // Add: message, icon and tooltip.
    [InlineData(6u)] // Modify: icon and tooltip.
    public void TrayIdentity_WhenPortableExecutableMoves_UsesWindowAndIdWithoutPathBoundGuid(uint flags)
    {
        var controller = new WindowsTrayIconController(_ => { }, () => { });
        var controllerType = typeof(WindowsTrayIconController);
        const BindingFlags privateInstance = BindingFlags.NonPublic | BindingFlags.Instance;
        var windowHandle = new nint(123);
        controllerType.GetField("_messageWindowHandle", privateInstance)!.SetValue(controller, windowHandle);

        // Inspect the actual payloads without registering an icon or opening a window.
        var data = controllerType.GetMethod("CreateIconData", privateInstance)!.Invoke(controller, [flags])!;
        var identifier = controllerType.GetMethod("CreateIconIdentifier", privateInstance)!.Invoke(controller, null)!;

        Assert.Equal(flags, (uint)data.GetType().GetField("Flags")!.GetValue(data)!);
        Assert.Equal(0u, (uint)data.GetType().GetField("Flags")!.GetValue(data)! & 0x20u);
        foreach (var payload in new[] { data, identifier })
        {
            var payloadType = payload.GetType();
            Assert.Equal(windowHandle, (nint)payloadType.GetField("WindowHandle")!.GetValue(payload)!);
            Assert.Equal(1u, (uint)payloadType.GetField("Id")!.GetValue(payload)!);
            Assert.Equal(Guid.Empty, (Guid)payloadType.GetField("Guid")!.GetValue(payload)!);
        }
    }

    [Theory]
    [InlineData(3, false, 2, false, true)]
    [InlineData(1, true, 0, true, true)]
    [InlineData(0, true, 0, false, true)]
    [InlineData(2, false, 2, false, false)]
    [InlineData(2, false, 3, false, false)]
    public void TrayFlash_WhenUnreadAuthorityChanges_StopsOnlyAfterAcknowledgement(
        int previousCount,
        bool previousIsTruncated,
        int currentCount,
        bool currentIsTruncated,
        bool expected) =>
        Assert.Equal(
            expected,
            WindowsTrayIconController.ShouldStopFlashingAfterUnreadUpdate(
                previousCount,
                previousIsTruncated,
                currentCount,
                currentIsTruncated));

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

    private static nint ActivateNotificationFactory(string name, Guid iid)
    {
        var nameHandle = WinRT.MarshalString.FromManaged(name);
        nint factory = 0;
        try
        {
            Marshal.ThrowExceptionForHR(DllGetActivationFactory(nameHandle, out factory));
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(factory, in iid, out var result));
            return result;
        }
        finally
        {
            if (factory != 0) Marshal.Release(factory);
            WinRT.MarshalString.DisposeAbi(nameHandle);
        }
    }

    [DllImport("Microsoft.WindowsAppRuntime.dll", ExactSpelling = true)]
    private static extern int DllGetActivationFactory(nint name, out nint factory);

    private sealed class UnusedAvatarFileStore : INotificationAvatarFileStore
    {
        public event EventHandler<AvatarChangedEventArgs>? AvatarChanged { add { } remove { } }

        public Task<Uri?> GetAvatarUriAsync(string sourceUrl, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The tray timer test must not fetch avatars.");

        public Task ClearAccountAsync(AccountId accountId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The tray timer test must not change account data.");
    }
}
