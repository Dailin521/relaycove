using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RelayCove.Client;
using RelayCove.Client.Updates;
using RelayCove.Shared.Updates;

namespace RelayCove.Client.Tests.Desktop;

[Collection(WpfTestCollection.Name)]
public sealed class ClientUpdateHandoffTests
{
    [Fact]
    public async Task OptionalUpdate_WhenAvailable_ExposesDownloadActionThroughSettingsDrawer()
    {
        await RunOnStaAsync(() =>
        {
            var downloadCount = 0;
            var window = CreateVisibleWindow();
            try
            {
                window.BindUpdateActions(
                    _ => Task.FromResult(true),
                    () => Task.CompletedTask,
                    () =>
                    {
                        downloadCount++;
                        return Task.CompletedTask;
                    },
                    static () => { },
                    () => Task.CompletedTask,
                    static () => { });
                window.ApplyUpdateState(CreateOptionalState());
                window.SettingsOverlay.Visibility = Visibility.Visible;
                window.UpdateLayout();

                Assert.True(window.SettingsOverlay.HasOptionalUpdateAction);
                Assert.Equal("下载更新", window.SettingsOverlay.UpdateActionLabel);
                Assert.True(window.SettingsOverlay.OptionalUpdateActionButton.IsEnabled);
                Assert.True(window.SettingsOverlay.OptionalUpdateActionButton.IsVisible);

                window.SettingsOverlay.OptionalUpdateActionButton.RaiseEvent(
                    new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));

                Assert.Equal(1, downloadCount);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task OptionalUpdate_WhenHandoffFails_ExposesFailureReasonAndRestoresApplyAction()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateVisibleWindow();
            try
            {
                window.BindUpdateActions(
                    _ => Task.FromResult(true),
                    () => Task.CompletedTask,
                    () => Task.CompletedTask,
                    static () => { },
                    () => Task.CompletedTask,
                    static () => { });
                window.ApplyUpdateState(CreateOptionalState() with
                {
                    Phase = ClientUpdatePhase.Downloaded,
                    ArchivePath = "C:\\temporary\\relaycove.zip",
                });
                window.ShowUpdateHandoffConfirming();
                window.ShowUpdateHandoffFailure("更新程序未能启动。");

                Assert.Contains("更新交接失败", window.UpdateStatusText.Text, StringComparison.Ordinal);
                Assert.Contains("更新程序未能启动", window.SettingsOverlay.UpdateStatus, StringComparison.Ordinal);
                Assert.True(window.SettingsOverlay.IsUpdateActionEnabled);
                Assert.Equal("关闭并更新", window.SettingsOverlay.UpdateActionLabel);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task MandatoryGate_WhenComposerHadFocus_DisablesBusinessPanelsAndCapturesEnter()
    {
        await RunOnStaAsync(() =>
        {
            var businessEntryCount = 0;
            var exitCount = 0;
            var window = CreateVisibleWindow();
            try
            {
                window.BindUpdateActions(
                    _ =>
                    {
                        businessEntryCount++;
                        return Task.FromResult(true);
                    },
                    () => Task.CompletedTask,
                    () => Task.CompletedTask,
                    static () => { },
                    () => Task.CompletedTask,
                    () => exitCount++);
                window.MessageComposerTextBox.IsEnabled = true;
                Assert.True(window.MessageComposerTextBox.Focus());
                Assert.Same(window.MessageComposerTextBox, Keyboard.FocusedElement);

                window.ApplyUpdateState(CreateMandatoryState());
                window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Input);

                Assert.False(window.LoginPanel.IsEnabled);
                Assert.False(window.AccountPanel.IsEnabled);
                Assert.Equal(Visibility.Visible, window.MandatoryUpdateOverlay.Visibility);
                Assert.True(window.MandatoryUpdateOverlay.IsKeyboardFocusWithin);

                window.UpdateStatusText.Focusable = true;
                Assert.Same(
                    window.UpdateStatusText,
                    Keyboard.Focus(window.UpdateStatusText));
                Assert.False(window.MandatoryUpdateOverlay.IsKeyboardFocusWithin);
                var enter = new KeyEventArgs(
                    Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(window) ??
                    throw new InvalidOperationException("Expected visible window presentation source."),
                    Environment.TickCount,
                    Key.Enter)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                };
                window.RaiseEvent(enter);

                Assert.True(enter.Handled);
                Assert.True(window.MandatoryUpdateOverlay.IsKeyboardFocusWithin);
                Assert.Equal(0, businessEntryCount);
                Assert.Equal(0, exitCount);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task MandatoryGate_WhenReleaseNotesAreAtMaximumLength_KeepsActionsReachableAtMinimumWindowSize()
    {
        await RunOnStaAsync(() =>
        {
            var window = CreateVisibleWindow();
            try
            {
                window.Width = 900;
                window.Height = 520;
                window.ApplyUpdateState(CreateMandatoryState(new string('x', 8192)));
                window.UpdateLayout();
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);

                AssertWithinBounds(root, window.RetryMandatoryUpdateButton);
                AssertWithinBounds(root, window.DownloadMandatoryUpdateButton);
                AssertWithinBounds(root, window.ExitMandatoryUpdateButton);
                Assert.True(window.MandatoryUpdateDetailText.ActualHeight > 0);
                Assert.True(window.MandatoryUpdateNotesScrollViewer.ActualHeight < 300);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void CompareAndDeleteBootstrapRecord_WhenCleanupTokenIsStale_PreservesNewRecord()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "RelayCove.UpdateHandoff.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var recordPath = Path.Combine(root, "owned-bootstrap-token.v1");
        const string staleToken = "0123456789abcdef0123456789abcdef";
        const string currentToken = "fedcba9876543210fedcba9876543210";
        try
        {
            File.WriteAllText(recordPath, currentToken, new UTF8Encoding(false));

            Assert.False(App.CompareAndDeleteBootstrapRecord(recordPath, staleToken));
            Assert.Equal(currentToken, File.ReadAllText(recordPath, Encoding.UTF8));
            Assert.True(App.CompareAndDeleteBootstrapRecord(recordPath, currentToken));
            Assert.False(File.Exists(recordPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryDeleteOwnedBootstrap_WhenExactMarkerMatches_DeletesOnlyExactDirectory()
    {
        var root = CreateTemporaryRoot();
        var appDirectory = Path.Combine(root, "RelayCove");
        Directory.CreateDirectory(appDirectory);
        const string ownedToken = "0123456789abcdef0123456789abcdef";
        const string unrelatedToken = "fedcba9876543210fedcba9876543210";
        var ownedDirectory = CreateOwnedBootstrap(root, ownedToken);
        var unrelatedDirectory = CreateOwnedBootstrap(root, unrelatedToken);
        var unrelatedFile = Path.Combine(root, ".relaycove-updater-not-owned.txt");
        File.WriteAllText(unrelatedFile, "keep", new UTF8Encoding(false));
        try
        {
            Assert.True(App.TryDeleteOwnedBootstrap(ownedToken, appDirectory, root));

            Assert.False(Directory.Exists(ownedDirectory));
            Assert.True(Directory.Exists(unrelatedDirectory));
            Assert.True(File.Exists(unrelatedFile));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void TryDeleteOwnedBootstrap_WhenUpdaterIsLocked_PreservesMarkerAndRetries()
    {
        Assert.True(OperatingSystem.IsWindows());
        var root = CreateTemporaryRoot();
        var appDirectory = Path.Combine(root, "RelayCove");
        Directory.CreateDirectory(appDirectory);
        const string token = "0123456789abcdef0123456789abcdef";
        var ownedDirectory = CreateOwnedBootstrap(root, token);
        var updaterPath = Path.Combine(ownedDirectory, "RelayCove.Updater.exe");
        var markerPath = Path.Combine(ownedDirectory, ".relaycove-bootstrap-owner");
        try
        {
            using (new FileStream(
                updaterPath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None))
            {
                Assert.False(App.TryDeleteOwnedBootstrap(token, appDirectory, root));
                Assert.True(File.Exists(updaterPath));
                Assert.True(File.Exists(markerPath));
            }

            Assert.True(App.TryDeleteOwnedBootstrap(token, appDirectory, root));
            Assert.False(Directory.Exists(ownedDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AddUpdaterArguments_WhenArchiveIsVerified_UsesExactBootstrapContract()
    {
        var startInfo = new ProcessStartInfo();
        var manifest = new UpdateManifestDto(
            SchemaVersion: UpdateConstants.SchemaVersion,
            Channel: UpdateConstants.Channel,
            Version: "1.0.1-rc.1",
            MinimumSupportedVersion: "1.0.0",
            Mandatory: false,
            Artifact: new UpdateArtifactDto(
                Type: UpdateConstants.ArtifactTypePortableZip,
                Url: "https://updates.example.test/RelayCove-1.0.1-rc.1.zip",
                SizeBytes: 123,
                Sha256: new string('a', 64)),
            ReleaseNotes: "Fixes.");
        var archive = Path.Combine(Path.GetTempPath(), "RelayCove-1.0.1-rc.1.zip");
        var target = Path.Combine(Path.GetTempPath(), "RelayCove-App");

        App.AddUpdaterArguments(
            startInfo,
            manifest,
            archive,
            "1.0.0",
            target,
            currentProcessId: 42,
            currentProcessStartTimeUtcTicks: 638000000000000000,
            bootstrapToken: "0123456789abcdef0123456789abcdef");

        Assert.Equal(
        [
            "apply",
            "--archive", Path.GetFullPath(archive),
            "--expected-sha256", new string('a', 64),
            "--expected-size", "123",
            "--expected-version", "1.0.1-rc.1",
            "--current-version", "1.0.0",
            "--target", Path.GetFullPath(target),
            "--wait-pid", "42",
            "--wait-start-time-utc-ticks", "638000000000000000",
            "--bootstrap-token", "0123456789abcdef0123456789abcdef",
        ],
        startInfo.ArgumentList);
    }

    private static MainWindow CreateVisibleWindow()
    {
        var window = new MainWindow
        {
            Width = 1200,
            Height = 800,
            ShowInTaskbar = false,
            Opacity = 0,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -32000,
            Top = -32000,
        };
        window.LoginPanel.Visibility = Visibility.Collapsed;
        window.AccountPanel.Visibility = Visibility.Visible;
        window.Show();
        window.Dispatcher.Invoke(static () => { }, DispatcherPriority.Loaded);
        return window;
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "RelayCove.UpdateHandoff.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string CreateOwnedBootstrap(string packageParent, string token)
    {
        var directory = Path.Combine(packageParent, ".relaycove-updater-" + token);
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "RelayCove.Updater.exe"), [1, 2, 3]);
        File.WriteAllText(
            Path.Combine(directory, ".relaycove-bootstrap-owner"),
            "relaycove-bootstrap-owner:" + token,
            new UTF8Encoding(false));
        return directory;
    }

    private static ClientUpdateState CreateMandatoryState(string releaseNotes = "Required fixes.")
    {
        var manifest = new UpdateManifestDto(
            SchemaVersion: UpdateConstants.SchemaVersion,
            Channel: UpdateConstants.Channel,
            Version: "1.0.1-rc.1",
            MinimumSupportedVersion: "1.0.1-rc.1",
            Mandatory: true,
            Artifact: new UpdateArtifactDto(
                Type: UpdateConstants.ArtifactTypePortableZip,
                Url: "https://updates.example.test/RelayCove-1.0.1-rc.1.zip",
                SizeBytes: 123,
                Sha256: new string('a', 64)),
            ReleaseNotes: releaseNotes);
        return new ClientUpdateState(
            ClientUpdatePhase.MandatoryAvailable,
            CurrentVersion: "1.0.0",
            manifest,
            UpdateDecisionKind.Mandatory,
            Progress: null,
            ArchivePath: null,
            ClientUpdateFailure.None);
    }

    private static ClientUpdateState CreateOptionalState()
    {
        var manifest = new UpdateManifestDto(
            SchemaVersion: UpdateConstants.SchemaVersion,
            Channel: UpdateConstants.Channel,
            Version: "1.0.1-rc.1",
            MinimumSupportedVersion: "1.0.0",
            Mandatory: false,
            Artifact: new UpdateArtifactDto(
                Type: UpdateConstants.ArtifactTypePortableZip,
                Url: "https://updates.example.test/RelayCove-1.0.1-rc.1.zip",
                SizeBytes: 123,
                Sha256: new string('b', 64)),
            ReleaseNotes: "Optional fixes.");
        return new ClientUpdateState(
            ClientUpdatePhase.OptionalAvailable,
            CurrentVersion: "1.0.0",
            manifest,
            UpdateDecisionKind.Optional,
            Progress: null,
            ArchivePath: null,
            ClientUpdateFailure.None);
    }

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

    private static void AssertWithinBounds(FrameworkElement root, FrameworkElement element)
    {
        Assert.True(element.IsVisible);
        var bounds = element.TransformToAncestor(root).TransformBounds(
            new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        Assert.True(bounds.Left >= -1, $"{element.Name} is clipped on the left.");
        Assert.True(bounds.Top >= -1, $"{element.Name} is clipped at the top.");
        Assert.True(bounds.Right <= root.ActualWidth + 1, $"{element.Name} is clipped on the right.");
        Assert.True(bounds.Bottom <= root.ActualHeight + 1, $"{element.Name} is clipped at the bottom.");
    }
}
