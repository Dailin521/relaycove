using System.IO;
using RelayCove.Client.Attachments;
using RelayCove.Shared.Messages;

namespace RelayCove.Client.Tests.Attachments;

public sealed class ClientAttachmentFileSourceFactoryTests : IDisposable
{
    private const long MaximumSizeBytes = 100L * 1024 * 1024;
    private readonly string rootDirectory = Path.Combine(
        Path.GetTempPath(),
        "RelayCove.AttachmentSelection.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateAsync_WhenPathsAreNullOrEmpty_ReturnsNoFilesSelected()
    {
        var nullOutcome = await ClientAttachmentFileSourceFactory.CreateAsync(null);
        var emptyOutcome = await ClientAttachmentFileSourceFactory.CreateAsync([]);

        Assert.Equal(ClientAttachmentFileSelectionStatus.NoFilesSelected, nullOutcome.Status);
        Assert.Equal(ClientAttachmentFileSelectionStatus.NoFilesSelected, emptyOutcome.Status);
        Assert.Empty(nullOutcome.Selections);
        Assert.Empty(emptyOutcome.Selections);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task CreateAsync_WhenOneToTenFilesAreValid_ReturnsOnlyNewBatch(int count)
    {
        var paths = Enumerable.Range(1, count)
            .Select(index => CreateFile($"image-{index}.png", index))
            .ToArray();

        var outcome = await ClientAttachmentFileSourceFactory.CreateAsync(paths);

        Assert.Equal(ClientAttachmentFileSelectionStatus.Success, outcome.Status);
        Assert.Equal(count, outcome.Selections.Count);
        Assert.Equal(count, outcome.Selections.Select(selection => selection.DraftId).Distinct().Count());
        Assert.All(outcome.Selections, selection => Assert.True(selection.IsImage));
        Assert.Equal(MessageType.Image, ClientAttachmentFileSourceFactory.ResolveMessageType(outcome.Selections));
    }

    [Fact]
    public async Task CreateAsync_WhenElevenFilesAreSelected_ReturnsTooManyFilesAtomically()
    {
        var paths = Enumerable.Range(1, 11)
            .Select(index => CreateFile($"file-{index}.txt", 1))
            .ToArray();

        var outcome = await ClientAttachmentFileSourceFactory.CreateAsync(paths);

        Assert.Equal(ClientAttachmentFileSelectionStatus.TooManyFiles, outcome.Status);
        Assert.Empty(outcome.Selections);
    }

    [Fact]
    public async Task CreateAsync_WhenExistingSelectionsReachLimit_ReturnsOnlyAllowedNewBatchThenRejectsOverflow()
    {
        var existingPaths = Enumerable.Range(1, 9)
            .Select(index => CreateFile($"existing-{index}.txt", 1))
            .ToArray();
        var existing = await ClientAttachmentFileSourceFactory.CreateAsync(existingPaths);
        var tenthPath = CreateFile("tenth.txt", 1);

        var tenth = await ClientAttachmentFileSourceFactory.CreateAsync([tenthPath], existing.Selections);
        var overflow = await ClientAttachmentFileSourceFactory.CreateAsync(
            [CreateFile("eleventh.txt", 1)],
            existing.Selections.Concat(tenth.Selections).ToArray());

        Assert.Equal(ClientAttachmentFileSelectionStatus.Success, tenth.Status);
        Assert.Single(tenth.Selections);
        Assert.Equal(ClientAttachmentFileSelectionStatus.TooManyFiles, overflow.Status);
        Assert.Empty(overflow.Selections);
    }

    [Fact]
    public async Task CreateAsync_WhenSamePathAppearsTwiceInBatch_ReturnsDuplicateAtomically()
    {
        var path = CreateFile("duplicate.txt", 1);

        var outcome = await ClientAttachmentFileSourceFactory.CreateAsync([path, path.ToUpperInvariant()]);

        Assert.Equal(ClientAttachmentFileSelectionStatus.DuplicateFile, outcome.Status);
        Assert.Empty(outcome.Selections);
    }

    [Fact]
    public async Task CreateAsync_WhenPathDuplicatesExistingSelection_ReturnsDuplicateAtomically()
    {
        var path = CreateFile("existing.txt", 1);
        var existing = await ClientAttachmentFileSourceFactory.CreateAsync([path]);

        var outcome = await ClientAttachmentFileSourceFactory.CreateAsync(
            [path.ToUpperInvariant()],
            existing.Selections);

        Assert.Equal(ClientAttachmentFileSelectionStatus.DuplicateFile, outcome.Status);
        Assert.Empty(outcome.Selections);
    }

    [Fact]
    public async Task CreateAsync_WhenUnicodeFileIsValid_PreservesDisplayNameWithoutExposingPath()
    {
        var path = CreateFile("发布说明-猫咪.png", 7);

        var outcome = await ClientAttachmentFileSourceFactory.CreateAsync([path]);

        var selection = Assert.Single(outcome.Selections);
        Assert.Equal("发布说明-猫咪.png", selection.DisplayName);
        Assert.Equal("发布说明-猫咪.png", selection.Source.OriginalFileName);
        Assert.Equal("7 B", selection.DisplaySize);
        Assert.NotEqual(path, selection.DisplayName);
    }

    [Fact]
    public async Task CreateAsync_WhenFileIsEmpty_ReturnsEmptyFile()
    {
        var outcome = await ClientAttachmentFileSourceFactory.CreateAsync(
            [CreateFile("empty.bin", 0)]);

        Assert.Equal(ClientAttachmentFileSelectionStatus.EmptyFile, outcome.Status);
        Assert.Empty(outcome.Selections);
    }

    [Fact]
    public async Task CreateAsync_WhenFileIsExactlyMaximumSize_SucceedsWithoutBufferingContents()
    {
        var outcome = await ClientAttachmentFileSourceFactory.CreateAsync(
            [CreateFile("maximum.bin", MaximumSizeBytes)]);

        var selection = Assert.Single(outcome.Selections);
        Assert.Equal(ClientAttachmentFileSelectionStatus.Success, outcome.Status);
        Assert.Equal(MaximumSizeBytes, selection.Source.Size);
        Assert.Equal("100 MiB", selection.DisplaySize);
    }

    [Fact]
    public async Task CreateAsync_WhenFileExceedsMaximumSize_ReturnsFileTooLarge()
    {
        var outcome = await ClientAttachmentFileSourceFactory.CreateAsync(
            [CreateFile("too-large.bin", MaximumSizeBytes + 1)]);

        Assert.Equal(ClientAttachmentFileSelectionStatus.FileTooLarge, outcome.Status);
        Assert.Empty(outcome.Selections);
    }

    [Fact]
    public async Task CreateAsync_WhenAnyPathIsMissing_ReturnsNoPartialSelections()
    {
        var valid = CreateFile("valid.txt", 1);
        var missing = Path.Combine(rootDirectory, "missing.txt");

        var outcome = await ClientAttachmentFileSourceFactory.CreateAsync([valid, missing]);

        Assert.Equal(ClientAttachmentFileSelectionStatus.FileNotFound, outcome.Status);
        Assert.Empty(outcome.Selections);
    }

    [Fact]
    public async Task CreateAsync_WhenPathIsRelative_ReturnsInvalidPath()
    {
        var outcome = await ClientAttachmentFileSourceFactory.CreateAsync(["relative.txt"]);

        Assert.Equal(ClientAttachmentFileSelectionStatus.InvalidPath, outcome.Status);
        Assert.Empty(outcome.Selections);
    }

    [Fact]
    public async Task CreateAsync_WhenPathIsDirectory_ReturnsInvalidPath()
    {
        Directory.CreateDirectory(rootDirectory);

        var outcome = await ClientAttachmentFileSourceFactory.CreateAsync([rootDirectory]);

        Assert.Equal(ClientAttachmentFileSelectionStatus.InvalidPath, outcome.Status);
        Assert.Empty(outcome.Selections);
    }

    [Fact]
    public async Task CreateAsync_WhenFileIsLocked_ReturnsFileUnavailable()
    {
        var path = CreateFile("locked.bin", 1);
        await using var exclusive = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            useAsync: true);

        var outcome = await ClientAttachmentFileSourceFactory.CreateAsync([path]);

        Assert.Equal(ClientAttachmentFileSelectionStatus.FileUnavailable, outcome.Status);
        Assert.Empty(outcome.Selections);
    }

    [Theory]
    [InlineData("photo.jpg", "image/jpeg")]
    [InlineData("photo.JPEG", "image/jpeg")]
    [InlineData("photo.jpe", "image/jpeg")]
    [InlineData("photo.jfif", "image/jpeg")]
    [InlineData("photo.png", "image/png")]
    [InlineData("photo.gif", "image/gif")]
    [InlineData("photo.bmp", "image/bmp")]
    [InlineData("photo.webp", "image/webp")]
    [InlineData("photo.tif", "image/tiff")]
    [InlineData("photo.tiff", "image/tiff")]
    [InlineData("photo.avif", "image/avif")]
    [InlineData("photo.heic", "image/heic")]
    [InlineData("photo.heif", "image/heif")]
    public async Task CreateAsync_WhenExtensionIsControlledImage_ClassifiesImage(
        string fileName,
        string expectedContentType)
    {
        var outcome = await ClientAttachmentFileSourceFactory.CreateAsync(
            [CreateFile(fileName, 1)]);

        var selection = Assert.Single(outcome.Selections);
        Assert.True(selection.IsImage);
        Assert.Equal(expectedContentType, selection.Source.ContentType);
        Assert.Equal(MessageType.Image, ClientAttachmentFileSourceFactory.ResolveMessageType(outcome.Selections));
    }

    [Theory]
    [InlineData("clip.mp4", "video/mp4")]
    [InlineData("clip.m4v", "video/mp4")]
    [InlineData("clip.mov", "video/quicktime")]
    [InlineData("clip.webm", "video/webm")]
    [InlineData("clip.avi", "video/x-msvideo")]
    [InlineData("clip.mkv", "video/x-matroska")]
    [InlineData("document.pdf", "application/pdf")]
    [InlineData("notes.txt", "text/plain")]
    [InlineData("data.json", "application/json")]
    [InlineData("archive.zip", "application/zip")]
    [InlineData("vector.svg", "image/svg+xml")]
    [InlineData("unknown.relaycove", "application/octet-stream")]
    public async Task CreateAsync_WhenExtensionIsNotControlledImage_ClassifiesFile(
        string fileName,
        string expectedContentType)
    {
        var outcome = await ClientAttachmentFileSourceFactory.CreateAsync(
            [CreateFile(fileName, 1)]);

        var selection = Assert.Single(outcome.Selections);
        Assert.False(selection.IsImage);
        Assert.Equal(expectedContentType, selection.Source.ContentType);
        Assert.Equal(MessageType.File, ClientAttachmentFileSourceFactory.ResolveMessageType(outcome.Selections));
    }

    [Fact]
    public async Task ResolveMessageType_WhenImagesAreMixedWithFile_ReturnsFile()
    {
        var outcome = await ClientAttachmentFileSourceFactory.CreateAsync(
            [CreateFile("image.png", 1), CreateFile("video.mp4", 1)]);

        Assert.Equal(ClientAttachmentFileSelectionStatus.Success, outcome.Status);
        Assert.Equal(MessageType.File, ClientAttachmentFileSourceFactory.ResolveMessageType(outcome.Selections));
    }

    [Fact]
    public async Task SourceOpenReadAsync_WhenFileIsUnchanged_ReturnsAsyncSeekableReadOnlyStream()
    {
        var path = CreateFile("reopen.bin", 3, [0x01, 0x02, 0x03]);
        var outcome = await ClientAttachmentFileSourceFactory.CreateAsync([path]);

        await using var stream = await Assert.Single(outcome.Selections).Source.OpenReadAsync(
            CancellationToken.None);

        var fileStream = Assert.IsType<FileStream>(stream);
        Assert.True(fileStream.IsAsync);
        Assert.True(fileStream.CanRead);
        Assert.True(fileStream.CanSeek);
        Assert.False(fileStream.CanWrite);
        Assert.Equal(3, fileStream.Length);
        Assert.Equal(0, fileStream.Position);
    }

    [Fact]
    public async Task SourceOpenReadAsync_WhenFileWasDeleted_FailsClosed()
    {
        var path = CreateFile("deleted.bin", 1);
        var outcome = await ClientAttachmentFileSourceFactory.CreateAsync([path]);
        File.Delete(path);

        await Assert.ThrowsAsync<FileNotFoundException>(async () =>
            await Assert.Single(outcome.Selections).Source.OpenReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SourceOpenReadAsync_WhenFileLengthChanged_FailsClosed()
    {
        var path = CreateFile("changed.bin", 1);
        var outcome = await ClientAttachmentFileSourceFactory.CreateAsync([path]);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            stream.SetLength(2);
        }

        await Assert.ThrowsAsync<IOException>(async () =>
            await Assert.Single(outcome.Selections).Source.OpenReadAsync(CancellationToken.None));
    }

    [Fact]
    public async Task SourceOpenReadAsync_WhenCanceled_DoesNotOpenFile()
    {
        var path = CreateFile("reopen-canceled.bin", 1);
        var outcome = await ClientAttachmentFileSourceFactory.CreateAsync([path]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Assert.Single(outcome.Selections).Source.OpenReadAsync(cancellation.Token));
    }

    [Fact]
    public async Task CreateAsync_WhenCanceled_ReturnsCanceledWithoutSelections()
    {
        var path = CreateFile("canceled.bin", 1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var outcome = await ClientAttachmentFileSourceFactory.CreateAsync(
            [path],
            cancellationToken: cancellation.Token);

        Assert.Equal(ClientAttachmentFileSelectionStatus.Canceled, outcome.Status);
        Assert.Empty(outcome.Selections);
    }

    [Fact]
    public async Task ToString_RedactsPathNameMimeSizeAndDraftIdentity()
    {
        var path = CreateFile("classified-name.png", 17);
        var outcome = await ClientAttachmentFileSourceFactory.CreateAsync([path]);
        var selection = Assert.Single(outcome.Selections);

        var selectionText = selection.ToString();
        var outcomeText = outcome.ToString();
        var sourceText = selection.Source.ToString();

        foreach (var text in new[] { selectionText, outcomeText, sourceText })
        {
            Assert.DoesNotContain(path, text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(selection.DisplayName, text, StringComparison.Ordinal);
            Assert.DoesNotContain(selection.Source.ContentType, text, StringComparison.Ordinal);
            Assert.DoesNotContain(selection.Source.Size.ToString(), text, StringComparison.Ordinal);
            Assert.DoesNotContain(selection.DraftId.ToString(), text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(rootDirectory))
        {
            Directory.Delete(rootDirectory, recursive: true);
        }
    }

    private string CreateFile(string fileName, long length, byte[]? content = null)
    {
        Directory.CreateDirectory(rootDirectory);
        var path = Path.Combine(rootDirectory, fileName);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        if (content is not null)
        {
            stream.Write(content);
        }
        else
        {
            stream.SetLength(length);
        }

        Assert.Equal(length, stream.Length);
        return path;
    }
}
