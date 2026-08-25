namespace RelayCove.App.ViewModels;

public sealed record ImageAttachmentMenuRequest(
    MessageItem Message,
    MessageAttachmentItem Attachment,
    double AnchorX,
    double AnchorY);
