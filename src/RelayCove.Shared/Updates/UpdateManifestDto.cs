namespace RelayCove.Shared.Updates;

public sealed record UpdateManifestDto(
    int SchemaVersion,
    string Channel,
    string Version,
    string MinimumSupportedVersion,
    bool Mandatory,
    UpdateArtifactDto Artifact,
    string ReleaseNotes);
