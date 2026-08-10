namespace RelayCove.App.Services;

public sealed class MauiUiDispatcher : IUiDispatcher
{
    public void Dispatch(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (Application.Current?.Dispatcher is { } dispatcher)
        {
            dispatcher.Dispatch(action);
            return;
        }

        action();
    }
}
