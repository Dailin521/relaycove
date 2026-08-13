using RelayCove.App.ViewModels;

namespace RelayCove.App.Controls;

public partial class ConversationPaneView : ContentView
{
    public ConversationPaneView()
    {
        InitializeComponent();
    }

    private void OnSearchFocused(object? sender, FocusEventArgs eventArgs)
    {
        if (BindingContext is ShellViewModel viewModel)
        {
            viewModel.OpenSearchCommand.Execute(null);
        }
    }
}
