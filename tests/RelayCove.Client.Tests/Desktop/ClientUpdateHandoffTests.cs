using System.Diagnostics;
using RelayCove.Client;
using RelayCove.Shared.Updates;

namespace RelayCove.Client.Tests.Desktop;

public sealed class ClientUpdateHandoffTests
{
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
}
