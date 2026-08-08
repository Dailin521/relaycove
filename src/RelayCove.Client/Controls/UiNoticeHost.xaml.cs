using System.Windows;
using System.Windows.Threading;

namespace RelayCove.Client.Controls;

public partial class UiNoticeHost : System.Windows.Controls.UserControl
{
    private static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(3);
    private readonly DispatcherTimer hideTimer;

    public UiNoticeHost()
    {
        InitializeComponent();
        hideTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = NoticeDuration,
        };
        hideTimer.Tick += OnHideTimerTick;
    }

    public bool IsNoticeVisible => Visibility == Visibility.Visible;

    public string Message => NoticeText.Text;

    public void ShowUnavailableFeature(string displayName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ShowNotice($"{displayName}功能暂未开放");
    }

    public void ShowNotice(string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        NoticeText.Text = message;
        Visibility = Visibility.Visible;
        hideTimer.Stop();
        hideTimer.Start();
    }

    public void HideNotice()
    {
        hideTimer.Stop();
        Visibility = Visibility.Collapsed;
        NoticeText.Text = string.Empty;
    }

    private void OnHideTimerTick(object? sender, EventArgs e)
    {
        _ = sender;
        _ = e;
        HideNotice();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _ = sender;
        _ = e;
        hideTimer.Stop();
    }
}
