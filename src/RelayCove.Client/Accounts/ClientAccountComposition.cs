using System.IO;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Activation;
using RelayCove.Client.Auth;
using RelayCove.Client.Notifications;

namespace RelayCove.Client.Accounts;

internal sealed class ClientAccountComposition : IAsyncDisposable
{
    private static readonly TimeSpan DefaultHttpTimeout = TimeSpan.FromSeconds(30);
    private readonly object stateGate = new();
    private readonly HttpClient httpClient;
    private Task? disposeTask;
    private bool detachedForProcessExit;

    internal ClientAccountComposition(
        HttpClient httpClient,
        ClientAccountShellCoordinator coordinator)
    {
        this.httpClient = httpClient;
        Coordinator = coordinator;
    }

    public ClientAccountShellCoordinator Coordinator { get; }

    public static ClientAccountComposition Create(
        string localAppDataRoot,
        ClientNotificationActivationRouter activationRouter,
        IClientNotificationAttention notificationAttention,
        ILoggerFactory loggerFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localAppDataRoot);
        if (!Path.IsPathFullyQualified(localAppDataRoot))
        {
            throw new ArgumentException(
                "The client data root must be an absolute path.",
                nameof(localAppDataRoot));
        }

        ArgumentNullException.ThrowIfNull(activationRouter);
        ArgumentNullException.ThrowIfNull(notificationAttention);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var normalizedRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(localAppDataRoot));
        var credentialRoot = ResolveChildDirectory(normalizedRoot, "Authentication");
        var accountRoot = ResolveChildDirectory(normalizedRoot, "Accounts");
        var httpClient = new HttpClient
        {
            Timeout = DefaultHttpTimeout,
        };
        try
        {
            var credentialStore = new ClientCredentialStore(
                credentialRoot,
                loggerFactory.CreateLogger<ClientCredentialStore>());
            var authentication = new PersistentClientAuthentication(
                httpClient,
                credentialStore,
                loggerFactory.CreateLogger<ClientAuthenticationClient>(),
                loggerFactory.CreateLogger<PersistentClientAuthentication>());
            var runtimeFactory = new ClientAccountRuntimeFactory(
                httpClient,
                accountRoot,
                loggerFactory,
                notificationAttention);
            var coordinator = new ClientAccountShellCoordinator(
                authentication,
                runtimeFactory,
                activationRouter,
                loggerFactory.CreateLogger<ClientAccountShellCoordinator>());
            return new ClientAccountComposition(httpClient, coordinator);
        }
        catch
        {
            httpClient.Dispose();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        Task sharedDispose;
        TaskCompletionSource? completion = null;
        lock (stateGate)
        {
            if (disposeTask is null)
            {
                if (detachedForProcessExit)
                {
                    disposeTask = Task.CompletedTask;
                }
                else
                {
                    completion = new TaskCompletionSource(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    disposeTask = completion.Task;
                }
            }

            sharedDispose = disposeTask;
        }

        if (completion is not null)
        {
            _ = CompleteDisposeAsync(completion);
        }

        return new ValueTask(sharedDispose);
    }

    public void DetachForProcessExit()
    {
        lock (stateGate)
        {
            if (disposeTask is not null || detachedForProcessExit)
            {
                return;
            }

            detachedForProcessExit = true;
        }

        Coordinator.DetachForProcessExit();
        // Process-exit detach intentionally abandons the runtime without awaiting it.
        // Its shared HTTP dependency must remain valid until process termination.
    }

    private async Task CompleteDisposeAsync(TaskCompletionSource completion)
    {
        try
        {
            await Coordinator.DisposeAsync().ConfigureAwait(false);
            httpClient.Dispose();
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            httpClient.Dispose();
            completion.TrySetException(exception);
        }
    }

    public override string ToString() =>
        $"{nameof(ClientAccountComposition)} {{ DataRoot = [REDACTED], " +
        "HttpClient = [REDACTED], Coordinator = [REDACTED] }";

    private static string ResolveChildDirectory(string root, string directoryName)
    {
        var child = Path.GetFullPath(Path.Combine(root, directoryName));
        var relative = Path.GetRelativePath(root, child);
        if (Path.IsPathFullyQualified(relative) ||
            string.Equals(relative, "..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Resolved client data directory escaped its root.");
        }

        return child;
    }
}
