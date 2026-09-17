namespace RelayCove.App.Controls;

internal sealed class ImagePreviewTransform
{
    public double Scale { get; private set; } = 1;
    public double X { get; private set; }
    public double Y { get; private set; }

    public void Zoom(int wheelDelta, double pointerX, double pointerY, double width, double height)
    {
        if (wheelDelta == 0 || width <= 0 || height <= 0) return;
        var next = Math.Clamp(Scale * Math.Pow(1.2, wheelDelta / 120d), 0.25, 8);
        var ratio = next / Scale;
        // MAUI image transforms are relative to the centre of the fitted image view.
        X = pointerX - width / 2 - (pointerX - width / 2 - X) * ratio;
        Y = pointerY - height / 2 - (pointerY - height / 2 - Y) * ratio;
        Scale = next;
    }

    public void Pan(double deltaX, double deltaY)
    {
        X += deltaX;
        Y += deltaY;
    }

    public void Reset()
    {
        Scale = 1;
        X = Y = 0;
    }
}
