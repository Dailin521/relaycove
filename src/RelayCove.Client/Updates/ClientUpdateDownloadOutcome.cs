using System.IO;

namespace RelayCove.Client.Updates;

internal sealed record ClientUpdateDownloadOutcome(
    ClientUpdateDownloadStatus Status,
    string? ArchivePath)
{
    public static ClientUpdateDownloadOutcome Success(string archivePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        if (!Path.IsPathFullyQualified(archivePath))
        {
            throw new ArgumentException("The update archive path must be absolute.", nameof(archivePath));
        }

        return new(ClientUpdateDownloadStatus.Success, archivePath);
    }

    public static ClientUpdateDownloadOutcome Failure(ClientUpdateDownloadStatus status) =>
        new(status, ArchivePath: null);

    public override string ToString() =>
        $"{nameof(ClientUpdateDownloadOutcome)} {{ Status = {Status}, ArchivePath = [REDACTED] }}";
}
