namespace RelayCove.App.ViewModels;

public sealed record ReactionPickerRequest(
    MessageItem Message,
    double AnchorX,
    double AnchorY);
