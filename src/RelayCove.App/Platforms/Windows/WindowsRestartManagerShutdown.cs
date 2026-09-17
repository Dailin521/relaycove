using System.Runtime.InteropServices;

namespace RelayCove.App.Platforms.Windows;

// Restart Manager uses session messages, not an ordinary user close request.
internal sealed class WindowsRestartManagerShutdown : IDisposable
{
    private readonly SubclassProcedure _procedure;
    private readonly Action _requestExit;
    private nint _window;
    private const nuint SubclassId = 0x52434D;

    internal WindowsRestartManagerShutdown(nint window, Action requestExit)
    {
        _requestExit = requestExit;
        _procedure = WindowProcedure;
        if (SetWindowSubclass(window, _procedure, SubclassId, 0)) _window = window;
        else WindowsLifecycleDiagnostics.Write("restart-manager-hook-failed");
    }

    internal static bool TryHandleMessage(uint message, nint wordParameter, nint longParameter,
        Action requestExit, out nint result)
    {
        result = 0;
        const uint queryEndSession = 0x0011;
        const uint endSession = 0x0016;
        const long closeApp = 1;
        if (((long)longParameter & closeApp) == 0) return false;
        if (message == queryEndSession)
        {
            // Agree, but do not exit until shutdown has actually been confirmed.
            result = 1;
            return true;
        }
        if (message != endSession) return false;
        if (wordParameter != 0) requestExit();
        return true;
    }

    private nint WindowProcedure(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data)
    {
        try
        {
            if (TryHandleMessage(message, wParam, lParam, _requestExit, out var result)) return result;
            if (message == 0x0082) Dispose(); // WM_NCDESTROY
        }
        catch (Exception)
        {
            WindowsLifecycleDiagnostics.Write("restart-manager-message-failed");
        }
        return DefSubclassProc(window, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (_window == 0) return;
        _ = RemoveWindowSubclass(_window, _procedure, SubclassId);
        _window = 0;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProcedure(nint window, uint message, nint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProcedure procedure, nuint id, nuint data);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure procedure, nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nint wParam, nint lParam);
}
