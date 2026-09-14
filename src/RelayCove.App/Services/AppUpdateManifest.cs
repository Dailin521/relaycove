namespace RelayCove.App.Services;

internal sealed record AppUpdateManifest(
    int SchemaVersion,
    string ProductId,
    string Platform,
    string Version,
    int BuildNumber,
    string InstallerName,
    long Size,
    string Sha256);
