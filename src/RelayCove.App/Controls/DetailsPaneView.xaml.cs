using RelayCove.App.ViewModels;

namespace RelayCove.App.Controls;

public partial class DetailsPaneView : ContentView
{
    public static readonly BindableProperty IsModalProperty = BindableProperty.Create(
        nameof(IsModal),
        typeof(bool),
        typeof(DetailsPaneView));

    public DetailsPaneView()
    {
        InitializeComponent();
    }

    public bool IsModal
    {
        get => (bool)GetValue(IsModalProperty);
        set => SetValue(IsModalProperty, value);
    }

    public ShellViewModel? ViewModel => BindingContext as ShellViewModel;

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        OnPropertyChanged(nameof(ViewModel));
    }

    internal void FocusCloseButton() => CloseButton.Focus();

    internal void FocusUnsubscribeButton() => UnsubscribeButton.Focus();

    private void OnDirectMessageMutedToggled(object? sender, ToggledEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel && eventArgs.Value != viewModel.IsSelectedDirectMessageMuted)
            viewModel.ToggleDirectMessageMutedCommand.Execute(null);
    }

    private void OnDirectMessagePinnedToggled(object? sender, ToggledEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel && eventArgs.Value != viewModel.IsSelectedDirectMessagePinned)
            viewModel.ToggleDirectMessagePinnedCommand.Execute(null);
    }

    private void OnChannelMutedToggled(object? sender, ToggledEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel && eventArgs.Value != viewModel.IsSelectedChannelMuted)
            viewModel.ToggleSelectedChannelMutedCommand.Execute(null);
    }

    private void OnChannelPinnedToggled(object? sender, ToggledEventArgs eventArgs)
    {
        if (ViewModel is { } viewModel && eventArgs.Value != viewModel.IsSelectedChannelPinned)
            viewModel.ToggleSelectedChannelPinnedCommand.Execute(null);
    }
}
