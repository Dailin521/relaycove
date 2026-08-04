using Microsoft.Extensions.Logging;
using RelayCove.Shared.Realtime;

namespace RelayCove.Client.Accounts;

internal sealed class ClientAccountRuntimeStateHub
{
    private readonly object stateGate = new();
    private readonly ILogger logger;
    private Action<ConnectionState>? connectionStateChanged;
    private Action<long>? conversationStateChanged;
    private Func<Task>? authenticationRequired;
    private bool stopped;

    public ClientAccountRuntimeStateHub(ILogger logger)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public event Action<ConnectionState> ConnectionStateChanged
    {
        add
        {
            lock (stateGate)
            {
                if (!stopped)
                {
                    connectionStateChanged += value;
                }
            }
        }
        remove
        {
            lock (stateGate)
            {
                connectionStateChanged -= value;
            }
        }
    }

    public event Action<long> ConversationStateChanged
    {
        add
        {
            lock (stateGate)
            {
                if (!stopped)
                {
                    conversationStateChanged += value;
                }
            }
        }
        remove
        {
            lock (stateGate)
            {
                conversationStateChanged -= value;
            }
        }
    }

    public event Func<Task> AuthenticationRequired
    {
        add
        {
            lock (stateGate)
            {
                if (!stopped)
                {
                    authenticationRequired += value;
                }
            }
        }
        remove
        {
            lock (stateGate)
            {
                authenticationRequired -= value;
            }
        }
    }

    public void PublishConnectionState(ConnectionState state)
    {
        Action<ConnectionState>? handlers;
        lock (stateGate)
        {
            handlers = stopped ? null : connectionStateChanged;
        }

        Publish(handlers, state, "connection");
    }

    public void PublishConversationStateChanged(long revision)
    {
        Action<long>? handlers;
        lock (stateGate)
        {
            handlers = stopped ? null : conversationStateChanged;
        }

        Publish(handlers, revision, "conversation");
    }

    public async Task PublishAuthenticationRequiredAsync()
    {
        Func<Task>? handlers;
        lock (stateGate)
        {
            handlers = stopped ? null : authenticationRequired;
        }

        if (handlers is null)
        {
            return;
        }

        foreach (Func<Task> handler in handlers.GetInvocationList())
        {
            try
            {
                await handler().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Publishing account access revocation failed; errorType={ErrorType}.",
                    exception.GetType().Name);
            }
        }
    }

    public void Stop()
    {
        lock (stateGate)
        {
            stopped = true;
            connectionStateChanged = null;
            conversationStateChanged = null;
            authenticationRequired = null;
        }
    }

    private void Publish<T>(Action<T>? handlers, T value, string kind)
    {
        if (handlers is null)
        {
            return;
        }

        foreach (Action<T> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(value);
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    "Publishing an account runtime state change failed; " +
                    "kind={Kind}; errorType={ErrorType}.",
                    kind,
                    exception.GetType().Name);
            }
        }
    }
}
