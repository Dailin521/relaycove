namespace RelayCove.Server.Options;

public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    public string UploadsPath { get; init; } = "data/uploads";
}
