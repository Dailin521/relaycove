namespace RelayCove.Core;

public interface IMessageMutationObserver
{
    event EventHandler<MessageMutationObservedEventArgs>? MessageMutationObserved;
}
