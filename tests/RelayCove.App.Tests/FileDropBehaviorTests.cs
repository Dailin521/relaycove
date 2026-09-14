using System.Xml.Linq;

namespace RelayCove.App.Tests;

public sealed class FileDropBehaviorTests
{
    [Theory]
    [InlineData("DragEnter", "_dragEnterHandler")]
    [InlineData("DragOver", "_dragOverHandler")]
    [InlineData("DragLeave", "_dragLeaveHandler")]
    [InlineData("Drop", "_dropHandler")]
    public void NativeDrop_WhenChildHandlesEvent_ReceivesRoutedEventAndDetachesHandler(string eventName, string handler)
    {
        var source = ReadSource("Platforms", "Windows", "Behaviors", "FileDropBehavior.cs");

        Assert.Contains($"AddHandler(WinUiElement.{eventName}Event, {handler}, true)", source);
        Assert.Contains($"RemoveHandler(WinUiElement.{eventName}Event, {handler})", source);
    }

    [Fact]
    public void Composer_WhenElevated_ConnectsOleFallbackWithVisibleHitTestAndDetachesOnUnload()
    {
        var source = ReadSource("Platforms", "Windows", "Behaviors", "FileDropBehavior.cs");
        Assert.Contains("!WindowsProcessEnvironment.IsElevated()", source);
        Assert.Contains("new NativeFileDropTarget(CanDropAtScreenPoint", source);
        Assert.Contains("FindElementsInHostCoordinates(position, root.Content).FirstOrDefault()", source);
        Assert.Contains("point.X / root.RasterizationScale", source);
        Assert.Contains("platformView.Unloaded += OnNativeUnloaded", source);
        Assert.Contains("_nativeDropTarget?.Dispose()", source);
    }

    [Fact]
    public void NativeDrop_WhenFilesAreReadAsynchronously_KeepsDataAliveUntilCommandCompletes()
    {
        var source = ReadSource("Platforms", "Windows", "Behaviors", "FileDropBehavior.cs");

        Assert.Contains("eventArgs.GetDeferral()", source);
        Assert.Contains("await command.ExecuteAsync(readAsync)", source);
        Assert.Contains("finally", source);
        Assert.Contains("deferral.Complete()", source);
    }

    [Fact]
    public void Composer_WhenFileIsDraggedOverTextInput_EnablesNativeTargetAndBindsGuardedDropCommand()
    {
        var handler = ReadSource("Platforms", "Windows", "Handlers", "ComposerEditorHandler.cs");
        var composer = XDocument.Parse(ReadSource("Controls", "ComposerView.xaml"));
        var behavior = Assert.Single(composer.Descendants(), element => element.Name.LocalName == "FileDropBehavior");

        Assert.Contains("_editor.AllowDrop = true;", handler);
        Assert.Contains("ViewModel.DropAttachmentsCommand", behavior.Attribute("Command")?.Value);
        Assert.Contains("ViewModel.CanCompose", behavior.Attribute("IsDropEnabled")?.Value);
    }

    private static string ReadSource(params string[] parts)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine([directory.FullName, "src", "RelayCove.App", .. parts]);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("Unable to locate the drop target source.");
    }
}
