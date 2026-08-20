using System.ComponentModel;
using RelayCove.App.ViewModels;

namespace RelayCove.App.Controls;

public partial class ChannelSettingsOverlayView : ContentView
{
    private ChannelSettingsViewModel? _viewModel;
    private VisualElement? _memberRemovalTrigger;
    private VisualElement? _colorPickerTrigger;

    public ChannelSettingsOverlayView() => InitializeComponent();

    protected override void OnBindingContextChanged()
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        base.OnBindingContextChanged();
        _viewModel = BindingContext as ChannelSettingsViewModel;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelPropertyChanged;
    }

    public void FocusCloseButton() => FocusTopLayer();

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(ChannelSettingsViewModel.IsMemberRemovalConfirmationOpen) &&
            _viewModel?.IsMemberRemovalConfirmationOpen == false && _memberRemovalTrigger is { } memberRemovalTrigger)
        {
            _memberRemovalTrigger = null;
            Dispatcher.Dispatch(() => memberRemovalTrigger.Focus());
            return;
        }

        if (eventArgs.PropertyName == nameof(ChannelSettingsViewModel.IsColorPickerOpen) &&
            _viewModel?.IsColorPickerOpen == false && _colorPickerTrigger is { } colorPickerTrigger)
        {
            _colorPickerTrigger = null;
            Dispatcher.Dispatch(() => colorPickerTrigger.Focus());
            return;
        }

        if (eventArgs.PropertyName is nameof(ChannelSettingsViewModel.IsEditDialogOpen) or
            nameof(ChannelSettingsViewModel.IsCreateFolderOpen) or
            nameof(ChannelSettingsViewModel.IsCreateChannelOpen) or
            nameof(ChannelSettingsViewModel.IsColorPickerOpen) or
            nameof(ChannelSettingsViewModel.IsMemberRemovalConfirmationOpen) or
            nameof(ChannelSettingsViewModel.Confirmation))
        {
            Dispatcher.Dispatch(FocusTopLayer);
        }
    }

    private void FocusTopLayer()
    {
        if (BindingContext is not ChannelSettingsViewModel viewModel) return;
        if (viewModel.IsEditDialogOpen) EditValueEditor.Focus();
        else if (viewModel.IsCreateFolderOpen) NewFolderNameEntry.Focus();
        else if (viewModel.IsCreateChannelOpen) NewChannelNameEntry.Focus();
        else if (viewModel.IsColorPickerOpen) ColorPickerConfirmButton.Focus();
        else if (viewModel.IsMemberRemovalConfirmationOpen) MemberRemovalCancelButton.Focus();
        else if (viewModel.IsConfirmationOpen) ConfirmationCancelButton.Focus();
        else CloseButton.Focus();
    }

    private void OnRemoveMemberClicked(object? sender, EventArgs eventArgs)
    {
        if (sender is not Button { BindingContext: ChannelMemberItem member } button ||
            _viewModel?.RequestRemoveMemberCommand.CanExecute(member) != true)
        {
            return;
        }

        _memberRemovalTrigger = button;
        _viewModel.RequestRemoveMemberCommand.Execute(member);
    }

    private void OnChannelRowTapped(object? sender, TappedEventArgs eventArgs)
    {
        var channel = eventArgs.Parameter as ChannelSettingsChannelItem ??
            (sender as TapGestureRecognizer)?.BindingContext as ChannelSettingsChannelItem;
        if (channel is null ||
            _viewModel?.SelectChannelCommand.CanExecute(channel) != true)
        {
            return;
        }

        _viewModel.SelectChannelCommand.Execute(channel);
    }

    private void OnChannelSubscriptionClicked(object? sender, EventArgs eventArgs)
    {
        if (sender is not Button { BindingContext: ChannelSettingsChannelItem channel } ||
            _viewModel?.ChangeChannelSubscriptionCommand.CanExecute(channel) != true)
        {
            return;
        }

        _viewModel.ChangeChannelSubscriptionCommand.Execute(channel);
    }

    private void OnOpenColorPickerClicked(object? sender, EventArgs eventArgs)
    {
        if (sender is not VisualElement trigger ||
            _viewModel?.OpenPersonalColorPickerCommand.CanExecute(null) != true)
        {
            return;
        }

        _colorPickerTrigger = trigger;
        var anchor = GetPopoverAnchor(trigger);
        _viewModel.ColorPickerAnchorX = anchor.X;
        _viewModel.ColorPickerAnchorY = anchor.Y;
        _viewModel.OpenPersonalColorPickerCommand.Execute(null);
    }

    private void OnColorSwatchClicked(object? sender, EventArgs eventArgs)
    {
        if (sender is not Button { BindingContext: ChannelColorOption option } ||
            _viewModel?.SelectPersonalColorCommand.CanExecute(option.Hex) != true)
        {
            return;
        }

        _viewModel.SelectPersonalColorCommand.Execute(option.Hex);
    }

    private static Point GetPopoverAnchor(VisualElement trigger)
    {
#if WINDOWS
        var source = trigger.Handler?.PlatformView as Microsoft.UI.Xaml.FrameworkElement;
        var pageRoot = Application.Current?.Windows
            .Select(window => window.Page?.Handler?.PlatformView)
            .OfType<Microsoft.UI.Xaml.FrameworkElement>()
            .FirstOrDefault();
        if (source is not null && pageRoot is not null)
        {
            try
            {
                var point = source.TransformToVisual(pageRoot)
                    .TransformPoint(new Windows.Foundation.Point(source.ActualWidth, 0d));
                return new Point(point.X, point.Y);
            }
            catch (InvalidOperationException)
            {
            }
        }
#endif
        return new Point(12d, 68d);
    }
}
