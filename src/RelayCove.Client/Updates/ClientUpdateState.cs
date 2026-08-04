using RelayCove.Shared.Updates;

namespace RelayCove.Client.Updates;

internal sealed record ClientUpdateState(
    ClientUpdatePhase Phase,
    string? CurrentVersion,
    UpdateManifestDto? Manifest,
    UpdateDecisionKind? Decision,
    ClientUpdateDownloadProgress? Progress,
    string? ArchivePath,
    ClientUpdateFailure Failure)
{
    public static ClientUpdateState Idle { get; } = new(
        ClientUpdatePhase.Idle,
        CurrentVersion: null,
        Manifest: null,
        Decision: null,
        Progress: null,
        ArchivePath: null,
        ClientUpdateFailure.None);

    public bool IsMandatory => Decision == UpdateDecisionKind.Unsupported ||
        Decision == UpdateDecisionKind.Mandatory;

    public override string ToString() =>
        $"{nameof(ClientUpdateState)} {{ Phase = {Phase}, CurrentVersion = {CurrentVersion ?? "[none]"}, " +
        "Manifest = [REDACTED], Decision = " +
        $"{Decision?.ToString() ?? "[none]"}, Progress = {Progress?.Percent.ToString() ?? "[none]"}, " +
        "ArchivePath = [REDACTED], Failure = " +
        $"{Failure} }}";
}
