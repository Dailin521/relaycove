namespace RelayCove.App.Controls;

// An input-transparent overlay: the underlying Button retains keyboard/accessibility behavior.
public sealed class DownloadBorderProgress : GraphicsView, IDrawable
{
    public static readonly BindableProperty ProgressProperty = BindableProperty.Create(
        nameof(Progress), typeof(double), typeof(DownloadBorderProgress), 0d,
        propertyChanged: OnProgressChanged);
    public static readonly BindableProperty IsRunningProperty = BindableProperty.Create(
        nameof(IsRunning), typeof(bool), typeof(DownloadBorderProgress), false,
        propertyChanged: OnProgressChanged);
    public static readonly BindableProperty IsIndeterminateProperty = BindableProperty.Create(
        nameof(IsIndeterminate), typeof(bool), typeof(DownloadBorderProgress), false,
        propertyChanged: OnProgressChanged);
    public static readonly BindableProperty ProgressColorProperty = BindableProperty.Create(
        nameof(ProgressColor), typeof(Color), typeof(DownloadBorderProgress), Colors.DodgerBlue,
        propertyChanged: OnProgressChanged);

    private Microsoft.Maui.Dispatching.IDispatcherTimer? _timer;
    private bool _loaded;
    private double _phase;
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public bool IsRunning { get => (bool)GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }
    public bool IsIndeterminate { get => (bool)GetValue(IsIndeterminateProperty); set => SetValue(IsIndeterminateProperty, value); }
    public Color ProgressColor { get => (Color)GetValue(ProgressColorProperty); set => SetValue(ProgressColorProperty, value); }

    public DownloadBorderProgress()
    {
        Drawable = this;
        InputTransparent = true;
        Loaded += (_, _) => { _loaded = true; UpdateAnimation(); };
        Unloaded += (_, _) => { _loaded = false; UpdateAnimation(); };
    }

    private static void OnProgressChanged(BindableObject sender, object oldValue, object newValue)
    {
        var view = (DownloadBorderProgress)sender;
        view.UpdateAnimation();
        view.Invalidate();
    }

    private void UpdateAnimation()
    {
        if (!_loaded || !IsRunning || !IsIndeterminate)
        {
            _timer?.Stop();
            _phase = 0;
            return;
        }
        if (_timer is null)
        {
            _timer = Dispatcher.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(33);
            _timer.Tick += (_, _) => { _phase = (_phase + 0.02) % 1; Invalidate(); };
        }
        _timer.Start();
    }

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        if (!IsRunning || dirtyRect.Width <= 2 || dirtyRect.Height <= 2) return;
        var start = IsIndeterminate ? _phase : 0;
        var amount = IsIndeterminate ? 0.25 : Math.Clamp(Progress, 0, 1);
        if (amount <= 0) return;
        var path = new PathF();
        var steps = Math.Max(1, (int)Math.Ceiling(amount * 240));
        for (var i = 0; i <= steps; i++)
        {
            var point = PointOnOutline(dirtyRect.Width, dirtyRect.Height, (start + amount * i / steps) % 1);
            if (i == 0) path.MoveTo(point.X, point.Y);
            else path.LineTo(point.X, point.Y);
        }
        canvas.StrokeColor = ProgressColor;
        canvas.StrokeSize = 2;
        canvas.DrawPath(path);
    }

    // Start at top center and traverse a radius-7 button clockwise at constant speed.
    internal static PointF PointOnOutline(float width, float height, double fraction)
    {
        var w = width - 2;
        var h = height - 2;
        var r = MathF.Min(6, MathF.Min(w, h) / 2);
        var horizontal = w - 2 * r;
        var vertical = h - 2 * r;
        var arc = Math.PI * r / 2;
        var distance = fraction * (2 * horizontal + 2 * vertical + 4 * arc);
        if (distance <= horizontal / 2) return new(1 + w / 2 + (float)distance, 1);
        distance -= horizontal / 2;
        for (var corner = 0; corner < 4; corner++)
        {
            var cx = corner < 2 ? 1 + w - r : 1 + r;
            var cy = corner is 0 or 3 ? 1 + r : 1 + h - r;
            if (distance <= arc)
            {
                var angle = (-90 + corner * 90) * Math.PI / 180 + distance / r;
                return new(cx + r * (float)Math.Cos(angle), cy + r * (float)Math.Sin(angle));
            }
            distance -= arc;
            var length = corner % 2 == 0 ? vertical : horizontal;
            if (distance <= length)
                return corner switch
                {
                    0 => new(1 + w, 1 + r + (float)distance),
                    1 => new(1 + w - r - (float)distance, 1 + h),
                    2 => new(1, 1 + h - r - (float)distance),
                    _ => new(1 + r + (float)distance, 1)
                };
            distance -= length;
        }
        return new(width / 2, 1);
    }
}
