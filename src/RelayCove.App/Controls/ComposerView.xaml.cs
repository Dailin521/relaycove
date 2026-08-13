using RelayCove.App.ViewModels;

namespace RelayCove.App.Controls;

public partial class ComposerView : ContentView
{
    public ComposerView()
    {
        InitializeComponent();
    }

    public ShellViewModel? ViewModel => BindingContext as ShellViewModel;

    protected override void OnBindingContextChanged()
    {
        base.OnBindingContextChanged();
        OnPropertyChanged(nameof(ViewModel));
    }
}
