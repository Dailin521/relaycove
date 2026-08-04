namespace RelayCove.Server.Options;

public sealed class UpdateOptions
{
    public const string SectionName = "Update";

    public string ManifestPath { get; init; } = "updates/manifest.json";
}
