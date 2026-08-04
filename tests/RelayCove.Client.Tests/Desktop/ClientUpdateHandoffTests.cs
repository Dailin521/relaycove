using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using RelayCove.Client;
using RelayCove.Client.Updates;
using RelayCove.Shared.Updates;

namespace RelayCove.Client.Tests.Desktop;

public sealed class ClientUpdateHandoffTests
{
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

    private static ClientUpdateState CreateMandatoryState()
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
            ReleaseNotes: "Required fixes.");
        return new ClientUpdateState(
            ClientUpdatePhase.MandatoryAvailable,
            CurrentVersion: "1.0.0",
            manifest,
            UpdateDecisionKind.Mandatory,
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
}
