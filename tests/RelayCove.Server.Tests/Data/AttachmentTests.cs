using RelayCove.Server.Data.Entities;

namespace RelayCove.Server.Tests.Data;

public sealed class AttachmentTests
{
    private static readonly DateTime CreatedAt = new(2026, 8, 4, 0, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void Constructor_WhenValuesAreValid_PreservesImmutableMetadata()
    {
        var id = Guid.Parse("63778bf9-d222-44e2-993f-de613209bf54");
        var uploaderId = Guid.Parse("16d92aa1-9350-4d28-b976-0172331359fb");
        var storedFileName = $"{id:N}_{new string('a', 32)}";

        var attachment = new Attachment(
            id,
            uploaderId,
            "报告 🛰️.bin",
            storedFileName,
            "application/octet-stream",
            42,
            new string('b', Attachment.Sha256Length),
            CreatedAt.AddTicks(9876));

        Assert.Equal(id, attachment.Id);
        Assert.Null(attachment.MessageId);
        Assert.Equal(uploaderId, attachment.UploaderUserId);
        Assert.Equal("报告 🛰️.bin", attachment.OriginalFileName);
        Assert.Equal(storedFileName, attachment.StoredFileName);
        Assert.Equal("application/octet-stream", attachment.ContentType);
        Assert.Equal(42, attachment.Size);
        Assert.Equal(new string('b', Attachment.Sha256Length), attachment.Sha256);
        Assert.Equal(CreatedAt, attachment.CreatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../secret.txt")]
    [InlineData("folder\\secret.txt")]
    [InlineData("name\u202Etxt.exe")]
    public void Constructor_WhenOriginalFileNameIsInvalid_Throws(string originalFileName)
    {
        var exception = Record.Exception(() => CreateAttachment(originalFileName: originalFileName));

        Assert.IsAssignableFrom<ArgumentException>(exception);
    }

    [Fact]
    public void Constructor_WhenOriginalFileNameExceedsScalarLimit_Throws()
    {
        var exact = string.Concat(Enumerable.Repeat("\U0001F6F0", Attachment.MaximumOriginalFileNameLength));
        var tooLong = exact + "a";

        Assert.Equal(exact, CreateAttachment(originalFileName: exact).OriginalFileName);
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateAttachment(originalFileName: tooLong));
    }

    [Fact]
    public void Constructor_WhenStoredNameHashSizeOrContentTypeIsInvalid_Throws()
    {
        var id = Guid.NewGuid();
        Assert.Throws<ArgumentException>(() => CreateAttachment(id: id, storedFileName: $"{id:N}_{new string('g', 32)}"));
        Assert.Throws<ArgumentException>(() => CreateAttachment(sha256: new string('A', Attachment.Sha256Length)));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateAttachment(size: 0));
        Assert.Throws<ArgumentException>(() => CreateAttachment(contentType: " "));
    }

    private static Attachment CreateAttachment(
        Guid? id = null,
        string originalFileName = "file.bin",
        string? storedFileName = null,
        string contentType = "application/octet-stream",
        long size = 1,
        string? sha256 = null)
    {
        var effectiveId = id ?? Guid.NewGuid();
        return new Attachment(
            effectiveId,
            Guid.NewGuid(),
            originalFileName,
            storedFileName ?? $"{effectiveId:N}_{new string('a', 32)}",
            contentType,
            size,
            sha256 ?? new string('b', Attachment.Sha256Length),
            CreatedAt);
    }
}
