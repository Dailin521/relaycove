using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using RelayCove.App.Platforms.Windows;
using RelayCove.App.Services;

namespace RelayCove.App.Tests;

public sealed class NativeFileDropTargetTests
{
    [Theory]
    [InlineData(true, true, 1u, 1u)]
    [InlineData(false, true, 1u, 0u)]
    [InlineData(true, false, 1u, 0u)]
    [InlineData(true, true, 2u, 0u)]
    public void DragEnter_WhenFormatPositionOrAllowedOperationDiffers_OnlyAcceptsFileCopyInComposer(
        bool files, bool inside, uint offered, uint expected)
    {
        var active = false;
        using var target = new NativeFileDropTarget((_, _) => inside, value => active = value, _ => { });
        var data = new FileDataObject([], files);
        var effect = offered;

        Assert.Equal(0, target.DragEnter(data, 0, new(), ref effect));

        Assert.Equal(expected, effect);
        Assert.Equal(expected != 0, active);
        Assert.Equal(0, data.Reads);
        target.DragLeave();
        Assert.False(active);
        effect = 1;
        target.DragOver(0, new(), ref effect);
        Assert.Equal(0u, effect);
    }

    [Fact]
    public void DragOver_WhenPointerLeavesComposer_RejectsDropAndClearsHighlight()
    {
        var active = false;
        using var target = new NativeFileDropTarget((x, _) => x < 100, value => active = value, _ => { });
        uint effect = 1;
        target.DragEnter(new FileDataObject([]), 0, new() { X = 20 }, ref effect);
        Assert.True(active);
        effect = 1;
        target.DragOver(0, new() { X = 200 }, ref effect);
        Assert.Equal(0u, effect);
        Assert.False(active);
    }

    [Fact]
    public async Task Drop_WhenFilesIncludeUnicode_CopiesNativePathsAndReadsOriginalFilesInOrder()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RichChat-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var first = Path.Combine(directory, "图片 [一].png");
        var second = Path.Combine(directory, "notes.txt");
        await File.WriteAllBytesAsync(first, [1, 2, 3]);
        await File.WriteAllBytesAsync(second, [4, 5]);
        try
        {
            Func<CancellationToken, Task<IReadOnlyList<SelectedAttachmentFile>>>? read = null;
            var calls = 0;
            using var target = new NativeFileDropTarget((_, _) => true, _ => { }, reader => { calls++; read = reader; });
            var data = new FileDataObject([first, second]);
            uint effect = 1;
            target.DragEnter(data, 0, new(), ref effect);
            target.Drop(data, 0, new(), ref effect);
            Assert.Equal(1u, effect);
            Assert.Equal(1, calls);
            Assert.Equal(1, data.Reads);
            var selected = await read!(CancellationToken.None);
            Assert.Equal(new[] { "图片 [一].png", "notes.txt" }, selected.Select(file => file.FileName));
            Assert.Equal("image/png", selected[0].ContentType);
            Assert.Equal(first, selected[0].LocalPath);
            Assert.Equal(3, selected[0].Length);
            await using var stream = await selected[1].OpenReadAsync();
            Assert.Equal(4, stream.ReadByte());
            Assert.Equal(5, stream.ReadByte());
        }
        finally
        {
            File.Delete(first);
            File.Delete(second);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task Drop_WhenNativeReadFails_PassesSafeFailureToGuardedDraftCommand()
    {
        Func<CancellationToken, Task<IReadOnlyList<SelectedAttachmentFile>>>? read = null;
        using var target = new NativeFileDropTarget((_, _) => true, _ => { }, reader => read = reader);
        var data = new FileDataObject([]) { ThrowOnRead = true };
        uint effect = 1;
        target.DragEnter(data, 0, new(), ref effect);
        target.Drop(data, 0, new(), ref effect);

        Assert.Equal(0u, effect);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => read!(CancellationToken.None));
        Assert.Equal("Unable to read dropped files.", error.Message);
    }

