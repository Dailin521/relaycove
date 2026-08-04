namespace RelayCove.Shared.Updates;

public static class UpdateConstants
{
    public const int SchemaVersion = 1;
    public const string Channel = "internal-rc";
    public const string ArtifactTypePortableZip = "portable-zip";
    public const long MaximumArtifactBytes = 2L * 1024 * 1024 * 1024;
    public const int MaximumArtifactUrlLength = 2048;
    public const int MaximumReleaseNotesLength = 8192;
    public const int MaximumVersionLength = 64;
}
