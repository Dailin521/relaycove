using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace RelayCove.Client.Attachments;

[SupportedOSPlatform("windows")]
internal sealed class WindowsAttachmentOpenNativeBackend : IWindowsAttachmentOpenNativeBackend
{
    private const uint CoinitApartmentThreaded = 0x2;
    private const uint ClsctxInprocServer = 0x1;
    private static readonly Guid AttachmentServicesClassId = new("4125DD96-E03A-4103-8F70-E0597D803B9C");
    private static readonly Guid AttachmentExecuteInterfaceId = new("73DB1241-1E85-4581-8E4F-A81E1D0F8C57");

    public int InitializeApartment() => NativeMethods.CoInitializeEx(
        IntPtr.Zero,
        CoinitApartmentThreaded);

    public void UninitializeApartment() => NativeMethods.CoUninitialize();

    public bool IsWindow(IntPtr window) =>
        window != IntPtr.Zero && NativeMethods.IsWindow(window);

    public int CreateAttachmentExecute(out IWindowsAttachmentExecuteNative? attachmentExecute)
    {
        attachmentExecute = null;
        var classId = AttachmentServicesClassId;
        var interfaceId = AttachmentExecuteInterfaceId;
        var result = NativeMethods.CoCreateInstance(
            ref classId,
            IntPtr.Zero,
            ClsctxInprocServer,
            ref interfaceId,
            out var nativePointer);
        if (result < 0 || nativePointer == IntPtr.Zero)
        {
            return result < 0 ? result : unchecked((int)0x80004005);
        }

        try
        {
            var nativeObject = Marshal.GetObjectForIUnknown(nativePointer);
            if (nativeObject is not IAttachmentExecuteCom native)
            {
                if (Marshal.IsComObject(nativeObject))
                {
                    Marshal.FinalReleaseComObject(nativeObject);
                }

                return unchecked((int)0x80004002);
            }

            attachmentExecute = new AttachmentExecuteNative(native);
            return result;
        }
        finally
        {
            Marshal.Release(nativePointer);
        }
    }

    public void ReleaseAttachmentExecute(IWindowsAttachmentExecuteNative attachmentExecute)
    {
        if (attachmentExecute is AttachmentExecuteNative native)
        {
            Marshal.FinalReleaseComObject(native.Value);
        }
    }

    public bool CloseProcessHandle(IntPtr processHandle) =>
        NativeMethods.CloseHandle(processHandle);

    private sealed class AttachmentExecuteNative(IAttachmentExecuteCom value) :
        IWindowsAttachmentExecuteNative
    {
        public IAttachmentExecuteCom Value { get; } = value;

        public int SetClientTitle(string title) => Value.SetClientTitle(title);

        public int SetClientGuid(Guid clientGuid) => Value.SetClientGuid(ref clientGuid);

        public int SetLocalPath(string localPath) => Value.SetLocalPath(localPath);

        public int CheckPolicy() => Value.CheckPolicy();

        public int Execute(
            IntPtr ownerWindow,
            Action enteredExecute,
            out IntPtr processHandle)
        {
            ArgumentNullException.ThrowIfNull(enteredExecute);
            enteredExecute();
            return Value.Execute(ownerWindow, null, out processHandle);
        }
    }

    [ComImport]
    [Guid("73DB1241-1E85-4581-8E4F-A81E1D0F8C57")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAttachmentExecuteCom
    {
        [PreserveSig]
        int SetClientTitle([MarshalAs(UnmanagedType.LPWStr)] string title);

        [PreserveSig]
        int SetClientGuid(ref Guid clientGuid);

        [PreserveSig]
        int SetLocalPath([MarshalAs(UnmanagedType.LPWStr)] string localPath);

        [PreserveSig]
        int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string fileName);

        [PreserveSig]
        int SetSource([MarshalAs(UnmanagedType.LPWStr)] string source);

        [PreserveSig]
        int SetReferrer([MarshalAs(UnmanagedType.LPWStr)] string referrer);

        [PreserveSig]
        int CheckPolicy();

        [PreserveSig]
        int Prompt(IntPtr ownerWindow, int prompt, out int action);

        [PreserveSig]
        int Save();

        [PreserveSig]
        int Execute(
            IntPtr ownerWindow,
            [MarshalAs(UnmanagedType.LPWStr)] string? verb,
            out IntPtr processHandle);
    }

    private static class NativeMethods
    {
        [DllImport("ole32.dll", ExactSpelling = true)]
        internal static extern int CoInitializeEx(IntPtr reserved, uint coInit);

        [DllImport("ole32.dll", ExactSpelling = true)]
        internal static extern void CoUninitialize();

        [DllImport("ole32.dll", ExactSpelling = true)]
        internal static extern int CoCreateInstance(
            ref Guid classId,
            IntPtr outer,
            uint classContext,
            ref Guid interfaceId,
            out IntPtr instance);

        [DllImport("user32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(IntPtr window);

        [DllImport("kernel32.dll", ExactSpelling = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CloseHandle(IntPtr handle);
    }
}
