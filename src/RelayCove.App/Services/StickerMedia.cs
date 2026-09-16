namespace RelayCove.App.Services;

public sealed record StickerMedia(byte[] Content, string ContentType, string FileName, string Hash);
