using System.ComponentModel;
using RelayCove.App.ViewModels;

namespace RelayCove.App.Controls;

public partial class ChannelSettingsOverlayView : ContentView
{
    private ChannelSettingsViewModel? _viewModel;

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
        if (eventArgs.PropertyName is nameof(ChannelSettingsViewModel.IsEditDialogOpen) or
            nameof(ChannelSettingsViewModel.IsCreateFolderOpen) or
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
        else if (viewModel.IsConfirmationOpen) ConfirmationCancelButton.Focus();
        else CloseButton.Focus();
    }
}
