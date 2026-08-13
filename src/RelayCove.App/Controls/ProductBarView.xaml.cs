using RelayCove.App.Services;
using RelayCove.App.ViewModels;

namespace RelayCove.App.Controls;

public partial class ProductBarView : TitleBar
{
    private readonly IWindowShellAdapter _windowShellAdapter;

    public static readonly BindableProperty IsPinnedProperty = BindableProperty.Create(
        nameof(IsPinned),
        typeof(bool),
        typeof(ProductBarView));

    public ProductBarView(IWindowShellAdapter windowShellAdapter)
    {
        _windowShellAdapter = windowShellAdapter ?? throw new ArgumentNullException(nameof(windowShellAdapter));
        InitializeComponent();
        _windowShellAdapter.StateChanged += OnWindowStateChanged;
        IsPinned = _windowShellAdapter.IsPinned;
    }

    public bool IsPinned
    {
        get => (bool)GetValue(IsPinnedProperty);
        set => SetValue(IsPinnedProperty, value);
    }

    public void Bind(ShellViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        BindingContext = viewModel;
        ConnectionStatusLabel.SetBinding(
            Label.TextProperty,
            new Binding(nameof(ShellViewModel.ConnectionStatus), source: viewModel));
    }

    private void OnPinClicked(object? sender, EventArgs eventArgs)
    {
        _windowShellAdapter.TogglePinned();
        IsPinned = _windowShellAdapter.IsPinned;
    }

    private void OnWindowStateChanged(object? sender, EventArgs eventArgs) =>
        Dispatcher.Dispatch(() => IsPinned = _windowShellAdapter.IsPinned);
}
