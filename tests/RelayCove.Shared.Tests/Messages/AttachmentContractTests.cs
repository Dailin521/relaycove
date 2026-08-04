using System.Text.Json;
using RelayCove.Shared.Messages;

namespace RelayCove.Shared.Tests.Messages;

public sealed class AttachmentContractTests
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    [Fact]
    public void AttachmentDto_WhenRoundTripped_PreservesStableShape()
    {
        var attachment = new AttachmentDto(
            Guid.Parse("c0f83c4e-d4b3-4639-9fdc-06fe3179093e"),
            "报告 🛰️.bin",
            "application/octet-stream",
            42,
            "/api/attachments/c0f83c4e-d4b3-4639-9fdc-06fe3179093e/download",
            null);

        var json = JsonSerializer.Serialize(attachment, WebJson);
        using var document = JsonDocument.Parse(json);
        var roundTripped = JsonSerializer.Deserialize<AttachmentDto>(json, WebJson);

        Assert.Equal(
            ["id", "originalFileName", "contentType", "size", "downloadUrl", "thumbnailUrl"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        Assert.Equal(attachment, roundTripped);
    }

    [Fact]
    public void AttachmentDto_WhenFormatted_RedactsAllMetadata()
    {
        var id = Guid.NewGuid();
        const string fileName = "secret-file-e4132a.bin";
        const string contentType = "application/secret-e4132a";
        const long size = 9_876_543;
        const string downloadUrl = "/secret-e4132a/download";
        const string thumbnailUrl = "/secret-e4132a/thumbnail";
        var attachment = new AttachmentDto(
            id,
            fileName,
            contentType,
            size,
            downloadUrl,
            thumbnailUrl);

        var formatted = attachment.ToString();

        Assert.DoesNotContain(id.ToString("D"), formatted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fileName, formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(contentType, formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(size.ToString(System.Globalization.CultureInfo.InvariantCulture), formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(downloadUrl, formatted, StringComparison.Ordinal);
        Assert.DoesNotContain(thumbnailUrl, formatted, StringComparison.Ordinal);
    }
}
