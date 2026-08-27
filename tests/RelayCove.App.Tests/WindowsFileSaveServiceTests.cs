using RelayCove.App.Platforms.Windows;

namespace RelayCove.App.Tests;

public sealed class WindowsFileSaveServiceTests
{
    [Fact]
    public void DefaultDownloadFolderName_WhenBrandIsRendered_UsesRichChat()
    {
        Assert.Equal("RichChat", WindowsFileSaveService.DefaultDownloadFolderName);
    }

    [Fact]
    public void SanitizeFileName_WhenPathOrInvalidCharactersArePresent_ReturnsSafeLeafName()
    {
        var result = WindowsFileSaveService.SanitizeFileName(@"folder\report:final?.pdf");

        Assert.Equal("report_final_.pdf", result);
    }

    [Fact]
    public void CreateUniquePath_WhenNameExists_AppendsFirstAvailableSuffix()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"relaycove-download-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "guide.pdf"), "existing");
            File.WriteAllText(Path.Combine(directory, "guide (1).pdf"), "existing");

            var result = WindowsFileSaveService.CreateUniquePath(directory, "guide.pdf");

            Assert.Equal(Path.Combine(directory, "guide (2).pdf"), result);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
