namespace RelayCove.App.Services;

public interface IUiDispatcher
{
    void Dispatch(Action action);
    Task YieldToRenderAsync(CancellationToken cancellationToken = default);
}
