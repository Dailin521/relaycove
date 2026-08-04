using RelayCove.Shared.Updates;

namespace RelayCove.Updater;

internal sealed class UpdaterOptions
{
    internal required string ArchivePath { get; init; }

    internal required string ExpectedSha256 { get; init; }

    internal required long ExpectedSize { get; init; }

    internal required SemanticVersion ExpectedVersion { get; init; }

    internal required SemanticVersion CurrentVersion { get; init; }

    internal required string TargetPath { get; init; }

    internal required int WaitProcessId { get; init; }

    internal required long WaitProcessStartTimeUtcTicks { get; init; }

    internal required int WaitTimeoutSeconds { get; init; }

    internal required string BootstrapToken { get; init; }

    internal required bool Bootstrapped { get; init; }
}
