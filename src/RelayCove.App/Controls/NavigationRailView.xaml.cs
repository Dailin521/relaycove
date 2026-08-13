namespace RelayCove.App.Controls;

public partial class NavigationRailView : ContentView
{
    public NavigationRailView()
    {
        InitializeComponent();
    }

    public void FocusAccountButton() => AccountMenuButton.Focus();
}
