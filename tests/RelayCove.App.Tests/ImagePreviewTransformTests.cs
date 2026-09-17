using RelayCove.App.Controls;

namespace RelayCove.App.Tests;

public sealed class ImagePreviewTransformTests
{
    [Fact]
    public void Zoom_WhenWheelForward_EnlargesAndKeepsPointerAnchor()
    {
        var state = new ImagePreviewTransform();
        state.Pan(30, -10);
        var imageX = (350 - 250 - state.X) / state.Scale;
        var imageY = (100 - 200 - state.Y) / state.Scale;
        state.Zoom(120, 350, 100, 500, 400);
        Assert.Equal(1.2, state.Scale, 8);
        Assert.Equal(350, 250 + imageX * state.Scale + state.X, 8);
        Assert.Equal(100, 200 + imageY * state.Scale + state.Y, 8);
        state.Zoom(-120, 350, 100, 500, 400);
        Assert.Equal(1, state.Scale, 8);
        Assert.Equal(30, state.X, 8);
        Assert.Equal(-10, state.Y, 8);
    }

    [Fact]
    public void Zoom_WhenRepeated_StaysWithinBounds()
    {
        var state = new ImagePreviewTransform();
        for (var i = 0; i < 100; i++) state.Zoom(120, 250, 200, 500, 400);
        Assert.Equal(8, state.Scale);
        for (var i = 0; i < 100; i++) state.Zoom(-120, 250, 200, 500, 400);
        Assert.Equal(0.25, state.Scale);
        Assert.Equal(0, state.X);
        Assert.Equal(0, state.Y);
    }

    [Fact]
    public void Pan_WhenZoomed_MovesInViewportPixelsAndResetRestoresFit()
    {
        var state = new ImagePreviewTransform();
        state.Zoom(120, 250, 200, 500, 400);
        state.Pan(50, -25);
        Assert.Equal(50, state.X);
        Assert.Equal(-25, state.Y);
        state.Reset();
        Assert.Equal(1, state.Scale);
        Assert.Equal(0, state.X);
        Assert.Equal(0, state.Y);
    }

    [Fact]
    public void Zoom_WhenViewportNotReady_DoesNotChangeTransform()
    {
        var state = new ImagePreviewTransform();
        state.Zoom(120, 30, 40, 0, 0);
        Assert.Equal(1, state.Scale);
        Assert.Equal(0, state.X);
        Assert.Equal(0, state.Y);
    }
}
