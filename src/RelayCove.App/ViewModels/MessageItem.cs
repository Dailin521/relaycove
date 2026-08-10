using System.Windows.Input;

namespace RelayCove.App.ViewModels;

public sealed record MessageItem(
    string Id,
    string Sender,
    string Content,
    string Timestamp,
    string? DeliveryState = null,
    bool CanRecover = false,
    ICommand? RecoverCommand = null);
