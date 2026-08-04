namespace RelayCove.Client.Updates;

internal enum ClientUpdateFailure
{
    None,
    Canceled,
    CurrentVersionInvalid,
    ManifestUnavailable,
    ManifestInvalid,
    DownloadFailed,
    NoUpdateAvailable,
}
