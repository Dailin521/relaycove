namespace RelayCove.App.Services;

public sealed class AppUpdateInfo
{
    internal AppUpdateInfo(string version, int buildNumber, string releaseNotes, DateTimeOffset publishedAt,
        string installerName, long installerSize, string sha256, Uri downloadUri)
    {
        Version = version;
        BuildNumber = buildNumber;
        ReleaseNotes = releaseNotes;
        PublishedAt = publishedAt;
        InstallerName = installerName;
        InstallerSize = installerSize;
        Sha256 = sha256;
        DownloadUri = downloadUri;
    }

    public string Version { get; }
    public int BuildNumber { get; }
    public string ReleaseNotes { get; }
    public DateTimeOffset PublishedAt { get; }
    public string InstallerName { get; }
    public long InstallerSize { get; }
    public string Sha256 { get; }
    internal Uri DownloadUri { get; }
}
