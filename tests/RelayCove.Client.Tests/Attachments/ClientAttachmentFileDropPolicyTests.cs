using RelayCove.Client.Attachments;

namespace RelayCove.Client.Tests.Attachments;

public sealed class ClientAttachmentFileDropPolicyTests
{
    [Fact]
    public void Capture_WhenExactFileDropFormatIsNotPresent_RejectsWithoutReadingData()
    {
        var result = ClientAttachmentFileDropPolicy.Capture(
            hasExactFileDrop: false,
            fileDropData: new[] { @"C:\\secret\\report.pdf" },
            currentAttachmentCount: 0);

        Assert.Equal(ClientAttachmentFileDropSnapshotStatus.FileDropFormatNotPresent, result.Status);
        Assert.Empty(result.Paths);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("C:\\secret\\report.pdf")]
    public void Capture_WhenFileDropDataIsNotAStringArray_Rejects(object? fileDropData)
    {
        var result = ClientAttachmentFileDropPolicy.Capture(
            hasExactFileDrop: true,
            fileDropData,
            currentAttachmentCount: 0);

        Assert.Equal(ClientAttachmentFileDropSnapshotStatus.InvalidFileDropData, result.Status);
        Assert.Empty(result.Paths);
    }

    [Fact]
    public void Capture_WhenFileDropDataIsAList_RejectsWithoutConvertingIt()
    {
        var result = ClientAttachmentFileDropPolicy.Capture(
            hasExactFileDrop: true,
            fileDropData: new List<string> { @"C:\\secret\\report.pdf" },
            currentAttachmentCount: 0);

        Assert.Equal(ClientAttachmentFileDropSnapshotStatus.InvalidFileDropData, result.Status);
    }

    [Fact]
    public void Capture_WhenFileDropArrayIsEmpty_Rejects()
    {
        var result = ClientAttachmentFileDropPolicy.Capture(
            hasExactFileDrop: true,
            fileDropData: Array.Empty<string>(),
            currentAttachmentCount: 0);

        Assert.Equal(ClientAttachmentFileDropSnapshotStatus.NoFilesSelected, result.Status);
        Assert.Empty(result.Paths);
    }

    [Fact]
    public void Capture_WhenFilesExceedRemainingAttachmentCapacity_RejectsAtomically()
    {
        var result = ClientAttachmentFileDropPolicy.Capture(
            hasExactFileDrop: true,
            fileDropData: new[] { "first.txt", "second.txt" },
            currentAttachmentCount: 9);

        Assert.Equal(ClientAttachmentFileDropSnapshotStatus.TooManyFiles, result.Status);
        Assert.Empty(result.Paths);
    }

    [Fact]
    public void Capture_WhenFileDropDataIsAccepted_CopiesAndProtectsThePathSnapshot()
    {
        var source = new[] { @"C:\\secret\\one.txt", @"C:\\secret\\two.txt" };

        var result = ClientAttachmentFileDropPolicy.Capture(
            hasExactFileDrop: true,
            fileDropData: source,
            currentAttachmentCount: 8);
        source[0] = @"C:\\secret\\changed.txt";
        var callerCopy = result.Paths;
        callerCopy[1] = @"C:\\secret\\caller-change.txt";

        Assert.Equal(ClientAttachmentFileDropSnapshotStatus.Success, result.Status);
        Assert.True(result.IsSuccess);
        Assert.Equal(@"C:\\secret\\one.txt", result.Paths[0]);
        Assert.Equal(@"C:\\secret\\two.txt", result.Paths[1]);
    }

    [Fact]
    public void ToString_WhenPathsContainSensitiveLocations_RedactsThem()
    {
        var result = ClientAttachmentFileDropPolicy.Capture(
            hasExactFileDrop: true,
            fileDropData: new[] { @"C:\\Users\\Ada\\Documents\\private.pdf" },
            currentAttachmentCount: 0);

        var text = result.ToString();

        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("private.pdf", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Documents", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, false, false)]
    public void CanShowCopyEffect_WhenComposerFormatOrSourceCopyIsUnavailable_ReturnsFalse(
        bool composerCanAccept,
        bool hasExactFileDrop,
        bool sourceAllowsCopy,
        bool expected)
    {
        var result = ClientAttachmentFileDropPolicy.CanShowCopyEffect(
            composerCanAccept,
            hasExactFileDrop,
            sourceAllowsCopy);

        Assert.Equal(expected, result);
    }
}
