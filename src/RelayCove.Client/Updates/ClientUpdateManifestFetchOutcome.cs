using RelayCove.Shared.Updates;

namespace RelayCove.Client.Updates;

internal sealed record ClientUpdateManifestFetchOutcome(
    ClientUpdateFetchStatus Status,
    UpdateManifestDto? Manifest)
{
    public static ClientUpdateManifestFetchOutcome Success(UpdateManifestDto manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return new(ClientUpdateFetchStatus.Success, manifest);
    }

    public static ClientUpdateManifestFetchOutcome Failure(ClientUpdateFetchStatus status) =>
        new(status, Manifest: null);

    public override string ToString() =>
        $"{nameof(ClientUpdateManifestFetchOutcome)} {{ Status = {Status}, Manifest = [REDACTED] }}";
}