    [Fact]
    public async Task CreateAsync_WhenSelectionContainsFolder_RejectsEntireBatch()
    {
        var file = Path.GetTempFileName();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                NativeDroppedFileFactory.CreateAsync([file, Path.GetTempPath()], CancellationToken.None));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task CreateAsync_WhenCancelled_DoesNotReadFiles()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NativeDroppedFileFactory.CreateAsync(["unavailable"], new CancellationToken(true)));
    }

    [Fact]
    public async Task Drop_WhenSourcePumpsMessagesDuringRead_CapturesDraftBeforeReadingNativeData()
    {
        var captured = false;
        var capturedDuringRead = false;
        Task<IReadOnlyList<SelectedAttachmentFile>>? pending = null;
        using var target = new NativeFileDropTarget((_, _) => true, _ => { }, reader =>
        {
            captured = true;
            pending = reader(CancellationToken.None);
        });
        var data = new FileDataObject([])
        {
            BeforeRead = () => capturedDuringRead = captured,
            ThrowOnRead = true
        };
        uint effect = 1;
        target.DragEnter(data, 0, new(), ref effect);
        target.Drop(data, 0, new(), ref effect);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pending!);
        Assert.True(capturedDuringRead);
        Assert.Equal(1, data.Reads);
    }

    [Fact]
    public async Task Attach_WhenNativeWindowHasChild_RegistersOleTargetsAndReleasesBothOnDispose()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            nint parent = 0;
            nint child = 0;
            try
            {
                // These HWNDs are never shown and are unrelated to the user's MAUI window.
                parent = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 1, 1, 0, 0, 0, 0);
                child = CreateWindowEx(0, "STATIC", "", 0x40000000, 0, 0, 1, 1, parent, 0, 0, 0);
                Assert.NotEqual(nint.Zero, parent);
                Assert.NotEqual(nint.Zero, child);
                using (var target = new NativeFileDropTarget((_, _) => false, _ => { }, _ => { }))
                {
                    target.Attach(parent);
                    Assert.Equal(unchecked((int)0x80040101), RegisterDragDrop(parent, target));
                    Assert.Equal(unchecked((int)0x80040101), RegisterDragDrop(child, target));
                }
                Assert.Equal(unchecked((int)0x80040100), RevokeDragDrop(parent));
                Assert.Equal(unchecked((int)0x80040100), RevokeDragDrop(child));
                completion.SetResult();
            }
            catch (Exception error) { completion.SetException(error); }
            finally
            {
                if (child != 0) DestroyWindow(child);
                if (parent != 0) DestroyWindow(parent);
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void ComInterface_WhenExposedToOle_ProvidesDropTargetInterface()
    {
        using var target = new NativeFileDropTarget((_, _) => false, _ => { }, _ => { });
        var pointer = Marshal.GetComInterfaceForObject<NativeFileDropTarget, IOleFileDropTarget>(target);
        try { Assert.NotEqual(nint.Zero, pointer); }
        finally { Marshal.Release(pointer); }
    }

    private sealed class FileDataObject(string[] paths, bool hasFiles = true) : IDataObject
    {
        public int Reads { get; private set; }
        public bool ThrowOnRead { get; init; }
        public Action? BeforeRead { get; init; }
        public int QueryGetData(ref FORMATETC format) =>
            hasFiles && format.cfFormat == 15 && format.tymed == TYMED.TYMED_HGLOBAL ? 0 : unchecked((int)0x80040064);

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            Reads++;
            BeforeRead?.Invoke();
            if (ThrowOnRead) throw new InvalidOperationException("Source-specific failure.");
            var bytes = Encoding.Unicode.GetBytes(string.Join('\0', paths) + "\0\0");
            var handle = GlobalAlloc(0x42, (nuint)(20 + bytes.Length));
            var pointer = GlobalLock(handle);
            Marshal.WriteInt32(pointer, 0, 20); // DROPFILES.pFiles
            Marshal.WriteInt32(pointer, 16, 1); // DROPFILES.fWide
            Marshal.Copy(bytes, 0, pointer + 20, bytes.Length);
            GlobalUnlock(handle);
            medium = new STGMEDIUM { tymed = TYMED.TYMED_HGLOBAL, unionmember = handle };
        }

        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new NotSupportedException();
        public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = default; return 1; }
        public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => throw new NotSupportedException();
        public IEnumFORMATETC EnumFormatEtc(DATADIR direction) => throw new NotSupportedException();
        public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return 1; }
        public void DUnadvise(int connection) { }
        public int EnumDAdvise(out IEnumSTATDATA? enumerator) { enumerator = null; return 1; }

        [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
        [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint handle);
        [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint handle);
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowEx(uint extendedStyle, string className, string title,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("ole32.dll")] private static extern int RegisterDragDrop(nint window, IOleFileDropTarget target);
    [DllImport("ole32.dll")] private static extern int RevokeDragDrop(nint window);
}
