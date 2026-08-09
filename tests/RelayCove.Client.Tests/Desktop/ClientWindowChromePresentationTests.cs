using System.ComponentModel;
using System.Windows;
using System.Windows.Shell;
using RelayCove.Client.Controls;

namespace RelayCove.Client.Tests.Desktop;

[Collection(WpfTestCollection.Name)]
public sealed class ClientWindowChromePresentationTests
{
    [Fact]
    public async Task TitleBar_WhenActionsAreInvoked_RaisesEachIntentExactlyOnce()
    {
        await RunOnStaAsync(() =>
        {
            var titleBar = new TitleBarControl();
            var minimizeCount = 0;
            var maximizeRestoreCount = 0;
            var closeCount = 0;
            var systemMenuCount = 0;

            titleBar.MinimizeRequested += (_, _) => minimizeCount++;
            titleBar.MaximizeRestoreRequested += (_, _) => maximizeRestoreCount++;
            titleBar.CloseRequested += (_, _) => closeCount++;
            titleBar.SystemMenuRequested += (_, _) => systemMenuCount++;

            titleBar.MinimizeButton.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            titleBar.MaximizeRestoreButton.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            titleBar.CloseButton.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            titleBar.RaiseEvent(
                new RoutedEventArgs(TitleBarControl.SystemMenuRequestedEvent, titleBar));

            Assert.Equal(1, minimizeCount);
            Assert.Equal(1, maximizeRestoreCount);
            Assert.Equal(1, closeCount);
            Assert.Equal(1, systemMenuCount);
        });
    }

    [Fact]
    public async Task TitleBar_WhenWindowStateChanges_ShowsMatchingWindowIcon()
    {
        await RunOnStaAsync(() =>
        {
            var titleBar = new TitleBarControl();

            titleBar.IsMaximized = true;

            Assert.Equal(Visibility.Collapsed, titleBar.MaximizeIcon.Visibility);
            Assert.Equal(Visibility.Visible, titleBar.RestoreIcon.Visibility);

            titleBar.IsMaximized = false;

            Assert.Equal(Visibility.Visible, titleBar.MaximizeIcon.Visibility);
            Assert.Equal(Visibility.Collapsed, titleBar.RestoreIcon.Visibility);
        });
    }

    [Fact]
    public async Task MainWindow_WhenConstructed_UsesFrozenRc25ChromeContract()
    {
        await RunOnStaAsync(() =>
        {
            var window = new MainWindow();
            try
            {
                var chrome = WindowChrome.GetWindowChrome(window);

                Assert.Equal(WindowStyle.None, window.WindowStyle);
                Assert.Equal(ResizeMode.CanResize, window.ResizeMode);
                Assert.NotNull(chrome);
                Assert.Equal(48, chrome.CaptionHeight);
                Assert.Equal(new Thickness(8), chrome.ResizeBorderThickness);
                Assert.Equal(new Thickness(0), chrome.GlassFrameThickness);
                Assert.False(chrome.UseAeroCaptionButtons);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task MainWindow_WhenTitleBarCloseIsRequested_UsesNormalClosingEvent()
    {
        await RunOnStaAsync(() =>
        {
            var window = new MainWindow
            {
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
            };
            var closingCount = 0;
            CancelEventHandler handler = (_, e) =>
            {
                closingCount++;
                e.Cancel = true;
            };
            window.Closing += handler;
            try
            {
                window.Show();
                window.ApplicationTitleBar.CloseButton.RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));

                Assert.Equal(1, closingCount);
                Assert.True(window.IsVisible);
            }
            finally
            {
                window.Closing -= handler;
                window.Close();
            }
        });
    }

    [Fact]
    public async Task MainWindow_WhenTitleBarCommandButtonsAreInvoked_RoutesIntentsToBoundWindow()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateOffscreenWindow();
            var observedEvents = new List<RoutedEvent>();
            var sources = new List<object?>();
            RoutedEventHandler observer = (_, e) =>
            {
                observedEvents.Add(e.RoutedEvent);
                sources.Add(e.Source);
            };

            window.AddHandler(TitleBarControl.MinimizeRequestedEvent, observer);
            window.AddHandler(TitleBarControl.MaximizeRestoreRequestedEvent, observer);
            try
            {
                window.Show();

                window.ApplicationTitleBar.MaximizeRestoreButton.RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));

                window.ApplicationTitleBar.MaximizeRestoreButton.RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));

                window.ApplicationTitleBar.MinimizeButton.RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));

                Assert.Equal(
                    [
                        TitleBarControl.MaximizeRestoreRequestedEvent,
                        TitleBarControl.MaximizeRestoreRequestedEvent,
                        TitleBarControl.MinimizeRequestedEvent,
                    ],
                    observedEvents);
                Assert.All(sources, source => Assert.Same(window.ApplicationTitleBar, source));
            }
            finally
            {
                window.RemoveHandler(TitleBarControl.MinimizeRequestedEvent, observer);
                window.RemoveHandler(TitleBarControl.MaximizeRestoreRequestedEvent, observer);
                window.Close();
            }
        });
    }

    private static MainWindow CreateOffscreenWindow() =>
        new()
        {
            ShowActivated = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
        };

    private static Task RunOnStaAsync(Action action)
    {
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
