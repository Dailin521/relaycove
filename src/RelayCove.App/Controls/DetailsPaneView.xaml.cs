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
}
