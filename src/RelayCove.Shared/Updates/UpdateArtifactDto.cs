namespace RelayCove.Shared.Updates;

public sealed record UpdateArtifactDto(
    string Type,
    string Url,
    long SizeBytes,
    string Sha256);
