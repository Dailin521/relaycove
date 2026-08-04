namespace RelayCove.Client.Attachments;

internal enum WindowsAttachmentOpenStatus
{
    Prepared = 1,
    Executed = 2,
    PolicyRejected = 3,
    Unavailable = 4,
    Aborted = 5,
    Canceled = 6,
    UserCanceled = 7,
    NoAssociation = 8,
    ExecuteFailed = 9,
    Busy = 10,
}
