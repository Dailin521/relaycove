namespace RelayCove.App.Controls;

public partial class ChatHeaderView : ContentView
{
    public ChatHeaderView()
    {
        InitializeComponent();
    }

    internal void FocusDetailsButton() => DetailsButton.Focus();
    internal void FocusSearchButton() => SearchButton.Focus();
}
