namespace RelayCove.Core;

public sealed record ChannelFolder(
    long FolderId,
    string Name,
    string? Description,
    bool IsArchived = false,
    int Order = 0);
