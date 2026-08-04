using System.IO;
using System.Net;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using RelayCove.Client.Activation;
using RelayCove.Client.Attachments;
using RelayCove.Client.Auth;
using RelayCove.Client.Notifications;

namespace RelayCove.Client.Accounts;

internal sealed class ClientAccountComposition : IAsyncDisposable
{
    private static readonly TimeSpan DefaultHttpTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AttachmentUploadHttpTimeout = TimeSpan.FromMinutes(10);
    private readonly object stateGate = new();
    private readonly HttpClient httpClient;
    private readonly HttpClient attachmentUploadHttpClient;
    private readonly IWindowsAttachmentOpenService? attachmentOpenService;
    private Task? disposeTask;
    private bool detachedForProcessExit;

    internal ClientAccountComposition(
        HttpClient httpClient,
        ClientAccountShellCoordinator coordinator)
        : this(httpClient, httpClient, coordinator)
    {
    }

    internal ClientAccountComposition(
        HttpClient httpClient,
        HttpClient attachmentUploadHttpClient,
        ClientAccountShellCoordinator coordinator,
        IWindowsAttachmentOpenService? attachmentOpenService = null)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.attachmentUploadHttpClient = attachmentUploadHttpClient ??
            throw new ArgumentNullException(nameof(attachmentUploadHttpClient));
        Coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        this.attachmentOpenService = attachmentOpenService;
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
        var attachmentCacheRoot = ResolveChildDirectory(normalizedRoot, "cache");
        var httpClient = CreateDefaultHttpClient();
        var attachmentUploadHttpClient = CreateAttachmentUploadHttpClient();
        IWindowsAttachmentOpenService? attachmentOpenService = null;
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
            attachmentOpenService = new WindowsAttachmentOpenService(
                loggerFactory.CreateLogger<WindowsAttachmentOpenService>());
            var runtimeFactory = new ClientAccountRuntimeFactory(
                httpClient: httpClient,
                accountDataRootDirectory: accountRoot,
                loggerFactory: loggerFactory,
                createRealtimeConnection: null,
                notificationAttention: notificationAttention,
                attachmentUploadHttpClient: attachmentUploadHttpClient,
                attachmentCacheRootDirectory: attachmentCacheRoot,
                attachmentOpenService: attachmentOpenService);
            var coordinator = new ClientAccountShellCoordinator(
                authentication,
                runtimeFactory,
                activationRouter,
                loggerFactory.CreateLogger<ClientAccountShellCoordinator>());
            return new ClientAccountComposition(
                httpClient,
                attachmentUploadHttpClient,
                coordinator,
                attachmentOpenService);
        }
        catch
        {
            if (attachmentOpenService is not null)
            {
                _ = attachmentOpenService.DisposeAsync();
            }
            httpClient.Dispose();
            attachmentUploadHttpClient.Dispose();
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
            if (attachmentOpenService is not null)
            {
                await attachmentOpenService.DisposeAsync().ConfigureAwait(false);
            }
            httpClient.Dispose();
            DisposeAttachmentUploadHttpClient();
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            httpClient.Dispose();
            DisposeAttachmentUploadHttpClient();
            if (attachmentOpenService is not null)
            {
                await attachmentOpenService.DisposeAsync().ConfigureAwait(false);
            }
            completion.TrySetException(exception);
        }
    }

    public override string ToString() =>
        $"{nameof(ClientAccountComposition)} {{ DataRoot = [REDACTED], " +
        "HttpClient = [REDACTED], Coordinator = [REDACTED] }";

    internal static HttpClient CreateDefaultHttpClient() => new()
    {
        Timeout = DefaultHttpTimeout,
    };

    internal static HttpClient CreateAttachmentUploadHttpClient() =>
        new(CreateAttachmentUploadHttpHandler())
        {
            Timeout = AttachmentUploadHttpTimeout,
        };

    internal static SocketsHttpHandler CreateAttachmentUploadHttpHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
    };

    private void DisposeAttachmentUploadHttpClient()
    {
        if (!ReferenceEquals(httpClient, attachmentUploadHttpClient))
        {
            attachmentUploadHttpClient.Dispose();
        }
    }

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
