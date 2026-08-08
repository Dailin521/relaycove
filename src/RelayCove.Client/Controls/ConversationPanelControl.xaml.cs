namespace RelayCove.Client.Controls;

public partial class ConversationPanelControl : System.Windows.Controls.UserControl
{
    public static readonly System.Windows.DependencyProperty CornerRadiusProperty =
        System.Windows.DependencyProperty.Register(
            nameof(CornerRadius),
            typeof(System.Windows.CornerRadius),
            typeof(ConversationPanelControl),
            new System.Windows.PropertyMetadata(new System.Windows.CornerRadius(0)));

    public ConversationPanelControl()
    {
        InitializeComponent();
    }

    public System.Windows.CornerRadius CornerRadius
    {
        get => (System.Windows.CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }
}
