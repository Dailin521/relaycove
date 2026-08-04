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

    int Execute(IntPtr ownerWindow, out IntPtr processHandle);
}
