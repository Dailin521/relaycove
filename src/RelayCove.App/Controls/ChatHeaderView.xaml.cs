namespace RelayCove.App.Controls;

public partial class ChatHeaderView : ContentView
{
    public ChatHeaderView()
    {
        InitializeComponent();
    }

    internal void FocusSettingsButton() => SettingsButton.Focus();
    internal void FocusSearchButton() => SearchButton.Focus();
}
