using System.Windows.Input;
using Microsoft.Maui.Platform;
using RelayCove.App.Services;
using RelayCove.App.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinDataPackageOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation;
using WinUiDragEventArgs = Microsoft.UI.Xaml.DragEventArgs;

namespace RelayCove.App.Platforms.Windows.Behaviors;

public sealed class FileDropBehavior : Behavior<Border>
{
    private ContentPanel? _platformView;
    private Border? _virtualView;

    public static readonly BindableProperty CommandProperty = BindableProperty.Create(
        nameof(Command),
        typeof(ICommand),
        typeof(FileDropBehavior));

    public static readonly BindableProperty IsDragActiveProperty = BindableProperty.Create(
        nameof(IsDragActive),
        typeof(bool),
        typeof(FileDropBehavior),
        false,
        BindingMode.TwoWay);

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public bool IsDragActive
    {
        get => (bool)GetValue(IsDragActiveProperty);
        set => SetValue(IsDragActiveProperty, value);
    }

    protected override void OnAttachedTo(Border bindable)
    {
        base.OnAttachedTo(bindable);
        _virtualView = bindable;
        bindable.HandlerChanged += OnHandlerChanged;
        AttachNativeView(bindable.Handler?.PlatformView as ContentPanel);
    }

    protected override void OnDetachingFrom(Border bindable)
    {
        bindable.HandlerChanged -= OnHandlerChanged;
        DetachNativeView();
        _virtualView = null;
        base.OnDetachingFrom(bindable);
    }

    private void OnHandlerChanged(object? sender, EventArgs eventArgs) =>
        AttachNativeView((sender as Border)?.Handler?.PlatformView as ContentPanel);

    private void AttachNativeView(ContentPanel? platformView)
    {
        DetachNativeView();
        if (platformView is null) return;
        _platformView = platformView;
        platformView.AllowDrop = true;
        platformView.DragEnter += OnDragEnter;
        platformView.DragOver += OnDragOver;
        platformView.DragLeave += OnDragLeave;
        platformView.Drop += OnDrop;
    }

    private void DetachNativeView()
    {
        if (_platformView is null) return;
        _platformView.DragEnter -= OnDragEnter;
        _platformView.DragOver -= OnDragOver;
        _platformView.DragLeave -= OnDragLeave;
        _platformView.Drop -= OnDrop;
        _platformView.AllowDrop = false;
        _platformView = null;
        IsDragActive = false;
    }

    private void OnDragEnter(object sender, WinUiDragEventArgs eventArgs) => UpdateDragState(eventArgs);

    private void OnDragOver(object sender, WinUiDragEventArgs eventArgs) => UpdateDragState(eventArgs);

    private void UpdateDragState(WinUiDragEventArgs eventArgs)
    {
        var hasFiles = eventArgs.DataView.Contains(StandardDataFormats.StorageItems);
        eventArgs.AcceptedOperation = hasFiles ? WinDataPackageOperation.Copy : WinDataPackageOperation.None;
        IsDragActive = hasFiles;
        eventArgs.Handled = true;
    }

    private void OnDragLeave(object sender, WinUiDragEventArgs eventArgs)
    {
        IsDragActive = false;
        eventArgs.Handled = true;
    }

    private async void OnDrop(object sender, WinUiDragEventArgs eventArgs)
    {
        IsDragActive = false;
        eventArgs.Handled = true;
        if (!eventArgs.DataView.Contains(StandardDataFormats.StorageItems)) return;

        try
        {
            var storageItems = await eventArgs.DataView.GetStorageItemsAsync();
            var selected = new List<SelectedAttachmentFile>();
            foreach (var file in storageItems.OfType<StorageFile>())
            {
                var properties = await file.GetBasicPropertiesAsync();
                var length = properties.Size > long.MaxValue ? long.MaxValue : (long)properties.Size;
                selected.Add(new SelectedAttachmentFile(
                    file.Name,
                    file.ContentType,
                    length,
                    async cancellationToken =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        return await file.OpenStreamForReadAsync();
                    },
                    string.IsNullOrWhiteSpace(file.Path) ? null : file.Path));
            }

            var command = Command ?? ResolveViewModel()?.AddDroppedAttachmentsCommand;
            if (command?.CanExecute(selected) == true) command.Execute(selected);
        }
        catch
        {
            ResolveViewModel()?.AddDroppedAttachmentsCommand.Execute(Array.Empty<SelectedAttachmentFile>());
        }
    }

    private ShellViewModel? ResolveViewModel()
    {
        for (Element? current = _virtualView; current is not null; current = current.Parent)
        {
            if (current.BindingContext is ShellViewModel viewModel) return viewModel;
        }
        return null;
    }
}
