namespace RelayCove.Client.Attachments;

internal interface IWindowsAttachmentOpenNativeBackend
{
    int InitializeApartment();

    void UninitializeApartment();

    bool IsWindow(IntPtr window);

    int CreateAttachmentExecute(out IWindowsAttachmentExecuteNative? attachmentExecute);

    void ReleaseAttachmentExecute(IWindowsAttachmentExecuteNative attachmentExecute);

    bool CloseProcessHandle(IntPtr processHandle);
}

internal interface IWindowsAttachmentExecuteNative
{
    int SetClientTitle(string title);

    int SetClientGuid(Guid clientGuid);

    int SetLocalPath(string localPath);

    int CheckPolicy();

    // Invokes enteredExecute at the native Execute call boundary, immediately
    // before the COM call. Service shutdown uses this acknowledgement to keep
    // the background STA alive for an already committed launch without waiting
    // for the external application or policy provider to return.
    int Execute(
        IntPtr ownerWindow,
        Action enteredExecute,
        out IntPtr processHandle);
}
