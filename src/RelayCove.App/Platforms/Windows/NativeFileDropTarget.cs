using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace RelayCove.App.Platforms.Windows;

// OLE's CF_HDROP path does not depend on the WinRT drag broker, which fails with UAC disabled.
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class NativeFileDropTarget(
    Func<int, int, bool> canDrop,
    Action<bool> setDragActive,
    Action<Func<CancellationToken, Task<IReadOnlyList<Services.SelectedAttachmentFile>>>> acceptFiles)
    : IOleFileDropTarget, IDisposable
{
    private readonly List<nint> _registeredWindows = [];
    private bool _hasFiles;
    private bool _disposed;
    private bool _oleInitialized;

    internal void Attach(nint windowHandle)
    {
        if (_disposed || windowHandle == 0 || _oleInitialized) return;
        var result = OleInitialize(0);
        if (result < 0) return;
        _oleInitialized = true;
        // WinUI can register its broker on a child input HWND. Replace those registrations
        // only in the elevated compatibility path; every callback still hit-tests the Composer.
        Register(windowHandle);
        EnumChildWindows(windowHandle, (child, _) =>
        {
            try { Register(child); }
            catch { /* Keep registration failure inside the native callback boundary. */ }
            return true;
        }, 0);
    }

    private void Register(nint windowHandle)
    {
        _ = RevokeDragDrop(windowHandle);
        if (RegisterDragDrop(windowHandle, this) >= 0) _registeredWindows.Add(windowHandle);
    }

    public int DragEnter(IDataObject data, uint keyState, NativeDropPoint point, ref uint effect)
    {
        try
        {
            var format = FileDropFormat();
            _hasFiles = data.QueryGetData(ref format) == 0;
            UpdateEffect(point, ref effect);
        }
        catch { effect = 0; ResetDrag(); }
        return 0;
    }

    public int DragOver(uint keyState, NativeDropPoint point, ref uint effect)
    {
        try { UpdateEffect(point, ref effect); }
        catch { effect = 0; ResetDrag(); }
        return 0;
    }

    public int DragLeave()
    {
        ResetDrag();
        return 0;
    }

    public int Drop(IDataObject data, uint keyState, NativeDropPoint point, ref uint effect)
    {
        try
        {
            UpdateEffect(point, ref effect);
            if (effect == 0) return 0;
            // Capture the draft before GetData, which may pump messages from the source.
            // Only copied paths cross await; the source's COM object is released on return.
            var pathsRead = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            acceptFiles(async token =>
            {
                var paths = await pathsRead.Task.WaitAsync(token).ConfigureAwait(false);
                return await NativeDroppedFileFactory.CreateAsync(paths, token).ConfigureAwait(false);
            });
            try { pathsRead.SetResult(ReadPaths(data)); }
            catch
            {
                pathsRead.SetException(new InvalidOperationException("Unable to read dropped files."));
                effect = 0;
            }
        }
        catch { effect = 0; }
        finally { ResetDrag(); }
        return 0;
    }

    private void UpdateEffect(NativeDropPoint point, ref uint effect)
    {
        var accepted = !_disposed && _hasFiles && (effect & 1) != 0 && canDrop(point.X, point.Y);
        effect = accepted ? 1u : 0;
        setDragActive(accepted);
    }

    private void ResetDrag()
    {
        _hasFiles = false;
        try { setDragActive(false); }
        catch { /* Do not let managed exceptions cross the OLE callback boundary. */ }
    }

    internal static string[] ReadPaths(IDataObject data)
    {
        var format = FileDropFormat();
        data.GetData(ref format, out var medium);
        try
        {
            if (medium.tymed != TYMED.TYMED_HGLOBAL || medium.unionmember == 0)
                throw new InvalidOperationException("Unsupported file drop data.");
            var count = DragQueryFile(medium.unionmember, uint.MaxValue, null, 0);
            if (count == 0) throw new InvalidOperationException("No files were supplied.");
            var paths = new string[count];
            for (uint index = 0; index < count; index++)
            {
                var length = DragQueryFile(medium.unionmember, index, null, 0);
                if (length == 0 || length > 32767) throw new InvalidOperationException("Invalid file drop data.");
                var path = new StringBuilder((int)length + 1);
                if (DragQueryFile(medium.unionmember, index, path, (uint)path.Capacity) != length)
                    throw new InvalidOperationException("Unable to read dropped files.");
                paths[index] = path.ToString();
            }
            return paths;
        }
        finally { ReleaseStgMedium(ref medium); }
    }

    private static FORMATETC FileDropFormat() => new()
    {
        cfFormat = 15, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var window in _registeredWindows) _ = RevokeDragDrop(window);
        _registeredWindows.Clear();
        ResetDrag();
        if (_oleInitialized) OleUninitialize();
        _oleInitialized = false;
    }

    private delegate bool EnumWindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(nint parent, EnumWindowCallback callback, nint parameter);
    [DllImport("ole32.dll")]
    private static extern int OleInitialize(nint reserved);
    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();
    [DllImport("ole32.dll")]
    private static extern int RegisterDragDrop(nint window, IOleFileDropTarget target);
    [DllImport("ole32.dll")]
    private static extern int RevokeDragDrop(nint window);
    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref STGMEDIUM medium);
    [DllImport("shell32.dll", EntryPoint = "DragQueryFileW", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(nint drop, uint index, StringBuilder? path, uint size);
}

[ComImport]
[Guid("00000122-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IOleFileDropTarget
{
    [PreserveSig] int DragEnter(IDataObject data, uint keyState, NativeDropPoint point, ref uint effect);
    [PreserveSig] int DragOver(uint keyState, NativeDropPoint point, ref uint effect);
    [PreserveSig] int DragLeave();
    [PreserveSig] int Drop(IDataObject data, uint keyState, NativeDropPoint point, ref uint effect);
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeDropPoint
{
    public int X;
    public int Y;
}
